using AiInfra.Core.Hashing;
using BenchmarkDotNet.Attributes;

namespace AiInfra.Benchmarks;

/// <summary>Insert 100,000 byte-string keys. Rust: bench/rust/core-bench map_build.</summary>
[MemoryDiagnoser]
public class MapBuildBench
{
    private byte[] _words = [];
    private int[] _offsets = [];

    [GlobalSetup]
    public void Setup()
    {
        (_words, _offsets) = Datasets.ReadLines("words.txt");
        int expected = Datasets.Manifest("map_build").GetProperty("expected_count").GetInt32();
        if (BytesMap().Count != expected)
            throw new InvalidOperationException("BytesMap build produced the wrong count.");
    }

    /// <summary>Grows from empty, like the Rust side's <c>HashMap::new()</c>.</summary>
    [Benchmark]
    public BytesMap<int> BytesMap()
    {
        var map = new BytesMap<int>();
        int keys = _offsets.Length - 1;
        for (int i = 0; i < keys; i++)
            map.TryAdd(_words.AsSpan(_offsets[i], _offsets[i + 1] - _offsets[i] - 1), i);
        return map;
    }
}

/// <summary>1,000,000 lookups (~90% hits) in a 100,000-key map. Rust: bench/rust/core-bench map_lookup.</summary>
[MemoryDiagnoser]
public class MapLookupBench
{
    private BytesMap<int> _map = null!;
    private byte[] _probes = [];
    private int[] _probeOffsets = [];

    [GlobalSetup]
    public void Setup()
    {
        var build = new MapBuildBench();
        build.Setup();
        _map = build.BytesMap();
        (_probes, _probeOffsets) = Datasets.ReadLines("probes.txt");

        var manifest = Datasets.Manifest("map_lookup");
        long expected = manifest.GetProperty("expected_hits").GetInt64() << 40 | manifest.GetProperty("expected_value_sum").GetInt64();
        if (BytesMap() != expected)
            throw new InvalidOperationException("BytesMap lookup returned the wrong checksum.");
    }

    /// <summary>Returns hits &lt;&lt; 40 | sum of values found, checked against the manifest.</summary>
    [Benchmark]
    public long BytesMap()
    {
        long hits = 0, sum = 0;
        int probes = _probeOffsets.Length - 1;
        for (int i = 0; i < probes; i++)
        {
            int start = _probeOffsets[i];
            if (_map.TryGetValue(_probes.AsSpan(start, _probeOffsets[i + 1] - start - 1), out int value))
            {
                hits++;
                sum += value;
            }
        }
        return hits << 40 | sum;
    }
}
