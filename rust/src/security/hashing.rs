//! hashing.rs
//!
//! Small self-contained hashing helpers shared across the security module.
//! Reuses the crate's vetted FIPS 180-4 SHA-256 core (byte-identical to
//! `System.Security.Cryptography.SHA256`) so cross-language checkpoints and
//! redacted evidence hash identically.

/// SHA-256 digest of `data` (32 bytes). Byte-identical to .NET `SHA256.HashData`.
pub(crate) fn sha256_bytes(data: &[u8]) -> [u8; 32] {
    crate::memory::multimodal::sha256(data)
}

/// Upper-case hex of `bytes`. Mirrors .NET `Convert.ToHexString`.
pub(crate) fn to_hex_upper(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{b:02X}"));
    }
    s
}

/// Lower-case hex of `bytes`. Mirrors .NET `Convert.ToHexString(..).ToLowerInvariant()`.
pub(crate) fn to_hex_lower(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{b:02x}"));
    }
    s
}

/// Constant-time byte-slice equality. Mirrors
/// `CryptographicOperations.FixedTimeEquals` — length-independent short-circuit
/// only on unequal length, otherwise compares every byte.
pub(crate) fn fixed_time_equals(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}
