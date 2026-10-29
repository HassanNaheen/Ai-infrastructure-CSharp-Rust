# 01 — Repository layout & build

```
C#-ai-infra/
  Directory.Build.props        shared settings (see 00-principles)
  global.json                  pins SDK 11.0.100-rc.1
  AiInfra.slnx
  src/
    AiInfra.Core/              shared low-level primitives            → 02-core
    AiInfra.Tokenizers/        BPE tokeniser                          → 03-tokenizers
    AiInfra.Validation/        runtime + JSON Schema tape interpreter → 04-validation
    AiInfra.Validation.Generator/  Roslyn incremental source generator
    AiInfra.Frames/            DataFrame engine                       → 05-dataframe
    AiInfra.Pkg/               PyPI installer library                 → 06-package-manager
    AiInfra.Pkg.Cli/           Native AOT executable "cpkg"
  tests/
    AiInfra.Core.Tests/        xUnit + FsCheck property tests
    AiInfra.Tokenizers.Tests/  golden-file conformance (tiktoken / HF outputs)
    AiInfra.Validation.Tests/  JSON-Schema-Test-Suite runner + generator snapshots
    AiInfra.Frames.Tests/      differential tests vs Polars (Arrow IPC fixtures)
    AiInfra.Pkg.Tests/         PEP 440/508 vectors, resolver fixtures vs uv
  bench/
    AiInfra.Benchmarks/        BenchmarkDotNet (JIT-PGO and NativeAOT jobs)
    rust/                      cargo workspace: tok-bench, validate-bench, frames-bench (criterion)
    datasets/                  download scripts + SHA-256 checksums; data is git-ignored
    compare/                   merges BDN + criterion + hyperfine JSON → report.md / report.html
  docs/
```

## Dependency rules

- `AiInfra.Core` depends on nothing but the BCL (`System.IO.Hashing` allowed).
- Component libraries depend only on `Core`. They never depend on each other.
- Test and bench projects may depend on anything, including third-party .NET baselines (`Microsoft.ML.Tokenizers`, `JsonSchema.Net`, …).
- Native dependencies (Snappy/Zstd for Parquet) are isolated behind an interface in `Frames` and loaded only when used.

## CI matrix

| Runner | Purpose |
|---|---|
| macOS arm64 (Apple Silicon) | build, tests, NEON benchmarks |
| Linux x64 (AVX2 + AVX-512) | build, tests, wide-SIMD benchmarks, `HardwareCounters` |
| Linux x64 with `DOTNET_EnableAVX512F=0` / `DOTNET_EnableHWIntrinsic=0` | verifies every SIMD fallback path |
