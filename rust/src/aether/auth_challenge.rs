//! aether::auth_challenge — Rust port of `CircleAI.Aether/IAuthChallenge.cs`.
//!
//! Contract 5 — Auth Challenge. A bidirectional trust gate: user auth enables
//! the security layer at OS level; the security layer demands re-auth when
//! threat thresholds are crossed. Minimum for any OS-level operation is
//! Biometric + DeviceAdmin. Developers can raise the bar; they cannot lower it.
//!
//! The C# surface is `Task`-based; the Rust port is sync, matching the crate's
//! existing sync-trait convention. Platform adapters (MAUI, server) implement
//! [`IAuthChallenge`] using native biometric / device-admin APIs;
//! [`PolicyAuthChallenge`] is the deterministic in-memory implementation.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

// ─────────────────────────────────────────────────────────────────────────────
// Enums
// ─────────────────────────────────────────────────────────────────────────────

/// Why an auth challenge is being issued. Ordinals follow the C# declaration.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AuthChallengeReason {
    /// The user is enabling or disabling the OS-level Aether service.
    OsLevelToggle = 0,
    /// The AI Security Layer detected anomaly scores above threshold and
    /// requires the user to confirm their identity.
    ThreatThresholdReached = 1,
    /// The operation being attempted requires elevated auth.
    PrivilegedOperation = 2,
    /// Scheduled trust renewal — periodic re-validation.
    PeriodicRevalidation = 3,
    /// Explicitly triggered by the developer or admin.
    ManualRequest = 4,
}

/// The authentication method used or required. Methods are ordered by strength;
/// higher numeric values are stronger. Discriminants match the C# enum exactly
/// (`Biometric = 1` .. `Custom = 4`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AuthMethod {
    /// Fingerprint, face, or iris recognition.
    Biometric = 1,
    /// Device administrator credential (PIN, password, pattern).
    DeviceAdmin = 2,
    /// Biometric AND device admin — the minimum for any OS-level operation.
    BiometricAndDeviceAdmin = 3,
    /// Developer-defined method layered on top of BiometricAndDeviceAdmin.
    Custom = 4,
}

// ─────────────────────────────────────────────────────────────────────────────
// AuthChallengeResult
// ─────────────────────────────────────────────────────────────────────────────

/// The outcome of an auth challenge.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthChallengeResult {
    pub succeeded: bool,
    pub method_used: AuthMethod,
    pub failure_reason: Option<String>,
    pub completed_at: DateTime<Utc>,
}

impl AuthChallengeResult {
    /// A successful result with no failure reason.
    pub fn success(method: AuthMethod) -> Self {
        Self {
            succeeded: true,
            method_used: method,
            failure_reason: None,
            completed_at: Utc::now(),
        }
    }

    /// A failed result with an explanatory reason.
    pub fn failure(method: AuthMethod, reason: impl Into<String>) -> Self {
        Self {
            succeeded: false,
            method_used: method,
            failure_reason: Some(reason.into()),
            completed_at: Utc::now(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IAuthChallenge trait
// ─────────────────────────────────────────────────────────────────────────────

/// Issues and resolves authentication challenges for security-sensitive
/// operations.
pub trait IAuthChallenge: Send + Sync {
    /// Presents an auth challenge to the user for the given reason. The adapter
    /// enforces the minimum-method requirement. `minimum_method` defaults to
    /// [`AuthMethod::BiometricAndDeviceAdmin`] when `None`.
    fn challenge(
        &self,
        reason: AuthChallengeReason,
        minimum_method: Option<AuthMethod>,
        prompt: &str,
    ) -> AuthChallengeResult;

    /// Presents the OS-level toggle challenge. Always requires
    /// [`AuthMethod::BiometricAndDeviceAdmin`] at minimum.
    fn request_os_toggle(&self, enable: bool) -> AuthChallengeResult;
}

// ─────────────────────────────────────────────────────────────────────────────
// PolicyAuthChallenge — deterministic in-memory implementation
// ─────────────────────────────────────────────────────────────────────────────

/// A deterministic [`IAuthChallenge`] used for tests and headless hosts. It
/// simulates the user presenting `available_method` and applies the minimum-
/// method policy: the OS floor is [`AuthMethod::BiometricAndDeviceAdmin`], and a
/// caller-supplied minimum can only raise the bar, never lower it below that
/// floor. Success requires the available method to be at least as strong as the
/// effective minimum.
#[derive(Debug, Clone)]
pub struct PolicyAuthChallenge {
    /// The strongest method the simulated user can satisfy.
    available_method: AuthMethod,
}

impl PolicyAuthChallenge {
    /// The OS floor — no challenge may accept anything weaker than this.
    pub const OS_FLOOR: AuthMethod = AuthMethod::BiometricAndDeviceAdmin;

    /// Creates a challenge where the user can satisfy up to `available_method`.
    pub fn new(available_method: AuthMethod) -> Self {
        Self { available_method }
    }

    /// A challenge whose user always satisfies the strongest method.
    pub fn always_succeeds() -> Self {
        Self::new(AuthMethod::Custom)
    }

    /// A challenge whose user can only satisfy [`AuthMethod::Biometric`] — used
    /// to exercise the "below the OS floor" failure path.
    pub fn biometric_only() -> Self {
        Self::new(AuthMethod::Biometric)
    }

    /// Resolves a challenge against `requested_minimum`. The effective minimum is
    /// the stronger of the OS floor and the requested minimum; the challenge
    /// succeeds iff the available method meets it.
    fn resolve(&self, requested_minimum: AuthMethod) -> AuthChallengeResult {
        let effective_minimum = requested_minimum.max(Self::OS_FLOOR);
        if self.available_method >= effective_minimum {
            AuthChallengeResult::success(self.available_method)
        } else {
            AuthChallengeResult::failure(
                self.available_method,
                format!(
                    "available method {:?} is weaker than the required minimum {:?}",
                    self.available_method, effective_minimum
                ),
            )
        }
    }
}

impl IAuthChallenge for PolicyAuthChallenge {
    fn challenge(
        &self,
        _reason: AuthChallengeReason,
        minimum_method: Option<AuthMethod>,
        _prompt: &str,
    ) -> AuthChallengeResult {
        let requested = minimum_method.unwrap_or(AuthMethod::BiometricAndDeviceAdmin);
        self.resolve(requested)
    }

    fn request_os_toggle(&self, _enable: bool) -> AuthChallengeResult {
        // OS toggle always demands the floor.
        self.resolve(AuthMethod::BiometricAndDeviceAdmin)
    }
}
