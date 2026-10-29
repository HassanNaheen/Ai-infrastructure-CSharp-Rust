using AiInfra.Core;
using BenchmarkDotNet.Attributes;

namespace AiInfra.Benchmarks;

/// <summary>Count occurrences of one byte value in 16 MiB. Rust: bench/rust/core-bench count_byte.</summary>
[MemoryDiagnoser]
public class CountByteBench
{
    private byte[] _data = [];
    private byte _value;
    private long _expected;

    [GlobalSetup]
    public void Setup()
    {
        _data = File.ReadAllBytes(Path.Combine(Datasets.M0, "bytes.bin"));
        var manifest = Datasets.Manifest("count_byte");
        _value = (byte)manifest.GetProperty("value").GetInt32();
        _expected = manifest.GetProperty("expected").GetInt64();

        Check(nameof(Simd), Simd());
        Check(nameof(Bcl), Bcl());
        Check(nameof(ScalarLoop), ScalarLoop());
    }

    /// <summary>AiInfra.Core hand-written SIMD kernel.</summary>
    [Benchmark]
    public long Simd() => AiInfra.Core.Simd.CountEqual(_data, _value);

    /// <summary>BCL <c>MemoryExtensions.Count</c> (vectorised in .NET 8+), for reference.</summary>
    [Benchmark]
    public long Bcl() => _data.AsSpan().Count(_value);

    /// <summary>Plain loop, to show what the JIT does without help.</summary>
    [Benchmark]
    public long ScalarLoop() => SimdReference.CountEqual(_data, _value);

    private void Check(string name, long actual)
    {
        if (actual != _expected)
            throw new InvalidOperationException($"{name} returned {actual}, expected {_expected}.");
    }
}
