using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AiInfra.Core.Memory;

/// <summary>
/// Bump allocator over native chunks for short-lived scratch memory.
/// </summary>
/// <remarks>
/// Spans handed out stay valid until <see cref="Reset"/> or <see cref="Dispose"/>.
/// Not thread-safe: use one arena per worker.
/// </remarks>
public sealed unsafe class Arena : IDisposable
{
    public const int DefaultChunkSize = 64 * 1024;
    private const int ChunkAlignment = 64;
    private const int MaxChunkSize = 64 * 1024 * 1024;

    private readonly List<Chunk> _chunks = new();
    private readonly int _initialChunkSize;
    private byte* _current;
    private nuint _offset;
    private nuint _capacity;
    private long _bytesInPreviousChunks;
    private bool _disposed;

    public Arena(int initialChunkSize = DefaultChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialChunkSize, 64);
        _initialChunkSize = initialChunkSize;
    }

    /// <summary>Bytes handed out since the last reset, including alignment padding.</summary>
    public long BytesUsed => _bytesInPreviousChunks + (long)_offset;

    /// <summary>Total native bytes reserved by the arena's chunks.</summary>
    public long BytesReserved
    {
        get
        {
            long total = 0;
            foreach (Chunk chunk in _chunks)
                total += (long)chunk.Size;
            return total;
        }
    }

    /// <summary>Allocates uninitialised space for <paramref name="count"/> values.</summary>
    public Span<T> Allocate<T>(int count) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return Span<T>.Empty;
        nuint bytes = checked((nuint)count * (nuint)sizeof(T));
        return new Span<T>(AllocateBytes(bytes, (nuint)Math.Min(sizeof(T), 16)), count);
    }

    /// <summary>Copies <paramref name="source"/> into the arena.</summary>
    public Span<T> Copy<T>(ReadOnlySpan<T> source) where T : unmanaged
    {
        Span<T> destination = Allocate<T>(source.Length);
        source.CopyTo(destination);
        return destination;
    }

    /// <summary>Makes all previously allocated memory available again, keeping the largest chunk.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_chunks.Count > 1)
        {
            // Chunks grow geometrically, so the last one is the largest; keep it.
            Chunk keep = _chunks[^1];
            for (int i = 0; i < _chunks.Count - 1; i++)
                FreeChunk(_chunks[i]);
            _chunks.Clear();
            _chunks.Add(keep);
        }
        if (_chunks.Count == 1)
        {
            _current = _chunks[0].Pointer;
            _capacity = _chunks[0].Size;
        }
        _offset = 0;
        _bytesInPreviousChunks = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (Chunk chunk in _chunks)
            FreeChunk(chunk);
        _chunks.Clear();
        _current = null;
        _offset = _capacity = 0;
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* AllocateBytes(nuint bytes, nuint alignment)
    {
        nuint aligned = (_offset + alignment - 1) & ~(alignment - 1);
        if (aligned + bytes <= _capacity)
        {
            _offset = aligned + bytes;
            return _current + aligned;
        }
        return AllocateSlow(bytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte* AllocateSlow(nuint bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        nuint next = _chunks.Count == 0
            ? (nuint)_initialChunkSize
            : Math.Min(_capacity * 2, (nuint)MaxChunkSize);
        nuint size = Math.Max(next, bytes);

        var chunk = new Chunk((byte*)NativeMemory.AlignedAlloc(size, ChunkAlignment), size);
        NativeMemoryStats.OnAllocate(size);
        _chunks.Add(chunk);

        _bytesInPreviousChunks += (long)_offset;
        _current = chunk.Pointer;
        _capacity = size;
        _offset = bytes;
        return _current;
    }

    private static void FreeChunk(Chunk chunk)
    {
        NativeMemory.AlignedFree(chunk.Pointer);
        NativeMemoryStats.OnFree(chunk.Size);
    }

    ~Arena() => Dispose();

    private readonly struct Chunk(byte* pointer, nuint size)
    {
        public byte* Pointer { get; } = pointer;
        public nuint Size { get; } = size;
    }
}
