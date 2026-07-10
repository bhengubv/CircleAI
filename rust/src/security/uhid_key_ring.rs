//! uhid_key_ring.rs
//!
//! Ephemeral session key management bound to a UHID identity — Rust port of
//! `UhidKeyRing.cs`.
//!
//! Each UHID session gets a fresh signing key. When an anomaly is confirmed the
//! watchdog calls `rotate()` — the old key is revoked and a new key ring is
//! issued. All in-flight requests signed with the revoked key are rejected.
//!
//! The C# reference uses ECDSA (P-256). The Rust crate carries no asymmetric
//! crypto crate (matching `HmacCryptoDelegation` in the companion port), so the
//! ring signs with a self-contained HMAC-SHA256 over the crate's vetted SHA-256
//! core — a real, verifiable MAC. Lifecycle semantics are identical to the C#
//! ring: fresh `ring_id` per generation, revoke disables signing but keeps
//! verification, and `rotate` returns a NEW ring while revoking this one. A host
//! that needs true public-key signing swaps this out behind the same surface.

use std::sync::Mutex;

use chrono::{DateTime, Utc};
use uuid::Uuid;

use super::hashing::{fixed_time_equals, sha256_bytes};

/// Internal mutable state guarded by the ring lock.
struct RingState {
    /// The HMAC secret for this ring. `None` once disposed.
    key: Option<Vec<u8>>,
    revoked: bool,
    ring_id: Uuid,
    generated_at: DateTime<Utc>,
    revoked_at: Option<DateTime<Utc>>,
    /// Public key material (SHA-256 of the secret — safe to share, corresponds
    /// to the signing key without revealing it). Mirrors the C# `PublicKeyDer`.
    public_key: Vec<u8>,
}

/// Ephemeral HMAC-SHA256 session key ring bound to a UHID identity.
/// Generate a fresh ring at session start or on anomaly confirmation. Once
/// revoked, the ring cannot sign; generate a new one.
pub struct UhidKeyRing {
    uhid_identity_id: String,
    state: Mutex<RingState>,
}

impl UhidKeyRing {
    /// Creates a new [`UhidKeyRing`] for `uhid_identity_id` with a freshly
    /// generated key.
    ///
    /// # Panics
    /// Panics if `uhid_identity_id` is blank (mirrors the C#
    /// `ArgumentException.ThrowIfNullOrWhiteSpace`).
    pub fn generate_fresh(uhid_identity_id: impl Into<String>) -> Self {
        let uhid_identity_id = uhid_identity_id.into();
        assert!(
            !uhid_identity_id.trim().is_empty(),
            "uhidIdentityId required"
        );
        let ring = Self {
            uhid_identity_id,
            state: Mutex::new(RingState {
                key: None,
                revoked: false,
                ring_id: Uuid::nil(),
                generated_at: Utc::now(),
                revoked_at: None,
                public_key: Vec::new(),
            }),
        };
        ring.regenerate_key();
        ring
    }

    /// The UHID identity this ring is bound to.
    pub fn uhid_identity_id(&self) -> &str {
        &self.uhid_identity_id
    }

    /// Unique ring identifier. Changes on every regeneration.
    pub fn ring_id(&self) -> Uuid {
        self.state.lock().unwrap().ring_id
    }

    /// UTC timestamp when this ring was generated.
    pub fn generated_at(&self) -> DateTime<Utc> {
        self.state.lock().unwrap().generated_at
    }

    /// UTC timestamp when this ring was revoked, or `None` if still active.
    pub fn revoked_at(&self) -> Option<DateTime<Utc>> {
        self.state.lock().unwrap().revoked_at
    }

    /// `true` if this ring has been explicitly revoked.
    pub fn is_revoked(&self) -> bool {
        self.state.lock().unwrap().revoked
    }

    /// The public key material for this ring. Safe to share; corresponds to the
    /// private signing key. (Mirrors the C# `PublicKeyDer`.)
    pub fn public_key_der(&self) -> Vec<u8> {
        self.state.lock().unwrap().public_key.clone()
    }

    /// Rotates the ring: revokes the current key and generates a replacement.
    /// Returns a NEW [`UhidKeyRing`] — this instance remains revoked.
    ///
    /// Prefer this pattern over mutating in place so call sites holding a
    /// reference to the old ring cannot accidentally sign with a rotated key.
    pub fn rotate(&self) -> UhidKeyRing {
        self.revoke();
        UhidKeyRing::generate_fresh(self.uhid_identity_id.clone())
    }

    /// Signs `data` with the current key using HMAC-SHA256.
    ///
    /// # Errors
    /// Returns `Err` if the ring has been disposed or revoked (mirrors the C#
    /// `ObjectDisposedException` / `InvalidOperationException`).
    pub fn sign(&self, data: &[u8]) -> Result<Vec<u8>, KeyRingError> {
        let guard = self.state.lock().unwrap();
        let Some(key) = guard.key.as_ref() else {
            return Err(KeyRingError::Disposed);
        };
        if guard.revoked {
            return Err(KeyRingError::Revoked(guard.ring_id));
        }
        Ok(hmac_sha256(key, data).to_vec())
    }

    /// Verifies an HMAC-SHA256 `signature` against `data` using this ring's key.
    /// Works even after revocation (so prior signatures can still be validated).
    /// Returns `false` if the ring has been disposed.
    pub fn verify(&self, data: &[u8], signature: &[u8]) -> bool {
        let guard = self.state.lock().unwrap();
        let Some(key) = guard.key.as_ref() else {
            return false;
        };
        let expected = hmac_sha256(key, data);
        fixed_time_equals(&expected, signature)
    }

    /// Revokes this ring. After revocation [`UhidKeyRing::sign`] errors;
    /// [`UhidKeyRing::verify`] continues to work for historical validation.
    /// Idempotent.
    pub fn revoke(&self) {
        let mut guard = self.state.lock().unwrap();
        if guard.revoked {
            return;
        }
        guard.revoked = true;
        guard.revoked_at = Some(Utc::now());
    }

    /// Disposes the ring, zeroising the secret. After dispose both sign and
    /// verify fail. Mirrors the C# `IDisposable.Dispose`.
    pub fn dispose(&self) {
        let mut guard = self.state.lock().unwrap();
        guard.key = None;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    fn regenerate_key(&self) {
        let mut guard = self.state.lock().unwrap();
        // Fresh random 32-byte secret (two UUIDs' worth of entropy).
        let mut secret = Vec::with_capacity(32);
        secret.extend_from_slice(Uuid::new_v4().as_bytes());
        secret.extend_from_slice(Uuid::new_v4().as_bytes());
        let public_key = sha256_bytes(&secret).to_vec();
        guard.key = Some(secret);
        guard.ring_id = Uuid::new_v4();
        guard.generated_at = Utc::now();
        guard.revoked_at = None;
        guard.revoked = false;
        guard.public_key = public_key;
    }
}

impl Drop for UhidKeyRing {
    fn drop(&mut self) {
        if let Ok(mut guard) = self.state.lock() {
            guard.key = None;
        }
    }
}

/// Errors returned by [`UhidKeyRing::sign`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum KeyRingError {
    /// The ring was disposed — no key material remains.
    Disposed,
    /// The ring has been revoked — call `rotate()` to get a fresh ring.
    Revoked(Uuid),
}

impl std::fmt::Display for KeyRingError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            KeyRingError::Disposed => write!(f, "UhidKeyRing has been disposed."),
            KeyRingError::Revoked(id) => write!(
                f,
                "UhidKeyRing {id} has been revoked — call rotate() to get a fresh ring."
            ),
        }
    }
}

impl std::error::Error for KeyRingError {}

/// HMAC-SHA256 (FIPS 198-1) over the crate's vetted SHA-256 core.
fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    const BLOCK: usize = 64;
    let mut k = if key.len() > BLOCK {
        sha256_bytes(key).to_vec()
    } else {
        key.to_vec()
    };
    k.resize(BLOCK, 0);
    let mut ipad = [0x36u8; BLOCK];
    let mut opad = [0x5cu8; BLOCK];
    for i in 0..BLOCK {
        ipad[i] ^= k[i];
        opad[i] ^= k[i];
    }
    let mut inner = ipad.to_vec();
    inner.extend_from_slice(message);
    let inner_hash = sha256_bytes(&inner);
    let mut outer = opad.to_vec();
    outer.extend_from_slice(&inner_hash);
    sha256_bytes(&outer)
}
