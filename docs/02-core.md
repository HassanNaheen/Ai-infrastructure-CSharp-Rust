# 02 — AiInfra.Core: shared low-level primitives

Everything the four components share lives here, so each optimisation is written, tested and benchmarked once.

## Status (M0)

| Primitive | State |
|---|---|
| `NativeBuffer<T>`, `NativeMemoryStats`, `Arena` | done |
| `Simd`: `CountEqual`, `PopCount`, `ClassifyBytes`, `CompressStore`, `ExclusivePrefixSum` | done (`CompressStore` is the branch-free 8-lane LUT version on all targets; AVX-512 `VPCOMPRESS` lands with the M5 filter kernel) |
| `Hash`, `BytesMap<V>` | done |
| `Bitmap`, `BitwiseKernels` | done |
| `WorkScheduler` | done, with a shared atomic morsel counter instead of per-worker deques (below) |
| `IntMap<K,V>` | deferred to M5 (first user: group-by) |
| `Utf8Text` + Unicode category tables | deferred to M1 (first user: pre-tokeniser) |
| `IndexOfAnyByte` | dropped: the BCL's `SearchValues<byte>` already does this with SIMD |

## Memory

### `NativeBuffer<T>` (`where T : unmanaged`)
- Allocated with `NativeMemory.AlignedAlloc`, **64-byte aligned** (cache line; Arrow requirement).
- Reference-counted with `Interlocked` (the Rust `Arc` equivalent) so immutable column buffers can be shared across threads and slices without copying.
- Finalizer fallback releases memory if `Dispose` is missed; `NativeMemoryStats` counts live native bytes and allocations so tests can assert nothing leaked.
- Exposes `Span<T>` / `ReadOnlySpan<T>` views; raw pointers only via `internal` members.

### `Arena`
- Bump allocator over a list of native chunks (default 64 KB, grows geometrically).
- `Reset()` frees everything at once; used for per-request scratch: tokeniser batches, validator unescaped strings and error paths, query-execution intermediates.
- Not thread-safe by design; one arena per worker.

## SIMD (`Simd` static class)

Width-agnostic helpers that pick `Vector512` → `Vector256` → `Vector128` → scalar at JIT/AOT time:

| Helper | Use |
|---|---|
| `IndexOfAnyByte(span, table)` | tokeniser character-class scanning, CSV structural chars |
| `ClassifyBytes(span, lut, dst)` | nibble-LUT classification (`Vector128.Shuffle` / `vqtbl1q_u8`) |
| `CompressStore(values, mask, dst)` | DataFrame `filter`: AVX-512 `VPCOMPRESS`, AVX2/NEON shuffle-LUT emulation |
| `MoveMask(vector)` | NEON emulation via narrowing shift (`ExtractMostSignificantBits` on x64) |
| `PrefixSum`, `CountTrue`, `PopCount` | offsets, selection vectors, bitmaps |

Each helper has a scalar reference implementation; property tests compare every width against it.

## Hashing & tables

- **Hashes:** `XxHash3` (System.IO.Hashing) for byte keys; FxHash-style multiply-rotate for integers (non-cryptographic, fast; DoS resistance is not a goal for these workloads, documented).
- **`BytesMap<V>`:** open-addressing hash table keyed by byte slices.
  - Keys stored back-to-back in one contiguous blob; entries are `(offset, length, hash, value)`.
  - SwissTable-style 16-byte control groups probed with `Vector128` equality → bitmask.
  - Lookup by `ReadOnlySpan<byte>` with zero allocation.
  - Used by the tokeniser vocab, string interning, group-by on strings.
- **`IntMap<K, V>`:** same layout specialised for `unmanaged` integer keys.

## Bitmaps

`Bitmap`: validity and selection bitmaps (LSB-first, Arrow-compatible, padding bits kept zero) with SIMD AND/OR/AND-NOT/NOT, popcount, and iteration of set bits via `TrailingZeroCount`. Bulk ops go through `BitwiseKernels.Binary<TOp>`, one struct per operation, so every op gets its own monomorphised vector loop.

## Parallelism

`WorkScheduler`:
- Fixed pool of dedicated threads (one per core by default); the calling thread joins in as worker 0.
- `ParallelFor(count, morselSize, TBody body)` where `TBody : struct, IMorselBody` — the struct body means no delegate allocation and full inlining. Each worker runs on its own copy of the body.
- Workers claim morsels from a shared atomic counter (dynamic self-scheduling). For flat data-parallel loops this balances load as well as work stealing does, with far less code. Chase-Lev deques come in when M6 needs nested task graphs.
- One loop at a time per scheduler; a loop started from inside a worker runs inline (no deadlock).
- The first exception stops new morsels and is rethrown to the caller.
- Per-worker state slots (arena, caches) accessed by worker index, no `ThreadLocal<T>` overhead.

## UTF-8 text

`Utf8Text` (`readonly ref struct`): view over UTF-8 bytes with validation (`Utf8.IsValid`), `Rune` enumeration, ASCII fast checks, and generated two-level Unicode category tables (shared with the tokeniser pre-tokenisers).

## Testing

- Property-style tests (seeded random inputs, lengths straddling every vector width) for every SIMD helper against its scalar reference in `SimdReference`. CI also runs the suite with `DOTNET_EnableHWIntrinsic=0` to cover the scalar paths.
- `BytesMap` fuzzed against `Dictionary<byte[], V>` with a byte-array comparer.
- Leak checks: memory tests assert `NativeMemoryStats` returns to its starting value.
