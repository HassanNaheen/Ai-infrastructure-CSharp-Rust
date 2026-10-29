using System.Runtime.ExceptionServices;

namespace AiInfra.Core.Parallelism;

/// <summary>Work for one morsel: the half-open range [<paramref name="start"/>, <paramref name="end"/>).</summary>
/// <remarks>
/// Implement on a struct so the call is devirtualised and inlined. Each worker runs on its own
/// copy of the struct; use <paramref name="workerIndex"/> to address per-worker state.
/// </remarks>
public interface IMorselBody
{
    void Execute(long start, long end, int workerIndex);
}

/// <summary>
/// Fixed pool of dedicated worker threads running data-parallel loops over morsels.
/// </summary>
/// <remarks>
/// Workers claim morsels from a shared atomic counter (dynamic self-scheduling), so fast workers
/// naturally take more. The calling thread participates as worker 0. One loop runs at a time;
/// a loop started from inside a worker runs inline on that worker. Work-stealing deques are
/// deferred until nested task graphs need them (docs/02-core.md).
/// </remarks>
public sealed class WorkScheduler : IDisposable
{
    private static readonly Lazy<WorkScheduler> s_shared = new(() => new WorkScheduler(Environment.ProcessorCount));

    [ThreadStatic]
    private static WorkScheduler? t_currentScheduler;

    private readonly Thread[] _threads;
    private readonly SemaphoreSlim[] _wakeups;
    private readonly ManualResetEventSlim _allDone = new(false, spinCount: 2000);
    private readonly Lock _dispatchLock = new();
    private JobBase? _job;
    private int _pending;
    private volatile bool _disposed;

    public WorkScheduler(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        WorkerCount = workerCount;
        _threads = new Thread[workerCount - 1];
        _wakeups = new SemaphoreSlim[workerCount - 1];
        for (int i = 0; i < _threads.Length; i++)
        {
            _wakeups[i] = new SemaphoreSlim(0);
            int workerIndex = i + 1;
            _threads[i] = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"AiInfra worker {workerIndex}",
            };
            _threads[i].Start();
        }
    }

    /// <summary>Process-wide scheduler with one worker per logical core.</summary>
    public static WorkScheduler Shared => s_shared.Value;

    public int WorkerCount { get; }

    /// <summary>
    /// Runs <paramref name="body"/> over [0, <paramref name="count"/>) in morsels of
    /// <paramref name="morselSize"/> and returns when all have completed. The first exception
    /// thrown by any morsel stops further morsels from starting and is rethrown here.
    /// </summary>
    public void ParallelFor<TBody>(long count, int morselSize, TBody body) where TBody : struct, IMorselBody
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(morselSize, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count == 0)
            return;

        if (count <= morselSize || _threads.Length == 0 || t_currentScheduler == this)
        {
            // Too small to split, single worker, or nested inside one of our workers.
            for (long start = 0; start < count; start += morselSize)
                body.Execute(start, Math.Min(start + morselSize, count), 0);
            return;
        }

        Job<TBody> job = Job<TBody>.Rent();
        job.Initialize(count, morselSize, body);
        lock (_dispatchLock)
        {
            _job = job;
            _allDone.Reset();
            Volatile.Write(ref _pending, _threads.Length);
            foreach (SemaphoreSlim wakeup in _wakeups)
                wakeup.Release();

            WorkerScope(0, job);
            _allDone.Wait();
            _job = null;
        }

        Exception? error = job.Error;
        Job<TBody>.Return(job);
        if (error is not null)
            ExceptionDispatchInfo.Throw(error);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (SemaphoreSlim wakeup in _wakeups)
            wakeup.Release();
        foreach (Thread thread in _threads)
            thread.Join();
        foreach (SemaphoreSlim wakeup in _wakeups)
            wakeup.Dispose();
        _allDone.Dispose();
    }

    private void WorkerLoop(int workerIndex)
    {
        SemaphoreSlim wakeup = _wakeups[workerIndex - 1];
        while (true)
        {
            wakeup.Wait();
            if (_disposed)
                return;
            WorkerScope(workerIndex, Volatile.Read(ref _job)!);
            if (Interlocked.Decrement(ref _pending) == 0)
                _allDone.Set();
        }
    }

    private void WorkerScope(int workerIndex, JobBase job)
    {
        WorkScheduler? previous = t_currentScheduler;
        t_currentScheduler = this;
        try
        {
            job.Work(workerIndex);
        }
        finally
        {
            t_currentScheduler = previous;
        }
    }

    private abstract class JobBase
    {
        private long _next;
        private long _count;
        private int _morselSize;
        private Exception? _error;

        public Exception? Error => _error;

        protected void Reset(long count, int morselSize)
        {
            _next = 0;
            _count = count;
            _morselSize = morselSize;
            _error = null;
        }

        protected abstract void Run(long start, long end, int workerIndex);

        public void Work(int workerIndex)
        {
            while (true)
            {
                long start = Interlocked.Add(ref _next, _morselSize) - _morselSize;
                if (start >= _count)
                    return;
                try
                {
                    Run(start, Math.Min(start + _morselSize, _count), workerIndex);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _error, ex, null);
                    Volatile.Write(ref _next, _count);
                    return;
                }
            }
        }
    }

    private sealed class Job<TBody> : JobBase where TBody : struct, IMorselBody
    {
        // One cached job per body type keeps steady-state loops allocation-free.
        private static Job<TBody>? s_cached;

        private TBody _body;

        public static Job<TBody> Rent() => Interlocked.Exchange(ref s_cached, null) ?? new Job<TBody>();

        public static void Return(Job<TBody> job)
        {
            job._body = default;
            Volatile.Write(ref s_cached, job);
        }

        public void Initialize(long count, int morselSize, TBody body)
        {
            Reset(count, morselSize);
            _body = body;
        }

        protected override void Run(long start, long end, int workerIndex)
        {
            // Copy per call so concurrent workers never share mutable struct state.
            TBody body = _body;
            body.Execute(start, end, workerIndex);
        }
    }
}
