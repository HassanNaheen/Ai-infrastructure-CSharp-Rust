# 08 — Roadmap

Each milestone ends with tests green and a committed comparison report (see [07-benchmarking](07-benchmarking.md)).

| # | Milestone | Exit criteria |
|---|---|---|
| M0 | Repo, `Directory.Build.props`, `global.json`, CI (macOS arm64 + Linux x64), Core primitives, benchmark harness skeleton, Rust toolchain | Core tests pass; one BenchmarkDotNet-vs-criterion sample appears in `report.md` |
| M1 | Tokeniser: tiktoken `cl100k_base` / `o200k_base` encode + decode | 100% golden-file match; ≥ tiktoken-rs single-threaded |
| M2 | Tokeniser: HF BPE models (GPT-2, Llama 3, Qwen), batch encode, offsets | Golden match for all models; batch scales near-linearly to core count |
| M3 | Validation: source generator + validate-only mode | 0 B allocated in validate-only; parity with serde + garde |
| M4 | Validation: JSON Schema instruction-tape interpreter | ≥ 95% JSON-Schema-Test-Suite (2020-12); parity with jsonschema-rs |
| M5 | Frames: Arrow memory, C Data Interface, core kernels, CSV reader | Zero-copy round trip with Polars; kernel benchmarks green |
| M6 | Frames: lazy engine, group-by, join, sort | h2oai group-by & join within ±10% of Polars |
| M7 | Pkg: Simple API client, PEP 440/508, PubGrub resolver | Resolutions identical to uv on the fixture set |
| M8 | Pkg: cache, clonefile/reflink install, AOT CLI | Warm install within ±10% of uv; binary < 20 MB |
| M9 | Stretch: Frames Parquet + streaming; WordPiece / Unigram tokenisers | — |

Order rationale: Core first because every component depends on it; tokeniser next because it is the smallest component that exercises UTF-8, hashing, SIMD and parallelism end to end, so it validates Core early.

## Before M0 starts

- Install Rust: `rustup` (stable), and `brew install hyperfine`.
- Confirm .NET 11 RC SDK is selected by `global.json` (`dotnet --version` → `11.0.100-rc.1…`).
- Provision the x64 AVX-512 Linux runner.

## Known risks

| Risk | Component | Mitigation |
|---|---|---|
| Unicode-category pre-tokenisation slow on non-ASCII text | Tokeniser | per-script fast paths after profiling |
| `pattern` regex dialect differences (ECMA-262 vs .NET) | Validation | documented, tested; dialect translation layer |
| GC pauses when callers materialise many objects | All | span/struct APIs; documented, not eliminated |
| Native AOT startup slower than Rust | Pkg | measured and reported honestly |
| NEON lacks native compress / movemask | Core, Frames | lookup-table emulation; x64 results reported separately |
| Polars' maturity across all query shapes | Frames | parity claim scoped to the fixed benchmark suite |
