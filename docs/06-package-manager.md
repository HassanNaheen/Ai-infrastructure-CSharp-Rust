# 06 — Package manager (`AiInfra.Pkg`, CLI `cpkg`)

**Rust comparator:** [uv](https://github.com/astral-sh/uv).

## Scope

A PyPI-compatible installer, so the comparison with uv is like-for-like:

- `cpkg venv` — create a virtual environment.
- `cpkg pip install <reqs>` / `cpkg pip compile` — resolve and install.
- `cpkg lock` / `cpkg sync` — lockfile workflow.
- Standards: PEP 503 / 691 Simple API (HTML + JSON), PEP 440 versions, PEP 508 requirements & markers, PEP 427 wheels, PEP 658/714 metadata files, PEP 376 RECORD.
- **Out of the hot path:** building sdists, delegated to PEP 517 backends in a subprocess (same as uv). Results cached as wheels.

## Architecture

```
CLI (AOT) → Planner → Resolver (PubGrub) ⇄ Metadata provider ⇄ HTTP client / cache
                         ↓
                 Install plan → Downloader → Unzipper → Linker → site-packages
```

### Network
- One `SocketsHttpHandler` shared process-wide: HTTP/2 multiplexing, connection pooling, Brotli/gzip.
- Bounded `Channel<T>` pipelines for downloads; concurrency tuned per host.
- HTTP caching: ETag / `Cache-Control` revalidation of Simple API pages.
- JSON Simple API parsed with System.Text.Json **source generation** (AOT-safe); HTML fallback parsed with a small hand-written scanner.

### Metadata
1. PEP 658 `.metadata` file when the index provides it.
2. Otherwise HTTP **range requests**: fetch the wheel's zip central directory, then only the `*.dist-info/METADATA` entry — no full download (the technique uv uses).
3. Otherwise full download (sdists, old indexes).

### Resolver
- **PubGrub** (incompatibility-driven, with good error explanations), as in uv.
- PEP 440 `Version`: common versions packed into a single `ulong` for branch-free comparison; full representation for epochs/local/dev segments.
- PEP 508 markers compiled to a small predicate tree evaluated against the target environment.
- Speculative **prefetch**: as soon as a package's candidate list is known, metadata for likely versions is fetched in parallel ahead of the solver.

### Cache
- Content-addressed global cache: unpacked wheels keyed by SHA-256, Simple API responses keyed by URL.
- Lockfile in TOML with hashes and resolution markers.

### Install
- Files are **linked** from the cache into `site-packages`, in order of preference:
  1. macOS APFS `clonefile(2)` (P/Invoke) — copy-on-write, near-instant;
  2. Linux `ioctl(FICLONE)` reflink (btrfs, xfs);
  3. hardlink;
  4. copy.
- Per-file parallelism on `WorkScheduler`; RECORD, INSTALLER, `direct_url.json` and console-script entry points written natively.
- `.pyc` compilation optional and delegated to the target Python, off by default (as uv).

### Extraction
- `System.IO.Compression` (zlib-ng backed in .NET 9+), entries decompressed in parallel from a random-access `FileStream`.

### Binary & startup
- Native AOT, trimmed, `InvariantGlobalization`, `UseSystemResourceKeys`, `OptimizationPreference=Speed`.
- Targets: binary < 20 MB (uv is ~35 MB, so size is not a disadvantage), startup < 15 ms.
- Expected honest gap: AOT startup ~5–10 ms behind uv; irrelevant for installs, visible on `--help`/no-op commands.

## Correctness

- PEP 440 and PEP 508 test vectors (from `packaging`).
- Resolver fixtures: a set of requirement files resolved by `uv pip compile` and by `cpkg pip compile`; resolutions must match (or differ only where both are valid, documented).
- Installed tree diffed against a uv-installed venv (files, RECORD contents, entry points).

## Benchmarks (hyperfine)

| Scenario | Cache |
|---|---|
| Resolve `jupyter`, `transformers[torch]`, Home Assistant requirements | cold / warm |
| Install into fresh venv | cold / warm |
| No-op `sync` | warm |
| `cpkg --help` | startup |

Run against a **local PyPI mirror** (deterministic, removes network noise) and live PyPI (informational). Peak RSS and binary size reported too.

## Risks

- Range-request support varies by index/CDN.
- Resolver heuristics (which version to try first) dominate real-world speed as much as raw CPU; uv's heuristics must be studied and matched.
