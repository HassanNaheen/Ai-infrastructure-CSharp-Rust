using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace AiInfra.Core;

/// <summary>
/// Width-agnostic SIMD helpers. Each picks <see cref="Vector512"/>, <see cref="Vector256"/>,
/// <see cref="Vector128"/> (NEON on arm64) or scalar code; the checks are JIT/AOT constants.
/// Every helper has a scalar reference in <see cref="SimdReference"/> used by the tests.
/// </summary>
public static class Simd
{
    /// <summary>Counts the bytes in <paramref name="data"/> equal to <paramref name="value"/>.</summary>
    public static long CountEqual(ReadOnlySpan<byte> data, byte value)
    {
        ref byte start = ref MemoryMarshal.GetReference(data);
        nuint length = (nuint)data.Length;
        nuint i = 0;
        long total = 0;

        if (Vector512.IsHardwareAccelerated && length >= (nuint)Vector512<byte>.Count)
            total = CountEqual512(ref start, length, value, ref i);
        else if (Vector256.IsHardwareAccelerated && length >= (nuint)Vector256<byte>.Count)
            total = CountEqual256(ref start, length, value, ref i);
        else if (Vector128.IsHardwareAccelerated && length >= (nuint)Vector128<byte>.Count)
            total = CountEqual128(ref start, length, value, ref i);

        for (; i < length; i++)
            total += Unsafe.Add(ref start, i) == value ? 1 : 0;
        return total;
    }

    // Each kernel keeps four byte-lane accumulators. A match compares to 0xFF (= -1), so
    // subtracting the comparison adds one. Lanes are flushed before they can overflow (255).
    // The loop index is copied out of the ref parameter: Native AOT otherwise keeps it in
    // memory and every iteration pays a store/load round trip (3x slower than the JIT).
    private static long CountEqual128(ref byte start, nuint length, byte value, ref nuint index)
    {
        nuint i = index; // keep the induction variable in a register (AOT spills ref params)
        const int width = 16;
        Vector128<byte> target = Vector128.Create(value);
        long total = 0;
        while (length - i >= width)
        {
            Vector128<byte> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
            nuint groups = Math.Min((length - i) / (4 * width), 255);
            nuint end = i + groups * 4 * width;
            for (; i < end; i += 4 * width)
            {
                acc0 -= Vector128.Equals(Vector128.LoadUnsafe(ref start, i), target);
                acc1 -= Vector128.Equals(Vector128.LoadUnsafe(ref start, i + width), target);
                acc2 -= Vector128.Equals(Vector128.LoadUnsafe(ref start, i + 2 * width), target);
                acc3 -= Vector128.Equals(Vector128.LoadUnsafe(ref start, i + 3 * width), target);
            }
            if (groups == 0)
            {
                // Fewer than four vectors left: single-vector steps.
                for (; length - i >= width; i += width)
                    acc0 -= Vector128.Equals(Vector128.LoadUnsafe(ref start, i), target);
            }
            total += Sum(acc0) + Sum(acc1) + Sum(acc2) + Sum(acc3);
        }
        index = i;
        return total;

        static long Sum(Vector128<byte> v)
        {
            (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(v);
            return Vector128.Sum(lo + hi);
        }
    }

    private static long CountEqual256(ref byte start, nuint length, byte value, ref nuint index)
    {
        nuint i = index; // keep the induction variable in a register (AOT spills ref params)
        const int width = 32;
        Vector256<byte> target = Vector256.Create(value);
        long total = 0;
        while (length - i >= width)
        {
            Vector256<byte> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
            nuint groups = Math.Min((length - i) / (4 * width), 255);
            nuint end = i + groups * 4 * width;
            for (; i < end; i += 4 * width)
            {
                acc0 -= Vector256.Equals(Vector256.LoadUnsafe(ref start, i), target);
                acc1 -= Vector256.Equals(Vector256.LoadUnsafe(ref start, i + width), target);
                acc2 -= Vector256.Equals(Vector256.LoadUnsafe(ref start, i + 2 * width), target);
                acc3 -= Vector256.Equals(Vector256.LoadUnsafe(ref start, i + 3 * width), target);
            }
            if (groups == 0)
            {
                for (; length - i >= width; i += width)
                    acc0 -= Vector256.Equals(Vector256.LoadUnsafe(ref start, i), target);
            }
            total += Sum(acc0) + Sum(acc1) + Sum(acc2) + Sum(acc3);
        }
        index = i;
        return total;

        static long Sum(Vector256<byte> v)
        {
            (Vector256<ushort> lo, Vector256<ushort> hi) = Vector256.Widen(v);
            return Vector256.Sum(lo + hi);
        }
    }

    private static long CountEqual512(ref byte start, nuint length, byte value, ref nuint index)
    {
        nuint i = index; // keep the induction variable in a register (AOT spills ref params)
        const int width = 64;
        Vector512<byte> target = Vector512.Create(value);
        long total = 0;
        while (length - i >= width)
        {
            Vector512<byte> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
            nuint groups = Math.Min((length - i) / (4 * width), 255);
            nuint end = i + groups * 4 * width;
            for (; i < end; i += 4 * width)
            {
                acc0 -= Vector512.Equals(Vector512.LoadUnsafe(ref start, i), target);
                acc1 -= Vector512.Equals(Vector512.LoadUnsafe(ref start, i + width), target);
                acc2 -= Vector512.Equals(Vector512.LoadUnsafe(ref start, i + 2 * width), target);
                acc3 -= Vector512.Equals(Vector512.LoadUnsafe(ref start, i + 3 * width), target);
            }
            if (groups == 0)
            {
                for (; length - i >= width; i += width)
                    acc0 -= Vector512.Equals(Vector512.LoadUnsafe(ref start, i), target);
            }
            total += Sum(acc0) + Sum(acc1) + Sum(acc2) + Sum(acc3);
        }
        index = i;
        return total;

        static long Sum(Vector512<byte> v)
        {
            (Vector512<ushort> lo, Vector512<ushort> hi) = Vector512.Widen(v);
            (Vector512<uint> a, Vector512<uint> b) = Vector512.Widen(lo + hi);
            return Vector512.Sum(a + b);
        }
    }

    /// <summary>Counts set bits across <paramref name="words"/>.</summary>
    public static long PopCount(ReadOnlySpan<ulong> words)
    {
        ref ulong start = ref MemoryMarshal.GetReference(words);
        int length = words.Length;
        long c0 = 0, c1 = 0, c2 = 0, c3 = 0;
        int i = 0;
        for (; i + 4 <= length; i += 4)
        {
            c0 += BitOperations.PopCount(Unsafe.Add(ref start, i));
            c1 += BitOperations.PopCount(Unsafe.Add(ref start, i + 1));
            c2 += BitOperations.PopCount(Unsafe.Add(ref start, i + 2));
            c3 += BitOperations.PopCount(Unsafe.Add(ref start, i + 3));
        }
        for (; i < length; i++)
            c0 += BitOperations.PopCount(Unsafe.Add(ref start, i));
        return c0 + c1 + c2 + c3;
    }

    /// <summary>
    /// Classifies bytes with a nibble lookup:
    /// <c>output[i] = lowTable[input[i] &amp; 15] &amp; highTable[input[i] &gt;&gt; 4]</c>.
    /// Maps to <c>TBL</c> on arm64 and <c>PSHUFB</c> on x64.
    /// </summary>
    public static void ClassifyBytes(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<byte> lowTable, ReadOnlySpan<byte> highTable)
    {
        if (lowTable.Length != 16 || highTable.Length != 16)
            throw new ArgumentException("Nibble tables must have exactly 16 entries.");
        ArgumentOutOfRangeException.ThrowIfLessThan(output.Length, input.Length, nameof(output));

        ref byte src = ref MemoryMarshal.GetReference(input);
        ref byte dst = ref MemoryMarshal.GetReference(output);
        nuint length = (nuint)input.Length;
        nuint i = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> low = Vector128.Create(lowTable);
            Vector128<byte> high = Vector128.Create(highTable);
            Vector128<byte> nibble = Vector128.Create((byte)0x0F);
            for (; length - i >= 16; i += 16)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref src, i);
                Vector128<byte> lo = Vector128.ShuffleNative(low, v & nibble);
                Vector128<byte> hi = Vector128.ShuffleNative(high, Vector128.ShiftRightLogical(v, 4));
                (lo & hi).StoreUnsafe(ref dst, i);
            }
        }

        for (; i < length; i++)
        {
            byte b = Unsafe.Add(ref src, i);
            Unsafe.Add(ref dst, i) = (byte)(lowTable[b & 0xF] & highTable[b >> 4]);
        }
    }

    /// <summary>
    /// Writes the values whose bit is set in <paramref name="mask"/> (LSB-first, Arrow order)
    /// to <paramref name="destination"/>, in order. Returns the number written.
    /// </summary>
    public static int CompressStore<T>(ReadOnlySpan<T> values, ReadOnlySpan<byte> mask, Span<T> destination)
        where T : unmanaged
    {
        if (mask.Length < (values.Length + 7) / 8)
            throw new ArgumentException("Mask is shorter than the value count.", nameof(mask));

        ref T src = ref MemoryMarshal.GetReference(values);
        ref T dst = ref MemoryMarshal.GetReference(destination);
        ref byte lut = ref MemoryMarshal.GetArrayDataReference(CompressLut.Indices);
        int fullBytes = values.Length / 8;
        int written = 0;

        for (int b = 0; b < fullBytes; b++)
        {
            uint m = mask[b];
            if (m == 0)
                continue;
            int count = BitOperations.PopCount(m);
            if (destination.Length - written < count)
                throw new ArgumentException("Destination is too short.", nameof(destination));

            ref T block = ref Unsafe.Add(ref src, b * 8);
            if (m == 0xFF)
            {
                MemoryMarshal.CreateReadOnlySpan(ref block, 8).CopyTo(destination.Slice(written));
            }
            else if (destination.Length - written >= 8)
            {
                // Branch-free: always write 8 lanes, then advance by the popcount.
                ref byte idx = ref Unsafe.Add(ref lut, (int)m * 8);
                ref T o = ref Unsafe.Add(ref dst, written);
                Unsafe.Add(ref o, 0) = Unsafe.Add(ref block, Unsafe.Add(ref idx, 0));
                Unsafe.Add(ref o, 1) = Unsafe.Add(ref block, Unsafe.Add(ref idx, 1));
                Unsafe.Add(ref o, 2) = Unsafe.Add(ref block, Unsafe.Add(ref idx, 2));
                Unsafe.Add(ref o, 3) = Unsafe.Add(ref block, Unsafe.Add(ref idx, 3));
                Unsafe.Add(ref o, 4) = Unsafe.Add(ref block, Unsafe.Add(ref idx, 4));
                Unsafe.Add(ref o, 5) = Unsafe.Add(ref block, Unsafe.Add(ref idx, 5));
                Unsafe.Add(ref o, 6) = Unsafe.Add(ref block, Unsafe.Add(ref idx, 6));
                Unsafe.Add(ref o, 7) = Unsafe.Add(ref block, Unsafe.Add(ref idx, 7));
            }
            else
            {
                int k = written;
                for (; m != 0; m &= m - 1)
                    destination[k++] = Unsafe.Add(ref block, BitOperations.TrailingZeroCount(m));
            }
            written += count;
        }

        for (int i = fullBytes * 8; i < values.Length; i++)
        {
            if ((mask[i >> 3] & (1 << (i & 7))) != 0)
                destination[written++] = values[i];
        }
        return written;
    }

    /// <summary>Exclusive prefix sum: <c>offsets[0] = 0</c>, <c>offsets[i + 1] = offsets[i] + lengths[i]</c>.</summary>
    public static void ExclusivePrefixSum(ReadOnlySpan<int> lengths, Span<int> offsets)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offsets.Length, lengths.Length + 1, nameof(offsets));
        int running = 0;
        offsets[0] = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            running = checked(running + lengths[i]);
            offsets[i + 1] = running;
        }
    }

    private static class CompressLut
    {
        // For each 8-bit mask, the indices of its set bits, padded with zeros to 8 entries.
        public static readonly byte[] Indices = Build();

        private static byte[] Build()
        {
            var table = new byte[256 * 8];
            for (int m = 0; m < 256; m++)
            {
                int k = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((m & (1 << bit)) != 0)
                        table[m * 8 + k++] = (byte)bit;
                }
            }
            return table;
        }
    }
}

/// <summary>Plain scalar versions of the <see cref="Simd"/> helpers, used as test oracles and baselines.</summary>
public static class SimdReference
{
    public static long CountEqual(ReadOnlySpan<byte> data, byte value)
    {
        long count = 0;
        foreach (byte b in data)
        {
            if (b == value)
                count++;
        }
        return count;
    }

    public static void ClassifyBytes(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<byte> lowTable, ReadOnlySpan<byte> highTable)
    {
        for (int i = 0; i < input.Length; i++)
            output[i] = (byte)(lowTable[input[i] & 0xF] & highTable[input[i] >> 4]);
    }

    public static int CompressStore<T>(ReadOnlySpan<T> values, ReadOnlySpan<byte> mask, Span<T> destination)
        where T : unmanaged
    {
        int written = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if ((mask[i >> 3] & (1 << (i & 7))) != 0)
                destination[written++] = values[i];
        }
        return written;
    }
}
