//! Loads the shared inputs written by bench/datasets/generate_m0.py.

use std::path::PathBuf;

pub fn m0_dir() -> PathBuf {
    let root = std::env::var("AIINFRA_ROOT")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../.."));
    root.join("bench/datasets/data/m0")
}

pub fn read(name: &str) -> Vec<u8> {
    let path = m0_dir().join(name);
    std::fs::read(&path).unwrap_or_else(|e| panic!("{}: {e} (run bench/datasets/generate_m0.py)", path.display()))
}

pub fn manifest() -> serde_json::Value {
    serde_json::from_slice(&read("manifest.json")).expect("manifest.json")
}

/// Splits a newline-terminated file into lines without copying.
pub fn lines(blob: &[u8]) -> Vec<&[u8]> {
    blob.split(|&b| b == b'\n').filter(|l| !l.is_empty()).collect()
}
