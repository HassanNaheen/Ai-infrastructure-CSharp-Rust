using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace AiInfra.Core.Hashing;

/// <summary>
/// Insert-only hash map keyed by byte strings, laid out like a SwissTable.
/// </summary>
/// <remarks>
/// <para>
/// Keys are copied back to back into one blob. Each entry gets a stable index in insertion
/// order (0, 1, 2, …), which callers can use as an id (token id, group id). Slots live in
/// 16-wide groups; each slot has a control byte holding 7 bits of the hash, or
/// <c>0x80</c> when empty. A lookup compares a whole group of control bytes in one
/// <see cref="Vector128"/> operation.
/// </para>
/// <para>Lookups allocate nothing. Removal is not supported (no tombstones needed).</para>
/// </remarks>
public sealed class BytesMap<TValue>
{
    private const int GroupSize = 16;
    private const byte EmptyControl = 0x80;

    private byte[] _control = [];
    private int[] _slots = [];
    private int _groupMask;
    private int _growthLimit;

    private byte[] _keys;
    private int _keysLength;
    private int[] _keyOffsets;
    private int[] _keyLengths;
    private ulong[] _hashes;
    private TValue[] _values;
    private int _count;

    public BytesMap(int capacity = 0, int expectedKeyBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        int entries = Math.Max(capacity, 4);
        _keys = new byte[Math.Max(expectedKeyBytes, 64)];
        _keyOffsets = new int[entries];
        _keyLengths = new int[entries];
        _hashes = new ulong[entries];
        _values = new TValue[entries];
        Rehash(GroupsFor(capacity));
    }

    public int Count => _count;

    /// <summary>Returns the entry index of <paramref name="key"/>, or -1.</summary>
    public int IndexOf(ReadOnlySpan<byte> key) => Find(key, Hash.Bytes(key), out _);

    public bool ContainsKey(ReadOnlySpan<byte> key) => IndexOf(key) >= 0;

    public bool TryGetValue(ReadOnlySpan<byte> key, out TValue value)
    {
        int index = Find(key, Hash.Bytes(key), out _);
        if (index >= 0)
        {
            value = _values[index];
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Adds the key if absent. Returns false (and changes nothing) if it exists.</summary>
    public bool TryAdd(ReadOnlySpan<byte> key, TValue value)
    {
        ref TValue slot = ref GetValueRefOrAddDefault(key, out bool exists);
        if (exists)
            return false;
        slot = value;
        return true;
    }

    /// <summary>
    /// Returns a reference to the value for <paramref name="key"/>, inserting a default value
    /// first if it is absent. The reference is invalidated by the next insertion.
    /// </summary>
    public ref TValue GetValueRefOrAddDefault(ReadOnlySpan<byte> key, out bool exists)
    {
        ulong hash = Hash.Bytes(key);
        int index = Find(key, hash, out int emptySlot);
        if (index >= 0)
        {
            exists = true;
            return ref _values[index];
        }

        exists = false;
        if (_count >= _growthLimit)
        {
            Rehash((_groupMask + 1) * 2);
            Find(key, hash, out emptySlot);
        }
        index = AppendEntry(key, hash);
        SetSlot(emptySlot, hash, index);
        return ref _values[index];
    }

    /// <summary>Key of the entry at <paramref name="index"/> (insertion order).</summary>
    public ReadOnlySpan<byte> GetKey(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
        return _keys.AsSpan(_keyOffsets[index], _keyLengths[index]);
    }

    /// <summary>Value of the entry at <paramref name="index"/>, by reference.</summary>
    public ref TValue GetValueRef(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
        return ref _values[index];
    }

    public void Clear()
    {
        _control.AsSpan().Fill(EmptyControl);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            Array.Clear(_values, 0, _count);
        _count = 0;
        _keysLength = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Find(ReadOnlySpan<byte> key, ulong hash, out int emptySlot)
    {
        ref byte control = ref MemoryMarshal.GetArrayDataReference(_control);
        ref int slots = ref MemoryMarshal.GetArrayDataReference(_slots);
        Vector128<byte> tag = Vector128.Create(Tag(hash));
        int group = (int)hash & _groupMask;

        for (int probe = 1; ; probe++)
        {
            int baseSlot = group * GroupSize;
            Vector128<byte> controls = Vector128.LoadUnsafe(ref control, (nuint)baseSlot);

            uint matches = Vector128.Equals(controls, tag).ExtractMostSignificantBits();
            while (matches != 0)
            {
                int entry = Unsafe.Add(ref slots, baseSlot + BitOperations.TrailingZeroCount(matches));
                if (_hashes[entry] == hash && key.SequenceEqual(_keys.AsSpan(_keyOffsets[entry], _keyLengths[entry])))
                {
                    emptySlot = -1;
                    return entry;
                }
                matches &= matches - 1;
            }

            uint empties = controls.ExtractMostSignificantBits();
            if (empties != 0)
            {
                emptySlot = baseSlot + BitOperations.TrailingZeroCount(empties);
                return -1;
            }

            // Triangular probing visits every group when the group count is a power of two.
            group = (group + probe) & _groupMask;
        }
    }

    private int AppendEntry(ReadOnlySpan<byte> key, ulong hash)
    {
        int index = _count;
        if (index == _hashes.Length)
        {
            int size = index * 2;
            Array.Resize(ref _keyOffsets, size);
            Array.Resize(ref _keyLengths, size);
            Array.Resize(ref _hashes, size);
            Array.Resize(ref _values, size);
        }
        int needed = checked(_keysLength + key.Length);
        if (needed > _keys.Length)
            Array.Resize(ref _keys, Math.Max(needed, _keys.Length * 2));

        key.CopyTo(_keys.AsSpan(_keysLength));
        _keyOffsets[index] = _keysLength;
        _keyLengths[index] = key.Length;
        _hashes[index] = hash;
        _values[index] = default!;
        _keysLength = needed;
        _count = index + 1;
        return index;
    }

    private void SetSlot(int slot, ulong hash, int entry)
    {
        _control[slot] = Tag(hash);
        _slots[slot] = entry;
    }

    private void Rehash(int groups)
    {
        _control = new byte[groups * GroupSize];
        _control.AsSpan().Fill(EmptyControl);
        _slots = new int[groups * GroupSize];
        _groupMask = groups - 1;
        _growthLimit = groups * GroupSize / 8 * 7;

        for (int entry = 0; entry < _count; entry++)
        {
            ulong hash = _hashes[entry];
            int group = (int)hash & _groupMask;
            for (int probe = 1; ; probe++)
            {
                int baseSlot = group * GroupSize;
                uint empties = Vector128.LoadUnsafe(ref _control[baseSlot]).ExtractMostSignificantBits();
                if (empties != 0)
                {
                    SetSlot(baseSlot + BitOperations.TrailingZeroCount(empties), hash, entry);
                    break;
                }
                group = (group + probe) & _groupMask;
            }
        }
    }

    // Top 7 bits of the hash; the group index uses the low bits, so the two are independent.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Tag(ulong hash) => (byte)(hash >> 57);

    private static int GroupsFor(int capacity)
    {
        long slots = Math.Max((long)capacity * 8 / 7 + 1, GroupSize);
        return (int)BitOperations.RoundUpToPowerOf2((ulong)((slots + GroupSize - 1) / GroupSize));
    }
}
