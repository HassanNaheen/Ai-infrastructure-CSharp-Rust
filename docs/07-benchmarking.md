# 07 — Benchmarking & comparison harness

The whole project rests on these numbers, so the method is fixed up front.

## Tools

| Side | Tool | Configuration |
|---|---|---|
| C# libraries | BenchmarkDotNet 0.16 (preview: first version with .NET 11 monikers) | `[MemoryDiagnoser]`; `[HardwareCounters]` on Linux; two jobs: **.NET 11 JIT (Tiered PGO)** and **NativeAOT** with `IlcInstructionSet=native` |
| Rust libraries | criterion | `--release`, `lto = "fat"`, `codegen-units = 1`, `target-cpu=native` |
| CLIs (`cpkg` vs `uv`) | hyperfine | ≥ 3 warmups, ≥ 10 runs, `--prepare` to reset caches for cold runs |

C# gets no special flags beyond the shared build props and the AOT instruction-set setting that mirrors `target-cpu=native`; both sides run on the same CPU, same OS, same power settings (plugged in, no other load).

## Inputs

- `bench/datasets/` holds download scripts and SHA-256 checksums only; data is git-ignored.
- Both languages read the **same files** (text corpora, JSON documents, Parquet/IPC tables, requirement files).
- DataFrame inputs are loaded outside the timed region on both sides.

## Metrics

- Throughput: median with 99% confidence interval (MB/s, rows/s, tokens/s, docs/s)
- Tail latency: p99 where per-operation timing makes sense
- Allocated bytes per operation (C#) / heap allocations (Rust, via `dhat` when investigated)
- Peak RSS (`/usr/bin/time -l` on macOS, `-v` on Linux)
- Startup time and binary size (CLI)

## Parity definition

For each benchmark: `ratio = C# median / Rust median` (time-based).

| Ratio | Verdict |
|---|---|
| ≤ 1.10 | **parity** (green) |
| 1.10 – 1.50 | amber — investigate |
| > 1.50 | red — design problem |

The headline claim ("C# matches Rust") holds for a component only if every benchmark in its core suite is green on **both** reference machines.

## Reference machines

1. Apple M4 Pro, macOS, arm64 (NEON / `Vector128`).
2. Linux x64 with AVX-512 (wide SIMD paths, hardware counters).

## Report

`bench/compare` reads BenchmarkDotNet JSON, criterion `estimates.json` and hyperfine JSON, joins them by benchmark id, and writes `report.md` (and optionally a static HTML page) with per-component tables, ratios, verdicts and machine/toolchain versions. Reports are committed per milestone so regressions are visible over time.

## Running

```bash
bench/run-m0.sh
```

Generates the data (`bench/datasets/generate_m0.py`), runs BenchmarkDotNet (both jobs), then criterion, then writes `bench/reports/m0-<cpu>.md`. Pass `--job jit` or `--job aot` to run only one C# job.

## Rules against fooling ourselves

- Outputs are checked for equality before any timing is accepted (a fast wrong answer fails the run).
- Same algorithmic work on both sides: e.g. tokeniser benchmarks disable caches on both or enable on both.
- No cherry-picking: the suite for each milestone is fixed in this doc before implementation starts.
