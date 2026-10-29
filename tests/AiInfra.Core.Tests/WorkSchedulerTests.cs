using AiInfra.Core.Parallelism;

namespace AiInfra.Core.Tests;

public class WorkSchedulerTests
{
    private struct SumBody(long[] values, long[] perWorker) : IMorselBody
    {
        public void Execute(long start, long end, int workerIndex)
        {
            long sum = 0;
            for (long i = start; i < end; i++)
                sum += values[i];
            Interlocked.Add(ref perWorker[workerIndex], sum);
        }
    }

    private struct CoverBody(int[] hits) : IMorselBody
    {
        public void Execute(long start, long end, int workerIndex)
        {
            for (long i = start; i < end; i++)
                Interlocked.Increment(ref hits[i]);
        }
    }

    private struct ThrowingBody : IMorselBody
    {
        public void Execute(long start, long end, int workerIndex)
        {
            if (start >= 5000)
                throw new InvalidOperationException("boom");
        }
    }

    private struct NestedBody(WorkScheduler scheduler, int[] hits) : IMorselBody
    {
        public void Execute(long start, long end, int workerIndex) =>
            scheduler.ParallelFor(10, 1, new CoverBody(hits));
    }

    [Fact]
    public void Every_index_runs_exactly_once()
    {
        using var scheduler = new WorkScheduler(4);
        for (int round = 0; round < 50; round++)
        {
            int[] hits = new int[100_003];
            scheduler.ParallelFor(hits.Length, 1000, new CoverBody(hits));
            Assert.All(hits, h => Assert.Equal(1, h));
        }
    }

    [Fact]
    public void Sums_with_per_worker_state()
    {
        using var scheduler = new WorkScheduler(Environment.ProcessorCount);
        long[] values = new long[1_000_000];
        for (int i = 0; i < values.Length; i++)
            values[i] = i;
        long[] perWorker = new long[scheduler.WorkerCount];

        scheduler.ParallelFor(values.Length, 4096, new SumBody(values, perWorker));
        Assert.Equal((long)values.Length * (values.Length - 1) / 2, perWorker.Sum());
    }

    [Fact]
    public void Exception_is_rethrown_and_scheduler_stays_usable()
    {
        using var scheduler = new WorkScheduler(4);
        Assert.Throws<InvalidOperationException>(() => scheduler.ParallelFor(100_000, 100, new ThrowingBody()));

        int[] hits = new int[10_000];
        scheduler.ParallelFor(hits.Length, 100, new CoverBody(hits));
        Assert.All(hits, h => Assert.Equal(1, h));
    }

    [Fact]
    public void Nested_loops_run_inline_without_deadlock()
    {
        using var scheduler = new WorkScheduler(4);
        int[] hits = new int[10];
        scheduler.ParallelFor(64, 1, new NestedBody(scheduler, hits));
        Assert.All(hits, h => Assert.Equal(64, h));
    }

    [Fact]
    public void Single_worker_and_small_loops_run_inline()
    {
        using var scheduler = new WorkScheduler(1);
        int[] hits = new int[1000];
        scheduler.ParallelFor(hits.Length, 7, new CoverBody(hits));
        Assert.All(hits, h => Assert.Equal(1, h));
        scheduler.ParallelFor(0, 7, new CoverBody(hits));
    }
}
