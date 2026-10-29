using System.Text;
using AiInfra.Core.Hashing;

namespace AiInfra.Core.Tests;

public class BytesMapTests
{
    [Fact]
    public void Matches_dictionary_under_random_workload()
    {
        var random = new Random(1234);
        var map = new BytesMap<int>();
        var reference = new Dictionary<string, int>();

        for (int op = 0; op < 200_000; op++)
        {
            // Small alphabet and lengths force many collisions on short keys, plus the empty key.
            int length = random.Next(0, 6);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = (char)('a' + random.Next(4));
            string key = new(chars);
            byte[] bytes = Encoding.UTF8.GetBytes(key);

            if (random.Next(2) == 0)
            {
                bool added = map.TryAdd(bytes, op);
                Assert.Equal(reference.TryAdd(key, op), added);
            }
            else
            {
                bool found = map.TryGetValue(bytes, out int value);
                Assert.Equal(reference.TryGetValue(key, out int expected), found);
                if (found)
                    Assert.Equal(expected, value);
            }
        }
        Assert.Equal(reference.Count, map.Count);
    }

    [Fact]
    public void Grows_and_keeps_insertion_order_indices()
    {
        var map = new BytesMap<long>();
        for (int i = 0; i < 100_000; i++)
            Assert.True(map.TryAdd(Encoding.ASCII.GetBytes($"key-{i}"), i * 10L));

        Assert.Equal(100_000, map.Count);
        for (int i = 0; i < 100_000; i += 997)
        {
            byte[] key = Encoding.ASCII.GetBytes($"key-{i}");
            Assert.Equal(i, map.IndexOf(key));
            Assert.True(map.GetKey(i).SequenceEqual(key));
            Assert.Equal(i * 10L, map.GetValueRef(i));
        }
        Assert.Equal(-1, map.IndexOf("missing"u8));
    }

    [Fact]
    public void GetValueRefOrAddDefault_supports_aggregation()
    {
        var map = new BytesMap<int>(capacity: 2);
        foreach (string word in "a b a c b a".Split(' '))
            map.GetValueRefOrAddDefault(Encoding.ASCII.GetBytes(word), out _)++;

        Assert.True(map.TryGetValue("a"u8, out int a));
        Assert.Equal(3, a);
        Assert.Equal(3, map.Count);

        map.Clear();
        Assert.Equal(0, map.Count);
        Assert.False(map.ContainsKey("a"u8));
        Assert.True(map.TryAdd("a"u8, 1));
    }

    [Fact]
    public void Lookup_does_not_allocate()
    {
        var map = new BytesMap<int>();
        for (int i = 0; i < 1000; i++)
            map.TryAdd(Encoding.ASCII.GetBytes(i.ToString()), i);
        byte[] probe = Encoding.ASCII.GetBytes("512");
        map.TryGetValue(probe, out _); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int i = 0; i < 1000; i++)
        {
            map.TryGetValue(probe, out int v);
            sum += v;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(512_000, sum);
    }

    [Fact]
    public void Int_hash_spreads_sequential_keys()
    {
        var seen = new HashSet<ulong>();
        for (ulong i = 0; i < 10_000; i++)
            Assert.True(seen.Add(Hash.Int(i) & 0xFFFF_FFFF));
    }
}
