# M0 benchmark report

Generated 2026-10-29 by `bench/compare/compare.py`. Method and parity thresholds: [07-benchmarking](../../docs/07-benchmarking.md). Ratio = C# candidate median ÷ fastest Rust median; ≤ 1.10 is parity.

| | |
|---|---|
| CPU | Apple M4 Pro |
| OS | Darwin 25.6.0 (arm64) |
| .NET SDK | 11.0.100-rc.1.26425.128 |
| rustc | rustc 1.99.1 (58cf8f9c3 2026-10-13) (Homebrew) |

## Summary

| Group | C# runtime | C# candidate | Fastest Rust | Ratio | Verdict |
|---|---|---|---|---:|---|
| count_byte | JIT | Simd 164.9 µs | bytecount 160.5 µs | 1.03 | 🟢 parity |
| count_byte | NativeAOT | Simd 158.1 µs | bytecount 160.5 µs | 0.99 | 🟢 parity |
| map_build | JIT | BytesMap 1.89 ms | hashbrown_foldhash 3.70 ms | 0.51 | 🟢 faster |
| map_build | NativeAOT | BytesMap 2.19 ms | hashbrown_foldhash 3.70 ms | 0.59 | 🟢 faster |
| map_lookup | JIT | BytesMap 19.99 ms | hashbrown_foldhash 12.43 ms | 1.61 | 🔴 gap |
| map_lookup | NativeAOT | BytesMap 17.92 ms | hashbrown_foldhash 12.43 ms | 1.44 | 🟠 investigate |

## Details

Bold = AiInfra candidate; other C# rows are baselines.

### count_byte

| Language | Runtime | Implementation | Median | GB/s | Allocated | vs fastest Rust |
|---|---|---|---:|---:|---:|---:|
| C# | NativeAOT | **Simd** | 158.1 µs | 106.09 | 0 B | 0.99 |
| Rust | release+LTO | bytecount | 160.5 µs | 104.52 | — | 1.00 |
| C# | JIT | **Simd** | 164.9 µs | 101.76 | 0 B | 1.03 |
| C# | NativeAOT | Bcl | 309.8 µs | 54.16 | 0 B | 1.93 |
| C# | JIT | Bcl | 313.3 µs | 53.56 | 0 B | 1.95 |
| Rust | release+LTO | naive_iter | 2.02 ms | 8.29 | — | 12.61 |
| C# | JIT | ScalarLoop | 4.61 ms | 3.64 | 0 B | 28.74 |
| C# | NativeAOT | ScalarLoop | 8.10 ms | 2.07 | 0 B | 50.46 |

### map_build

| Language | Runtime | Implementation | Median | M keys/s | Allocated | vs fastest Rust |
|---|---|---|---:|---:|---:|---:|
| C# | JIT | **BytesMap** | 1.89 ms | 52.82 | 8,654,294 B | 0.51 |
| C# | NativeAOT | **BytesMap** | 2.19 ms | 45.65 | 8,654,643 B | 0.59 |
| Rust | release+LTO | hashbrown_foldhash | 3.70 ms | 27.00 | — | 1.00 |
| Rust | release+LTO | hashbrown_xxh3 | 5.18 ms | 19.29 | — | 1.40 |

### map_lookup

| Language | Runtime | Implementation | Median | M lookups/s | Allocated | vs fastest Rust |
|---|---|---|---:|---:|---:|---:|
| Rust | release+LTO | hashbrown_foldhash | 12.43 ms | 80.42 | — | 1.00 |
| C# | NativeAOT | **BytesMap** | 17.92 ms | 55.82 | 0 B | 1.44 |
| C# | JIT | **BytesMap** | 19.99 ms | 50.03 | 0 B | 1.61 |
| Rust | release+LTO | hashbrown_xxh3 | 21.43 ms | 46.67 | — | 1.72 |

