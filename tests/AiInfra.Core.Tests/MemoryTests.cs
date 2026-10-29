using AiInfra.Core.Memory;

namespace AiInfra.Core.Tests;

// Memory tests read process-wide counters, so they must not run in parallel with each other.
[Collection(nameof(NativeMemoryCollection))]
public class MemoryTests
{
    [Fact]
    public unsafe void NativeBuffer_is_aligned_zeroed_and_freed()
    {
        long before = NativeMemoryStats.LiveBytes;
        var buffer = NativeBuffer<long>.Allocate(1000);
        Assert.Equal(0, (long)buffer.Pointer % NativeBuffer<long>.Alignment);
        Assert.All(buffer.Span.ToArray(), v => Assert.Equal(0, v));
        Assert.Equal(before + 8000, NativeMemoryStats.LiveBytes);

        buffer.Dispose();
        Assert.Equal(before, NativeMemoryStats.LiveBytes);
        Assert.Throws<ObjectDisposedException>(() => buffer.Span.Length);
    }

    [Fact]
    public void NativeBuffer_refcount_frees_on_last_release()
    {
        long before = NativeMemoryStats.LiveAllocations;
        var buffer = NativeBuffer<byte>.CopyFrom([1, 2, 3]);
        buffer.AddRef();
        buffer.Dispose();
        Assert.False(buffer.IsDisposed);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.Span.ToArray());
        buffer.Dispose();
        Assert.True(buffer.IsDisposed);
        Assert.Equal(before, NativeMemoryStats.LiveAllocations);
        Assert.Throws<ObjectDisposedException>(() => buffer.AddRef());
    }

    [Fact]
    public void NativeBuffer_zero_length()
    {
        using var buffer = NativeBuffer<int>.Allocate(0);
        Assert.Equal(0, buffer.Span.Length);
    }

    [Fact]
    public unsafe void Arena_allocations_are_aligned_distinct_and_survive_growth()
    {
        long before = NativeMemoryStats.LiveBytes;
        using (var arena = new Arena(initialChunkSize: 256))
        {
            var first = arena.Copy<byte>("hello"u8);
            Span<long> longs = arena.Allocate<long>(3);
            Assert.Equal(0, (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref longs[0]) % 8);
            longs.Fill(-1);

            // Force several new chunks, including one larger than the growth step.
            for (int i = 0; i < 100; i++)
                arena.Allocate<byte>(100).Fill((byte)i);
            Span<byte> big = arena.Allocate<byte>(10_000);
            big.Fill(9);

            Assert.True("hello"u8.SequenceEqual(first));
            Assert.All(longs.ToArray(), v => Assert.Equal(-1, v));
            Assert.True(arena.BytesUsed >= 5 + 24 + 100 * 100 + 10_000);

            long reserved = arena.BytesReserved;
            arena.Reset();
            Assert.Equal(0, arena.BytesUsed);
            Assert.True(arena.BytesReserved < reserved);
            Assert.True(arena.Copy<byte>("again"u8).SequenceEqual("again"u8));
        }
        Assert.Equal(before, NativeMemoryStats.LiveBytes);
    }
}

[CollectionDefinition(nameof(NativeMemoryCollection), DisableParallelization = true)]
public class NativeMemoryCollection;
