# 04 — Validation (`AiInfra.Validation`)

**Rust comparators:** [`jsonschema`](https://github.com/Stranger6667/jsonschema) (jsonschema-rs), `serde_json` + `garde` / `validator` derive.
**Informational:** `pydantic-core` (via Python), **.NET baselines:** System.Text.Json + DataAnnotations, `JsonSchema.Net`, FluentValidation.

## The problem

Typical .NET validation deserialises into objects, then walks them with reflection. Rust validators parse JSON straight into structs (or validate without materialising) in one pass. We do the same.

## Two front-ends, one runtime

### Static: source-generated (serde / pydantic-core equivalent)

```csharp
[Validated]
public partial struct User
{
    [MinLength(1), MaxLength(64)] public Utf8Str Name;
    [Range(0, 150)]               public int Age;
    [Format(Formats.Email)]       public Utf8Str Email;
    public Address? Address;      // nested [Validated] type
}

if (!User.TryParse(jsonUtf8, out User user, ref errors)) { ... }
bool ok = User.Validate(jsonUtf8, ref errors);   // validate-only, no materialisation
```

The incremental Roslyn generator (`AiInfra.Validation.Generator`) emits, per type:
- A single-pass parser over `Utf8JsonReader`: decode and validate together.
- Property-name dispatch: `switch` on name length, then `SequenceEqual` against UTF-8 literals (`"name"u8`); case-insensitive option generates folded comparisons.
- Required-property tracking with a bitmask local.
- No reflection, no `JsonSerializer` → fully AOT/trim safe.
- Diagnostics for unsupported attribute combinations at compile time.

### Dynamic: JSON Schema (Draft 7 and 2020-12)

The schema is compiled once into an **instruction tape**: a flat `Op[]` array plus side tables.

| Op examples | Meaning |
|---|---|
| `TypeCheck(mask)` | allowed JSON types as bitmask |
| `MinLen` / `MaxLen` / `Pattern(id)` / `Format(id)` | string constraints |
| `Min` / `Max` / `MultipleOf` | numeric (exact decimal handling for `multipleOf`) |
| `Props(tableId)` | property table: `BytesMap` name → sub-tape offset; `additionalProperties`, `required` bitmask |
| `Items(offset)` / `PrefixItems` / `Contains` | arrays |
| `Enum(setId)` / `Const` | canonicalised value hash set |
| `AllOf` / `AnyOf` / `OneOf` / `Not` / `If-Then-Else` | combinators with jump offsets |
| `Ref(offset)` | resolved at compile time; recursion supported |

- The interpreter is a tight `switch` loop driven by the token stream; combinators that need re-reading capture a `Utf8JsonReader` checkpoint (it is a struct, so copying it is cheap).
- `unevaluatedProperties/Items` handled with per-object evaluated-bitmasks.
- Optional JIT tier: when `RuntimeFeature.IsDynamicCodeCompiled`, hot schemas are emitted as IL via `DynamicMethod`. Disabled automatically under Native AOT, where the interpreter is used.

## Zero-allocation design

- `Utf8Str`: borrowed slice of the input buffer (like serde `&str`). If the JSON string has escapes, the unescaped bytes go into the caller's `Arena`.
- **Validate-only mode** never creates values.
- `ErrorSink` (`ref struct` over pooled storage): fail-fast or collect-all. Error locations are a stack of `(kind, index | name-offset)`; JSON Pointer strings are rendered only when the caller reads them.
- Formats (`email`, `uuid`, `date`, `date-time`, `ipv4`, `ipv6`, `uri`, `hostname`) are hand-written validators on UTF-8 bytes.
- `pattern`: .NET `Regex` (source-generated where static, `NonBacktracking` where dynamic) on a `stackalloc` UTF-16 copy of just that string. ECMA-262 vs .NET dialect differences are documented and tested.
- Large/streaming input: `ReadOnlySequence<byte>` from `PipeReader` without concatenation.

## Correctness

- Official **JSON-Schema-Test-Suite** runner; pass rate per draft tracked in CI (target ≥ 95% for 2020-12 in M4, optional `format` assertion suites reported separately).
- Generator snapshot tests (Verify) and compile-error diagnostics tests.
- Differential fuzzing: random documents validated by us and by jsonschema-rs (through a small CLI) must agree.

## Benchmarks

| Benchmark | Inputs |
|---|---|
| Small API payloads, valid / invalid, fail-fast | 200 B – 2 KB objects, 1 M iterations |
| Large documents | canada.json (GeoJSON), citm_catalog.json, twitter.json |
| Schema compile time | jsonschema-rs bench schemas, OpenAPI-sized schema |
| Static parse+validate vs serde+garde | same struct shape in both languages |

Metrics: MB/s, ns/document, allocations (target 0 B for validate-only).

## Risks

- `pattern` regex dialect and Unicode semantics.
- `unevaluated*` keywords complicate single-pass validation; may require a second pass for affected sub-schemas.
