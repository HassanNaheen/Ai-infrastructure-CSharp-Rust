using AiInfra.Core.Bitmaps;

namespace AiInfra.Core.Tests;

[Collection(nameof(NativeMemoryCollection))]
public class BitmapTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    [InlineData(70_001)]
    public void Bulk_ops_match_bool_arrays(int length)
    {
        var random = new Random(length);
        bool[] x = RandomBools(random, length), y = RandomBools(random, length);
        using Bitmap a = FromBools(x), b = FromBools(y), result = new(length);

        result.And(a, b);
        AssertBits(result, i => x[i] && y[i]);
        result.Or(a, b);
        AssertBits(result, i => x[i] || y[i]);
        result.AndNot(a, b);
        AssertBits(result, i => x[i] && !y[i]);
        result.Not(a);
        AssertBits(result, i => !x[i]);

        result.SetAll(true);
        Assert.Equal(length, result.CountSet());
    }

    [Fact]
    public void Enumerates_set_bits_in_order()
    {
        using var bitmap = new Bitmap(200);
        int[] expected = [0, 5, 63, 64, 130, 199];
        foreach (int i in expected)
            bitmap[i] = true;
        bitmap[5] = false;
        bitmap[5] = true;

        var actual = new List<int>();
        foreach (int i in bitmap.EnumerateSetBits())
            actual.Add(i);
        Assert.Equal(expected, actual);
        Assert.Throws<ArgumentOutOfRangeException>(() => bitmap[200]);
    }

    private static bool[] RandomBools(Random random, int length)
    {
        var bools = new bool[length];
        for (int i = 0; i < length; i++)
            bools[i] = random.Next(2) == 1;
        return bools;
    }

    private static Bitmap FromBools(bool[] bools)
    {
        var bitmap = new Bitmap(bools.Length);
        for (int i = 0; i < bools.Length; i++)
            bitmap[i] = bools[i];
        return bitmap;
    }

    private static void AssertBits(Bitmap bitmap, Func<int, bool> expected)
    {
        long count = 0;
        for (int i = 0; i < bitmap.Length; i++)
        {
            Assert.Equal(expected(i), bitmap[i]);
            count += expected(i) ? 1 : 0;
        }
        Assert.Equal(count, bitmap.CountSet()); // padding bits must stay clear
    }
}
