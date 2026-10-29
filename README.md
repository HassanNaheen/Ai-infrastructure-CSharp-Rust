# C# AI-Infra

Four pieces of AI/data infrastructure — a **tokeniser**, a **validation library**, a **DataFrame engine** and a **package manager** — written in deeply optimised modern C# (.NET 11), each benchmarked head-to-head against the Rust tool it replaces.

The claim being tested: with `Span<T>`, `ref struct`, hardware SIMD intrinsics, `NativeMemory` and Native AOT, C# can match Rust on CPU throughput. "Match" is defined precisely in [Benchmarking](docs/07-benchmarking.md): C# median within ±10% of Rust on the same machine and inputs.

> **Status:** M0 done — solution skeleton, `AiInfra.Core` primitives with tests, and the first C#-vs-Rust comparison ([report](bench/reports/m0-apple-m4-pro.md)). Component libraries are empty projects until their milestones ([roadmap](docs/08-roadmap.md)).

```bash
dotnet test AiInfra.slnx     # Core tests
bench/run-m0.sh              # C# (JIT + NativeAOT) vs Rust benchmarks → bench/reports/
```

## Components

| Component | Project | Rust comparator | Design doc |
|---|---|---|---|
| Shared primitives | `AiInfra.Core` | — | [02-core](docs/02-core.md) |
| Tokeniser (byte-level BPE, tiktoken + HF compatible) | `AiInfra.Tokenizers` | tiktoken-rs, HF `tokenizers` | [03-tokenizers](docs/03-tokenizers.md) |
| Validation (source-generated + JSON Schema) | `AiInfra.Validation` | jsonschema-rs, serde + garde/validator | [04-validation](docs/04-validation.md) |
| DataFrame engine (Arrow layout, lazy queries) | `AiInfra.Frames` | Polars | [05-dataframe](docs/05-dataframe.md) |
| PyPI package manager (`cpkg`) | `AiInfra.Pkg` | uv | [06-package-manager](docs/06-package-manager.md) |

## Docs

0. [Principles: how C# closes the gap with Rust](docs/00-principles.md)
1. [Repository layout & build settings](docs/01-repo-layout.md)
2. [AiInfra.Core](docs/02-core.md)
3. [Tokenisers](docs/03-tokenizers.md)
4. [Validation](docs/04-validation.md)
5. [DataFrame engine](docs/05-dataframe.md)
6. [Package manager](docs/06-package-manager.md)
7. [Benchmarking & comparison harness](docs/07-benchmarking.md)
8. [Roadmap](docs/08-roadmap.md)

## Toolchain

- .NET SDK `11.0.100-rc.1` (pinned in `global.json` once code lands)
- Rust stable via `rustup` (for comparator benchmarks)
- `hyperfine` (CLI benchmarks), `uv` / Python 3 (golden-file generation, uv comparison)

Primary dev machine is Apple M4 Pro (arm64, NEON = `Vector128`). Wide SIMD paths (`Vector256`/`Vector512`) are validated on x64 Linux with AVX2/AVX-512 in CI.
