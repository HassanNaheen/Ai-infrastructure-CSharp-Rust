using AiInfra.Core;

namespace AiInfra.Core.Tests;

public class SimdTests
{
    // Lengths straddle every vector width and the 255-iteration accumulator flush.
    public static TheoryData<int> Lengths => new() { 0, 1, 15, 16, 17, 63, 64, 65, 255, 256, 1000, 4096 * 4 + 3, 255 * 64 * 4 + 77, 1 << 20 };

    [Theory]
    [MemberData(nameof(Lengths))]
    public void CountEqual_matches_reference(int length)
    {
        var random = new Random(length);
        byte[] data = new byte[length];
        random.NextBytes(data);
        foreach (byte value in new byte[] { 0, 42, 255 })
            Assert.Equal(SimdReference.CountEqual(data, value), Simd.CountEqual(data, value));
    }

    [Fact]
    public void CountEqual_all_matching_does_not_overflow_lanes()
    {
        byte[] data = new byte[300_000];
        data.AsSpan().Fill(7);
        Assert.Equal(data.Length, Simd.CountEqual(data, 7));
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void ClassifyBytes_matches_reference(int length)
    {
        var random = new Random(length + 1);
        byte[] input = new byte[length];
        byte[] low = new byte[16];
        byte[] high = new byte[16];
        random.NextBytes(input);
        random.NextBytes(low);
        random.NextBytes(high);

        byte[] expected = new byte[length];
        byte[] actual = new byte[length];
        SimdReference.ClassifyBytes(input, expected, low, high);
        Simd.ClassifyBytes(input, actual, low, high);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void CompressStore_matches_reference(int length)
    {
        var random = new Random(length + 2);
        int[] values = new int[length];
        for (int i = 0; i < length; i++)
            values[i] = random.Next();
        byte[] mask = new byte[(length + 7) / 8];
        random.NextBytes(mask);
        if (mask.Length > 2)
        {
            mask[0] = 0xFF; // exercise the all-set and none-set fast paths
            mask[1] = 0x00;
        }

        int[] expected = new int[length];
        int[] actual = new int[length];
        int expectedCount = SimdReference.CompressStore<int>(values, mask, expected);
        int actualCount = Simd.CompressStore<int>(values, mask, actual);
        Assert.Equal(expectedCount, actualCount);
        Assert.Equal(expected.AsSpan(0, expectedCount).ToArray(), actual.AsSpan(0, actualCount).ToArray());
    }

    [Fact]
    public void CompressStore_exact_size_destination()
    {
        long[] values = [10, 11, 12, 13, 14, 15, 16, 17, 18];
        byte[] mask = [0b1010_0101, 0b1];
        long[] destination = new long[5];
        Assert.Equal(5, Simd.CompressStore<long>(values, mask, destination));
        Assert.Equal([10L, 12, 15, 17, 18], destination);
    }

    [Fact]
    public void PopCount_and_prefix_sum()
    {
        Assert.Equal(64 + 1 + 3, Simd.PopCount([ulong.MaxValue, 1, 0b111, 0, 0]));

        int[] offsets = new int[4];
        Simd.ExclusivePrefixSum([3, 0, 5], offsets);
        Assert.Equal([0, 3, 3, 8], offsets);
    }
}
