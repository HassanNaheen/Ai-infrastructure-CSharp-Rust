//! M0 comparators. Group and function names match the C# benchmark classes and methods,
//! so bench/compare/compare.py can pair them up.

use std::hint::black_box;

use core_bench::{lines, manifest, read};
use criterion::{Criterion, criterion_group, criterion_main};
use hashbrown::HashMap;
use xxhash_rust::xxh3::Xxh3Builder;

fn count_byte(c: &mut Criterion) {
    let data = read("bytes.bin");
    let m = manifest();
    let value = m["count_byte"]["value"].as_u64().unwrap() as u8;
    let expected = m["count_byte"]["expected"].as_u64().unwrap() as usize;

    let naive = |d: &[u8]| d.iter().filter(|&&b| b == value).count();
    assert_eq!(naive(&data), expected);
    assert_eq!(bytecount::count(&data, value), expected);

    let mut g = c.benchmark_group("count_byte");
    // Idiomatic iterator; LLVM auto-vectorises it.
    g.bench_function("naive_iter", |b| b.iter(|| naive(black_box(&data))));
    // The bytecount crate: hand-written SIMD with runtime dispatch.
    g.bench_function("bytecount", |b| b.iter(|| bytecount::count(black_box(&data), value)));
    g.finish();
}

fn build_default(words: &[&[u8]]) -> HashMap<Vec<u8>, i32> {
    let mut map = HashMap::new();
    for (i, w) in words.iter().enumerate() {
        map.insert(w.to_vec(), i as i32);
    }
    map
}

fn build_xxh3(words: &[&[u8]]) -> HashMap<Vec<u8>, i32, Xxh3Builder> {
    let mut map = HashMap::with_hasher(Xxh3Builder::new());
    for (i, w) in words.iter().enumerate() {
        map.insert(w.to_vec(), i as i32);
    }
    map
}

// Keys are owned (Vec<u8>) so the map, like BytesMap, copies key bytes on insert.
fn map_build(c: &mut Criterion) {
    let blob = read("words.txt");
    let words = lines(&blob);
    let expected = manifest()["map_build"]["expected_count"].as_u64().unwrap() as usize;
    assert_eq!(build_default(&words).len(), expected);
    assert_eq!(build_xxh3(&words).len(), expected);

    let mut g = c.benchmark_group("map_build");
    // hashbrown with its default hasher (foldhash): the fastest common Rust choice.
    g.bench_function("hashbrown_foldhash", |b| b.iter(|| build_default(black_box(&words))));
    // Same table with XXH3, the hash BytesMap uses.
    g.bench_function("hashbrown_xxh3", |b| b.iter(|| build_xxh3(black_box(&words))));
    g.finish();
}

fn lookup<S: std::hash::BuildHasher>(map: &HashMap<Vec<u8>, i32, S>, probes: &[&[u8]]) -> i64 {
    let (mut hits, mut sum) = (0i64, 0i64);
    for p in probes {
        if let Some(&v) = map.get(*p) {
            hits += 1;
            sum += v as i64;
        }
    }
    hits << 40 | sum
}

fn map_lookup(c: &mut Criterion) {
    let words_blob = read("words.txt");
    let words = lines(&words_blob);
    let probes_blob = read("probes.txt");
    let probes = lines(&probes_blob);
    let m = manifest();
    let expected = (m["map_lookup"]["expected_hits"].as_i64().unwrap() << 40)
        | m["map_lookup"]["expected_value_sum"].as_i64().unwrap();

    let default_map = build_default(&words);
    let xxh3_map = build_xxh3(&words);
    assert_eq!(lookup(&default_map, &probes), expected);
    assert_eq!(lookup(&xxh3_map, &probes), expected);

    let mut g = c.benchmark_group("map_lookup");
    g.bench_function("hashbrown_foldhash", |b| b.iter(|| lookup(&default_map, black_box(&probes))));
    g.bench_function("hashbrown_xxh3", |b| b.iter(|| lookup(&xxh3_map, black_box(&probes))));
    g.finish();
}

criterion_group!(benches, count_byte, map_build, map_lookup);
criterion_main!(benches);
