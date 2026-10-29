using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using AiInfra.Core.Memory;

namespace AiInfra.Core.Bitmaps;

/// <summary>
/// Fixed-length bit set in Arrow layout (LSB-first, 64-byte aligned, padded to whole words).
/// Bits past <see cref="Length"/> are always zero, so counts and bulk ops need no masking.
/// </summary>
public sealed class Bitmap : IDisposable
{
    private readonly NativeBuffer<ulong> _words;

    public Bitmap(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        _words = NativeBuffer<ulong>.Allocate(WordCount(length), zeroed: true);
    }

    public int Length { get; }

    public Span<ulong> Words => _words.Span;

    /// <summary>The bitmap as Arrow bytes (little-endian words).</summary>
    public Span<byte> Bytes => MemoryMarshal.AsBytes(_words.Span);

    public bool this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Length, nameof(index));
            return (Words[index >> 6] & (1UL << index)) != 0;
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Length, nameof(index));
            ref ulong word = ref Words[index >> 6];
            word = value ? word | (1UL << index) : word & ~(1UL << index);
        }
    }

    public long CountSet() => Simd.PopCount(Words);

    public void SetAll(bool value)
    {
        Words.Fill(value ? ulong.MaxValue : 0);
        if (value)
            ClearPadding();
    }

    /// <summary><c>this = a &amp; b</c>.</summary>
    public void And(Bitmap a, Bitmap b) => Apply<AndOp>(a, b);

    /// <summary><c>this = a | b</c>.</summary>
    public void Or(Bitmap a, Bitmap b) => Apply<OrOp>(a, b);

    /// <summary><c>this = a &amp; ~b</c>.</summary>
    public void AndNot(Bitmap a, Bitmap b) => Apply<AndNotOp>(a, b);

    /// <summary><c>this = ~a</c>.</summary>
    public void Not(Bitmap a)
    {
        RequireSameLength(a);
        BitwiseKernels.Binary<NotFirstOp>(a.Words, a.Words, Words);
        ClearPadding();
    }

    public SetBitEnumerator EnumerateSetBits() => new(Words);

    public void Dispose() => _words.Dispose();

    private void Apply<TOp>(Bitmap a, Bitmap b) where TOp : struct, IBitwiseOp
    {
        RequireSameLength(a);
        RequireSameLength(b);
        BitwiseKernels.Binary<TOp>(a.Words, b.Words, Words);
    }

    private void RequireSameLength(Bitmap other)
    {
        if (other.Length != Length)
            throw new ArgumentException("Bitmaps must have the same length.");
    }

    private void ClearPadding()
    {
        int tail = Length & 63;
        if (tail != 0)
            Words[^1] &= (1UL << tail) - 1;
    }

    private static int WordCount(int bits) => (bits + 63) >> 6;

    /// <summary>Enumerates indices of set bits using trailing-zero counts.</summary>
    public ref struct SetBitEnumerator
    {
        private readonly ReadOnlySpan<ulong> _words;
        private int _wordIndex;
        private ulong _current;

        internal SetBitEnumerator(ReadOnlySpan<ulong> words)
        {
            _words = words;
            _wordIndex = -1;
            _current = 0;
            Current = -1;
        }

        public int Current { get; private set; }

        public readonly SetBitEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_current == 0)
            {
                if (++_wordIndex >= _words.Length)
                    return false;
                _current = _words[_wordIndex];
            }
            Current = (_wordIndex << 6) + BitOperations.TrailingZeroCount(_current);
            _current &= _current - 1;
            return true;
        }
    }
}

/// <summary>A bitwise operation, written once per vector width so kernels are monomorphised.</summary>
public interface IBitwiseOp
{
    static abstract ulong Apply(ulong a, ulong b);
    static abstract Vector128<ulong> Apply(Vector128<ulong> a, Vector128<ulong> b);
    static abstract Vector256<ulong> Apply(Vector256<ulong> a, Vector256<ulong> b);
    static abstract Vector512<ulong> Apply(Vector512<ulong> a, Vector512<ulong> b);
}

public struct AndOp : IBitwiseOp
{
    public static ulong Apply(ulong a, ulong b) => a & b;
    public static Vector128<ulong> Apply(Vector128<ulong> a, Vector128<ulong> b) => a & b;
    public static Vector256<ulong> Apply(Vector256<ulong> a, Vector256<ulong> b) => a & b;
    public static Vector512<ulong> Apply(Vector512<ulong> a, Vector512<ulong> b) => a & b;
}

public struct OrOp : IBitwiseOp
{
    public static ulong Apply(ulong a, ulong b) => a | b;
    public static Vector128<ulong> Apply(Vector128<ulong> a, Vector128<ulong> b) => a | b;
    public static Vector256<ulong> Apply(Vector256<ulong> a, Vector256<ulong> b) => a | b;
    public static Vector512<ulong> Apply(Vector512<ulong> a, Vector512<ulong> b) => a | b;
}

public struct AndNotOp : IBitwiseOp
{
    public static ulong Apply(ulong a, ulong b) => a & ~b;
    public static Vector128<ulong> Apply(Vector128<ulong> a, Vector128<ulong> b) => Vector128.AndNot(a, b);
    public static Vector256<ulong> Apply(Vector256<ulong> a, Vector256<ulong> b) => Vector256.AndNot(a, b);
    public static Vector512<ulong> Apply(Vector512<ulong> a, Vector512<ulong> b) => Vector512.AndNot(a, b);
}

/// <summary><c>~a</c>; the second operand is ignored. Lets unary NOT reuse the binary kernel.</summary>
public struct NotFirstOp : IBitwiseOp
{
    public static ulong Apply(ulong a, ulong b) => ~a;
    public static Vector128<ulong> Apply(Vector128<ulong> a, Vector128<ulong> b) => ~a;
    public static Vector256<ulong> Apply(Vector256<ulong> a, Vector256<ulong> b) => ~a;
    public static Vector512<ulong> Apply(Vector512<ulong> a, Vector512<ulong> b) => ~a;
}

public static class BitwiseKernels
{
    /// <summary><c>destination[i] = TOp(a[i], b[i])</c> at the widest available vector width.</summary>
    public static void Binary<TOp>(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> destination)
        where TOp : struct, IBitwiseOp
    {
        int length = destination.Length;
        if (a.Length < length || b.Length < length)
            throw new ArgumentException("Inputs are shorter than the destination.");

        ref ulong pa = ref MemoryMarshal.GetReference(a);
        ref ulong pb = ref MemoryMarshal.GetReference(b);
        ref ulong pd = ref MemoryMarshal.GetReference(destination);
        nuint n = (nuint)length;
        nuint i = 0;

        if (Vector512.IsHardwareAccelerated)
        {
            for (; n - i >= (nuint)Vector512<ulong>.Count; i += (nuint)Vector512<ulong>.Count)
                TOp.Apply(Vector512.LoadUnsafe(ref pa, i), Vector512.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            for (; n - i >= (nuint)Vector256<ulong>.Count; i += (nuint)Vector256<ulong>.Count)
                TOp.Apply(Vector256.LoadUnsafe(ref pa, i), Vector256.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; n - i >= (nuint)Vector128<ulong>.Count; i += (nuint)Vector128<ulong>.Count)
                TOp.Apply(Vector128.LoadUnsafe(ref pa, i), Vector128.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
        }

        for (; i < n; i++)
            Unsafe.Add(ref pd, i) = TOp.Apply(Unsafe.Add(ref pa, i), Unsafe.Add(ref pb, i));
    }
}
