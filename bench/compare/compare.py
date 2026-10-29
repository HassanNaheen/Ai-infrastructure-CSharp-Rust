"""Join BenchmarkDotNet and criterion results into a parity report (docs/07-benchmarking.md).

Usage: python3 bench/compare/compare.py --milestone m0

Reads  bench/results/csharp/results/*-report-full.json   (BenchmarkDotNet, JsonExporter.Full)
       bench/rust/target/criterion/<group>/<fn>/new/estimates.json
Writes bench/reports/<milestone>-<cpu>.md
"""
import argparse
import json
import platform
import re
import subprocess
from dataclasses import dataclass
from datetime import date
from pathlib import Path

BENCH = Path(__file__).resolve().parent.parent

# Per benchmark group: the C# class, the C# method that is the AiInfra candidate (the others are
# baselines), and the work done per operation, used for throughput.
GROUPS = {
    "m0": {
        "count_byte": {"class": "CountByteBench", "candidate": "Simd", "work": 16 << 20, "unit": "GB/s", "scale": 1e9},
        "map_build": {"class": "MapBuildBench", "candidate": "BytesMap", "work": 100_000, "unit": "M keys/s", "scale": 1e6},
        "map_lookup": {"class": "MapLookupBench", "candidate": "BytesMap", "work": 1_000_000, "unit": "M lookups/s", "scale": 1e6},
    },
}

GREEN, AMBER = 1.10, 1.50


@dataclass
class Result:
    language: str
    runtime: str
    name: str
    median_ns: float
    allocated: int | None = None


def load_csharp(classes: set[str]) -> dict[str, list[Result]]:
    out: dict[str, list[Result]] = {}
    for path in sorted((BENCH / "results" / "csharp" / "results").glob("*-report-full.json")):
        report = json.loads(path.read_text())
        for b in report["Benchmarks"]:
            if b["Type"] not in classes or not b.get("Statistics"):
                continue
            job = re.search(r": (\w+)\(", b["DisplayInfo"])
            runtime = job.group(1) if job else "?"
            memory = b.get("Memory") or {}
            out.setdefault(b["Type"], []).append(
                Result("C#", runtime, b["Method"], b["Statistics"]["Median"], memory.get("BytesAllocatedPerOperation"))
            )
    return out


def load_rust(group: str) -> list[Result]:
    root = BENCH / "rust" / "target" / "criterion" / group
    results = []
    for est in sorted(root.glob("*/new/estimates.json")):
        median = json.loads(est.read_text())["median"]["point_estimate"]
        results.append(Result("Rust", "release+LTO", est.parent.parent.name, median))
    return results


def verdict(ratio: float) -> str:
    if ratio < 1 / GREEN:
        return "🟢 faster"
    if ratio <= GREEN:
        return "🟢 parity"
    if ratio <= AMBER:
        return "🟠 investigate"
    return "🔴 gap"


def fmt_time(ns: float) -> str:
    if ns >= 1e6:
        return f"{ns / 1e6:.2f} ms"
    if ns >= 1e3:
        return f"{ns / 1e3:.1f} µs"
    return f"{ns:.0f} ns"


def run(cmd: list[str]) -> str:
    try:
        return subprocess.run(cmd, capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return "?"


def machine() -> dict[str, str]:
    cpu = run(["sysctl", "-n", "machdep.cpu.brand_string"]) if platform.system() == "Darwin" else platform.processor()
    return {
        "CPU": cpu or platform.machine(),
        "OS": f"{platform.system()} {platform.release()} ({platform.machine()})",
        ".NET SDK": run(["dotnet", "--version"]),
        "rustc": run(["rustc", "--version"]),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--milestone", default="m0", choices=sorted(GROUPS))
    args = parser.parse_args()
    groups = GROUPS[args.milestone]

    csharp = load_csharp({g["class"] for g in groups.values()})
    info = machine()
    lines = [
        f"# {args.milestone.upper()} benchmark report",
        "",
        f"Generated {date.today().isoformat()} by `bench/compare/compare.py`. Method and parity thresholds: "
        "[07-benchmarking](../../docs/07-benchmarking.md). Ratio = C# candidate median ÷ fastest Rust median; "
        f"≤ {GREEN:.2f} is parity.",
        "",
        "| | |",
        "|---|---|",
        *[f"| {k} | {v} |" for k, v in info.items()],
        "",
        "## Summary",
        "",
        "| Group | C# runtime | C# candidate | Fastest Rust | Ratio | Verdict |",
        "|---|---|---|---|---:|---|",
    ]
    details: list[str] = []
    missing: list[str] = []

    for group, spec in groups.items():
        cs = csharp.get(spec["class"], [])
        rs = load_rust(group)
        if not cs or not rs:
            missing.append(group)
            continue
        best_rust = min(rs, key=lambda r: r.median_ns)
        for runtime in sorted({r.runtime for r in cs}):
            cand = next((r for r in cs if r.runtime == runtime and r.name == spec["candidate"]), None)
            if cand is None:
                continue
            ratio = cand.median_ns / best_rust.median_ns
            lines.append(
                f"| {group} | {runtime} | {cand.name} {fmt_time(cand.median_ns)} | "
                f"{best_rust.name} {fmt_time(best_rust.median_ns)} | {ratio:.2f} | {verdict(ratio)} |"
            )

        details += [
            f"### {group}",
            "",
            f"| Language | Runtime | Implementation | Median | {spec['unit']} | Allocated | vs fastest Rust |",
            "|---|---|---|---:|---:|---:|---:|",
        ]
        for r in sorted(cs + rs, key=lambda r: r.median_ns):
            throughput = spec["work"] / (r.median_ns / 1e9) / spec["scale"]
            alloc = "—" if r.allocated is None else f"{r.allocated:,} B"
            name = f"**{r.name}**" if r.language == "C#" and r.name == spec["candidate"] else r.name
            details.append(
                f"| {r.language} | {r.runtime} | {name} | {fmt_time(r.median_ns)} | {throughput:,.2f} | {alloc} | "
                f"{r.median_ns / best_rust.median_ns:.2f} |"
            )
        details.append("")

    lines += ["", "## Details", "", "Bold = AiInfra candidate; other C# rows are baselines.", "", *details]
    if missing:
        lines += ["## Missing", "", f"No results for: {', '.join(missing)}. Run `bench/run-m0.sh`.", ""]

    slug = re.sub(r"[^a-z0-9]+", "-", info["CPU"].lower()).strip("-") or "unknown"
    out = BENCH / "reports" / f"{args.milestone}-{slug}.md"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(lines) + "\n")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
