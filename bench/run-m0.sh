#!/usr/bin/env bash
# Runs the M0 comparison end to end: data, C# (JIT + NativeAOT), Rust, report.
# Suites run one after another, never concurrently. Close other heavy apps first.
set -euo pipefail
cd "$(dirname "$0")"

python3 datasets/generate_m0.py

rm -rf results/csharp
dotnet run -c Release --project AiInfra.Benchmarks -- --filter '*' "$@"

(cd rust && cargo bench --bench m0 -- --noplot)

python3 compare/compare.py --milestone m0
