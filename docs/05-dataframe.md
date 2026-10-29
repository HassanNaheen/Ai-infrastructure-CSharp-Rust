# 05 — DataFrame engine (`AiInfra.Frames`)

**Rust comparator:** [Polars](https://github.com/pola-rs/polars) (Rust crate, same version pinned in `bench/rust`).
**Informational:** DuckDB, `Microsoft.Data.Analysis`.

## Memory model: Apache Arrow layout

- A column chunk is an immutable `Array` owning refcounted `NativeBuffer`s (64-byte aligned, see [02-core](02-core.md)):
  - validity bitmap + values buffer for primitives;
  - strings as Arrow offsets + data **and** Polars-style **string views** (16-byte entry: length + 4-byte prefix + inline data or buffer/offset) — views are the default because they make filter/gather cheap;
  - dictionary / categorical; list and struct types in a later milestone.
- `ChunkedArray` → `Series` (named, typed) → `DataFrame` (schema + series). Slicing is zero-copy; mutation is copy-on-write.
- **Arrow C Data Interface**: import/export of `ArrowArray` / `ArrowSchema` structs, with release callbacks implemented via `[UnmanagedCallersOnly]` function pointers. Gives zero-copy exchange with Polars, pyarrow, DuckDB — and lets benchmarks feed the identical buffers to both engines.

### Containing the unsafety

Raw pointers and `NativeMemory` are confined to `AiInfra.Frames.Memory` and the kernel files. The public API is safe: `Span`-returning accessors, typed `Series<T>`, no pointers. This is how the doc's "you bypass C#'s safety" concern is contained — the same way Polars confines `unsafe` to its arrow crate.

## Compute kernels

Generic over `T : unmanaged, INumber<T>` and struct operator types (`struct Add : IBinaryOp<T>`), so each (type, op) pair is monomorphised.

| Kernel family | Notes |
|---|---|
| Arithmetic, comparison | vectorised; comparisons produce bitmaps directly |
| `filter` | `Simd.CompressStore`; selection-vector variant when selectivity is low |
| `take` / gather | AVX2/AVX-512 gather where it wins, scalar unrolled otherwise |
| `sum/min/max/mean/count` | null-aware via validity mask; pairwise/Kahan option for floats |
| `cast` | including string ↔ number parsing on UTF-8 |
| String predicates | `starts_with` from view prefix without touching the data buffer; `contains` via `IndexOf` on spans |

### Sort
- LSD radix sort for integers and floats (sign-flip key normalisation), 8- or 11-bit digits chosen by size.
- Multi-column sort: row-encode keys into normalised, memcmp-comparable byte strings, then radix/pdqsort.
- `argsort` returns `uint` indices; nulls first/last handled in the key encoding.

### Group-by
- Hash aggregation in `IntMap` / `BytesMap`; per-worker partial tables partitioned by hash high bits, merged per partition in parallel.
- Small-cardinality fast path: keys that are small integers or dictionary codes use direct-index arrays.
- Aggregates are struct accumulators (`ISumState<T>` etc.) updated in tight loops.

### Join
- Partitioned hash join: inner, left, semi, anti; build side chosen by size.
- Sort-merge join when both sides are already sorted on the key.

## Query layer

Polars-style lazy API:

```csharp
var result = df.Lazy()
    .Filter(Col("a") > 5)
    .GroupBy("k")
    .Agg(Col("v").Sum().Alias("total"), Col("v").Mean())
    .Sort("total", descending: true)
    .Collect();
```

- Expression tree → logical plan → optimiser: predicate and projection pushdown, constant folding, common-subexpression elimination, simplification of boolean expressions.
- Physical plan executed **morsel-driven** (~64 K-row morsels) on `WorkScheduler`, pipelines broken at hash-build / sort.
- Streaming (out-of-core) execution is a later milestone (M9).
- Eager API (`df.Filter(...)`) is a thin wrapper over lazy + `Collect`.

## IO

- **CSV:** SIMD structural-character scanning (quote/delimiter/newline bitmaps à la simdjson), file split into chunks parsed in parallel with quote-state fix-up, type inference on a sample, direct writes into Arrow buffers.
- **Arrow IPC** read/write (file + stream).
- **Parquet** (M9): Thrift footer, PLAIN / RLE / dictionary / delta decoders, Snappy & Zstd via native libraries, predicate pushdown using row-group statistics.

## Correctness

- Differential tests: a Python/Rust script runs the same query in Polars and writes Arrow IPC; tests compare results exactly (float tolerance where order-dependent).
- Property tests on every kernel: nulls, empty arrays, all-null, overflow behaviour, NaN ordering.
- C Data Interface round-trip tests with pyarrow and Polars.

## Benchmarks

| Benchmark | Data |
|---|---|
| h2oai db-benchmark group-by (q1–q10) | 1e7 and 1e8 rows |
| h2oai db-benchmark join (q1–q5) | 1e7 rows |
| TPC-H Q1, Q6 | SF1, SF10 |
| CSV parse | GB/s on a 5 GB file |
| Sort single / multi-column | 1e8 rows |
| Individual kernels | filter, sum, gather, cast |

Inputs are read from identical Parquet/IPC files by both engines; load time is measured separately from query time. Target: within ±10% of Polars on group-by and join.

## Risks

- Polars has years of tuning; matching it across all queries is ambitious. Success is defined on the db-benchmark suite first.
- Memory pressure: native allocations are invisible to the GC; we call `GC.AddMemoryPressure` and expose an explicit memory budget.
