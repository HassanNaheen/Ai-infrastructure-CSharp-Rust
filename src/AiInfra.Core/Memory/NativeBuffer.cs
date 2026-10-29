using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AiInfra.Core.Memory;

/// <summary>
/// A 64-byte-aligned block of native memory holding <typeparamref name="T"/> values,
/// shared through reference counting (the Rust <c>Arc</c> equivalent).
/// </summary>
/// <remarks>
/// The creator holds one reference. <see cref="AddRef"/> takes another; every reference is
/// released with <see cref="Dispose"/>. Memory is freed when the count reaches zero, or by the
/// finalizer if the buffer becomes unreachable without being disposed.
/// </remarks>
public sealed unsafe class NativeBuffer<T> : IDisposable where T : unmanaged
{
    public const int Alignment = 64;

    private nint _address;
    private readonly int _length;
    private readonly nuint _byteCount;
    private int _refCount;

    private NativeBuffer(T* pointer, int length, nuint byteCount)
    {
        _address = (nint)pointer;
        _length = length;
        _byteCount = byteCount;
        _refCount = 1;
    }

    /// <summary>Allocates a buffer of <paramref name="length"/> elements.</summary>
    /// <param name="zeroed">When false the contents are left uninitialised.</param>
    public static NativeBuffer<T> Allocate(int length, bool zeroed = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        nuint byteCount = checked((nuint)length * (nuint)sizeof(T));
        T* pointer = null;
        if (byteCount != 0)
        {
            pointer = (T*)NativeMemory.AlignedAlloc(byteCount, Alignment);
            if (zeroed)
                NativeMemory.Clear(pointer, byteCount);
            NativeMemoryStats.OnAllocate(byteCount);
        }
        return new NativeBuffer<T>(pointer, length, byteCount);
    }

    /// <summary>Allocates a buffer holding a copy of <paramref name="source"/>.</summary>
    public static NativeBuffer<T> CopyFrom(ReadOnlySpan<T> source)
    {
        NativeBuffer<T> buffer = Allocate(source.Length, zeroed: false);
        source.CopyTo(buffer.Span);
        return buffer;
    }

    public int Length => _length;

    public bool IsDisposed => Volatile.Read(ref _refCount) <= 0;

    public Span<T> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return new Span<T>((T*)_address, _length);
        }
    }

    internal T* Pointer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (T*)_address;
    }

    /// <summary>Takes an additional reference. Each call must be matched by a <see cref="Dispose"/>.</summary>
    public NativeBuffer<T> AddRef()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _refCount);
            ObjectDisposedException.ThrowIf(current <= 0, this);
        }
        while (Interlocked.CompareExchange(ref _refCount, current + 1, current) != current);
        return this;
    }

    /// <summary>Releases one reference; frees the memory when it was the last one.</summary>
    public void Dispose()
    {
        int remaining = Interlocked.Decrement(ref _refCount);
        if (remaining == 0)
        {
            Free();
            GC.SuppressFinalize(this);
        }
        else if (remaining < 0)
        {
            throw new ObjectDisposedException(GetType().Name, "Released more times than referenced.");
        }
    }

    ~NativeBuffer() => Free();

    private void Free()
    {
        T* pointer = (T*)Interlocked.Exchange(ref _address, 0);
        if (pointer != null)
        {
            NativeMemory.AlignedFree(pointer);
            NativeMemoryStats.OnFree(_byteCount);
        }
    }
}

/// <summary>Process-wide counters for native allocations, used for leak checks.</summary>
public static class NativeMemoryStats
{
    private static long s_liveBytes;
    private static long s_liveAllocations;

    public static long LiveBytes => Volatile.Read(ref s_liveBytes);

    public static long LiveAllocations => Volatile.Read(ref s_liveAllocations);

    internal static void OnAllocate(nuint bytes)
    {
        Interlocked.Add(ref s_liveBytes, (long)bytes);
        Interlocked.Increment(ref s_liveAllocations);
    }

    internal static void OnFree(nuint bytes)
    {
        Interlocked.Add(ref s_liveBytes, -(long)bytes);
        Interlocked.Decrement(ref s_liveAllocations);
    }
}
