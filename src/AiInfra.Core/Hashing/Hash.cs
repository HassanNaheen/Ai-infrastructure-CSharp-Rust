using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace AiInfra.Core.Hashing;

/// <summary>
/// Fast non-cryptographic hashes. Seeds are fixed: these are not meant to resist
/// hash-flooding by adversarial input (see docs/02-core.md).
/// </summary>
public static class Hash
{
    private const ulong Seed = 0x243F_6A88_85A3_08D3;
    private const ulong Multiplier = 0x9E37_79B9_7F4A_7C15;

    /// <summary>XXH3-64 of <paramref name="data"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bytes(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);

    /// <summary>Folded-multiply mix of an integer key (the scheme used by aHash/foldhash).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Int(ulong value)
    {
        ulong high = Math.BigMul(value ^ Seed, Multiplier, out ulong low);
        return high ^ low;
    }
}
