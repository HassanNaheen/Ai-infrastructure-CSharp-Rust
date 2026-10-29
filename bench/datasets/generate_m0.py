"""Generate the deterministic M0 benchmark inputs shared by the C# and Rust suites.

Writes to bench/datasets/data/m0/:
  bytes.bin      16 MiB of random bytes                      (count_byte)
  words.txt      100,000 distinct lowercase keys, one per line (map_build, map_lookup)
  probes.txt     1,000,000 lookups, ~90% hits                  (map_lookup)
  manifest.json  expected results; both suites check them before timing
"""
import hashlib
import json
import random
import string
from pathlib import Path

OUT = Path(__file__).resolve().parent / "data" / "m0"
COUNT_BYTE_VALUE = 42
WORDS = 100_000
PROBES = 1_000_000


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    rng = random.Random(42)

    data = rng.randbytes(16 << 20)
    (OUT / "bytes.bin").write_bytes(data)

    words: list[str] = []
    seen: set[str] = set()
    while len(words) < WORDS:
        w = "".join(rng.choices(string.ascii_lowercase, k=rng.randint(3, 12)))
        if w not in seen:
            seen.add(w)
            words.append(w)
    (OUT / "words.txt").write_text("\n".join(words) + "\n")

    # Misses contain an uppercase letter, so they can never collide with a key.
    probes: list[str] = []
    hits = 0
    value_sum = 0
    for _ in range(PROBES):
        if rng.random() < 0.9:
            i = rng.randrange(WORDS)
            probes.append(words[i])
            hits += 1
            value_sum += i
        else:
            probes.append("".join(rng.choices(string.ascii_letters, k=rng.randint(3, 12))) + "X")
    (OUT / "probes.txt").write_text("\n".join(probes) + "\n")

    manifest = {
        "count_byte": {"value": COUNT_BYTE_VALUE, "expected": data.count(COUNT_BYTE_VALUE)},
        "map_build": {"expected_count": WORDS},
        # The value stored for each key is its line index in words.txt.
        "map_lookup": {"expected_hits": hits, "expected_value_sum": value_sum},
        "sha256": {
            name: hashlib.sha256((OUT / name).read_bytes()).hexdigest()
            for name in ("bytes.bin", "words.txt", "probes.txt")
        },
    }
    (OUT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
