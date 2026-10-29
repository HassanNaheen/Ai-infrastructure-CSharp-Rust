# 03 — Tokenisers (`AiInfra.Tokenizers`)

**Rust comparators:** [`tiktoken-rs`](https://github.com/zurawiki/tiktoken-rs), HuggingFace [`tokenizers`](https://github.com/huggingface/tokenizers) (Rust crate).
**.NET baseline:** `Microsoft.ML.Tokenizers`.

## Scope

- Byte-level BPE compatible with tiktoken encodings: `cl100k_base`, `o200k_base`.
- HF `tokenizer.json` BPE models: GPT-2, Llama 3, Qwen.
- Later (M9): WordPiece (BERT), Unigram (SentencePiece).

## The UTF-16 problem, and the answer

.NET strings are UTF-16; BPE vocabularies are byte sequences. The design keeps the whole pipeline in **UTF-8 bytes**:

- Primary API: `int Encode(ReadOnlySpan<byte> utf8, Span<int> destination)` (returns count, or `OperationStatus.DestinationTooSmall`).
- Convenience: `Encode(string)` transcodes **once** with `Utf8.FromUtf16` into an arena buffer, then calls the byte API. No further UTF-16 work.
- Offsets reported in bytes; converted to UTF-16 indices only when the caller asks.

## Pipeline

1. **Special tokens.** Split input on special tokens (`<|endoftext|>`, …) with an Aho-Corasick automaton over bytes, honouring `allowed_special` / `disallowed_special` semantics exactly like tiktoken.
2. **Pre-tokenisation.** tiktoken and HF use regexes (Rust `fancy-regex`, itself a known bottleneck). We replace them with **hand-written DFA scanners**, one per known pattern (cl100k, o200k, GPT-2, Llama 3):
   - ASCII fast path classifies 16–64 bytes at a time with SIMD nibble lookup tables (`Simd.ClassifyBytes`) into letter / digit / space / newline / punctuation classes, then walks class runs.
   - Non-ASCII bytes fall back to `Rune` decoding + generated Unicode general-category table (`\p{L}`, `\p{N}`, …) from Core.
   - Contractions (`'s`, `'t`, …), number grouping (`\p{N}{1,3}`) and whitespace-lookahead rules (`\s+(?!\S)`) are encoded as explicit states.
   - Unknown HF patterns fall back to the .NET `Regex` source generator (`NonBacktracking`) on a UTF-16 copy — correct but slower; logged as a slow path.
3. **BPE merge**, per pre-token piece:
   - (a) piece cache hit (`BytesMap` piece → token span, per-worker, bounded) → copy tokens;
   - (b) whole piece is a vocab entry → one token;
   - (c) short piece (< ~128 bytes): tiktoken's linear algorithm — `stackalloc` array of `(start, rank)` parts, repeatedly merge the minimum rank;
   - (d) long piece: min-heap of candidate merges + doubly linked list over a `stackalloc`/arena array, O(n log n).
   - Rank lookups go to `BytesMap<int>` with slices of the input — zero allocation.
4. **Decode.** All token byte strings stored in one blob with an offsets table; `Decode(ReadOnlySpan<int>, Span<byte>)` is a gather-copy loop. `DecodeToString` transcodes once.
5. **Batch.** `EncodeBatch(ReadOnlySpan<ReadOnlyMemory<byte>>, …)` distributes documents over `WorkScheduler`; each worker owns its arena and piece cache, so no locks.
6. **Offsets / alignment (HF compat).** Optional mode records byte offsets per token; mapping to `char` offsets is computed lazily.

## Loading

- `.tiktoken` files: base64 token + rank per line; parsed with `Base64.DecodeFromUtf8` directly into the vocab blob.
- `tokenizer.json`: streamed with `Utf8JsonReader`; byte-level BPE "Ġ"-style byte mapping applied at load time so the runtime never sees it.
- Optional compiled format: a single binary file (blob + offsets + hash table) loaded with `MemoryMappedFile` → near-zero load time.

## API sketch

```csharp
var enc = BpeTokenizer.FromTiktoken("o200k_base.tiktoken");
Span<int> ids = stackalloc int[256];
int n = enc.Encode("hello world"u8, ids);
int count = enc.CountTokens(utf8Text);          // no output buffer at all
```

## Correctness

- Golden files: a multilingual corpus (enwik8 slice, CJK, Arabic, emoji, source code, pathological whitespace) encoded by Python `tiktoken` / `tokenizers` via `uv run`; tests assert identical IDs for every model.
- Property tests: `Decode(Encode(x)) == x` for arbitrary valid UTF-8; invalid UTF-8 handled like tiktoken (bytes still round-trip).
- Pre-tokeniser differential fuzzing against the reference regexes.

## Benchmarks

| Benchmark | Metric |
|---|---|
| Encode single doc (1 KB, 100 KB, 10 MB), ASCII-heavy and multilingual | MB/s, tokens/s, allocations |
| Batch encode 10 k docs | throughput vs cores |
| `CountTokens` | MB/s |
| Decode | MB/s |
| Load vocab (json / tiktoken / compiled) | ms |

Targets: ≥ tiktoken-rs single-threaded on cl100k/o200k; within ±10% of HF `tokenizers` for Llama 3.

## Risks

- Non-ASCII-heavy text depends on Unicode table lookups; may trail on CJK. Mitigation: per-script fast paths after profiling.
- Exact regex semantics (especially `\s+(?!\S)`) must be replicated byte-for-byte; differential fuzzing is mandatory before any perf work.
