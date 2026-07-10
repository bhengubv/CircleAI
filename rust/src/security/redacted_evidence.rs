//! redacted_evidence.rs
//!
//! Rust port of `RedactedEvidenceJsonConverter.cs`.
//!
//! Serialises an evidence map with every value replaced by the hex SHA-256 of
//! its UTF-8 bytes (prefixed `sha256:`) instead of the raw content. The keys
//! (evidence labels) are preserved so structured log sinks can still join
//! entries by evidence shape, but the raw values — which may carry session
//! tokens, payload fragments, or PII — never leave the process in clear text.
//!
//! Read side intentionally reverses to an empty map: incoming JSON cannot be
//! trusted to carry the original cleartext, and round-tripping hashes back into
//! the map would mask whether the source-of-record is the in-process signal or
//! a serialised copy.

use std::collections::{BTreeMap, HashMap};

use serde::ser::{SerializeMap, Serializer};

use super::hashing::{sha256_bytes, to_hex_lower};

/// Redacts a single evidence value to `sha256:<hex-lower>`. An empty/missing
/// value hashes to the bare prefix `sha256:` (matching the C#
/// `string.IsNullOrEmpty` fast-path).
pub fn hash_redacted(raw: &str) -> String {
    if raw.is_empty() {
        return "sha256:".to_string();
    }
    let hash = sha256_bytes(raw.as_bytes());
    format!("sha256:{}", to_hex_lower(&hash))
}

/// Redacts an entire evidence map, returning a deterministically-ordered
/// `label -> "sha256:<hex>"` map. Keys are preserved; every value is redacted.
pub fn redact_evidence(evidence: &HashMap<String, String>) -> BTreeMap<String, String> {
    evidence
        .iter()
        .map(|(k, v)| (k.clone(), hash_redacted(v)))
        .collect()
}

/// Serialises the redacted evidence map to a JSON object string. Values are
/// redacted; key order is deterministic (sorted). Mirrors the C# converter's
/// `Write` output shape.
pub fn to_redacted_json(evidence: &HashMap<String, String>) -> String {
    let redacted = redact_evidence(evidence);
    serde_json::to_string(&redacted).unwrap_or_else(|_| "{}".to_string())
}

/// serde `serialize_with` adapter — apply on an evidence field to emit redacted
/// values on the wire. Key order is deterministic (sorted).
///
/// ```ignore
/// #[serde(serialize_with = "redacted_evidence::serialize_redacted")]
/// evidence: HashMap<String, String>,
/// ```
pub fn serialize_redacted<S>(
    evidence: &HashMap<String, String>,
    serializer: S,
) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    let ordered = redact_evidence(evidence);
    let mut map = serializer.serialize_map(Some(ordered.len()))?;
    for (k, v) in &ordered {
        map.serialize_entry(k, v)?;
    }
    map.end()
}
