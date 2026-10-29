# 00 — Principles: how C# closes the gap with Rust

Rust's performance edge in this kind of infrastructure comes from a handful of properties. Each has a C# counter-measure, and every component in this repo is expected to use it. Where the gap cannot be fully closed, the docs say so.

## Parity table

| Rust advantage | C# counter-measure |
|---|---|
| Native UTF-8 (`&str`, `&[u8]`) | All hot APIs take `ReadOnlySpan<byte>`. `string` overloads transcode **once** with `Utf8.FromUtf16` (SIMD, multi-GB/s) into a pooled buffer. Never round-trip UTF-8 ↔ UTF-16 inside a pipeline. |
| Zero-cost generics (monomorphisation) | Generic kernels over **struct** type parameters plus `static abstract` interface members (`IKernel<T>`, `INumber<T>`). The JIT and Native AOT specialise code per struct type, exactly like Rust generics. |
| No garbage collector | Hot paths allocate nothing: `stackalloc` (≤ 1 KB), `ArrayPool<T>`, arenas, `NativeMemory.AlignedAlloc`. Enforced by BenchmarkDotNet `[MemoryDiagnoser]` reporting 0 B. |
| No bounds checks where proven safe | `MemoryMarshal.GetReference` + `Unsafe.Add` in audited inner loops only; `[SkipLocalsInit]` assembly-wide. Everything else keeps bounds checks. |
| Portable SIMD | Width-agnostic helpers in Core: `Vector512` → `Vector256` → `Vector128` (NEON) → scalar, selected via `IsHardwareAccelerated` checks that the JIT/AOT compiler constant-folds. |
| Fearless parallelism | Morsel/chunk parallelism on a custom work-stealing scheduler (no `Task` allocation per item), sharing only immutable buffers. |
| Small static binaries, fast start | Native AOT, `InvariantGlobalization`, trimming, `UseSystemResourceKeys`, System.Text.Json source generation, zero reflection. |

## Shared build settings

`Directory.Build.props` applies to every project:

- `TargetFramework` = `net11.0`, `LangVersion` = `preview` only if a feature requires it
- `AllowUnsafeBlocks`, `Nullable=enable`, `IsAotCompatible=true`
- `TieredPGO=true` for JIT runs; `PublishAot=true` for the CLI and AOT benchmark jobs
- In `src/`, all warnings are errors (including the trim/AOT analyzers) and `[module: SkipLocalsInit]` is applied (`src/Directory.Build.props`)

## Coding rules for hot paths

- No LINQ, lambdas/closures, boxing, `params object[]`, `string.Format`, or interface dispatch on non-struct types.
- `sealed` classes; `readonly struct` passed by `in`; `ref struct` for views over buffers.
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` only on tiny helpers; measure before adding.
- Unsafe code lives in clearly named files/namespaces (`*.Unsafe.cs`, `Frames.Memory`) and is covered by tests that run under `DOTNET_GCStress` and with bounds-checked debug variants.
- Inner-loop induction variables are locals, never `ref` parameters or fields: Native AOT keeps a `ref` index in memory and pays a store/load per iteration (found in M0: 3× slower `CountEqual` under AOT than JIT until fixed).
- Every public hot API has a caller-supplied-buffer form (`Span<T> destination`) and returns counts / `OperationStatus` rather than allocating results.

## Known limits (stated honestly)

- **Startup:** Native AOT startup is ~5–10 ms slower than an equivalent Rust binary. Matters only for the CLI.
- **User-materialised objects:** if callers turn results into many managed objects, the GC cost is theirs; the libraries offer span/struct APIs to avoid it but cannot eliminate it.
- **NEON:** no native `movemask` or compress instruction on arm64; emulated with lookup tables, so some kernels are relatively slower on Apple Silicon than on AVX-512.
- **Regex:** .NET `Regex` is UTF-16 only; components that need regex on hot paths use hand-written scanners instead.
