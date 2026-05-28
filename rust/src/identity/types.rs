//! types.rs
//!
//! CircleIdentity, RegisteredDevice, and IdentityTier.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// Trust/verification tier of a [`CircleIdentity`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub enum IdentityTier {
    Anonymous,
    Pseudonymous,
    Verified,
}

/// A Circle AI identity — the unified persona key that travels with the person.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CircleIdentity {
    /// Stable GUID — never changes.
    pub identity_id: String,
    pub display_name: String,
    pub preferred_language: Option<String>,
    pub tier: IdentityTier,
    pub device_ids: Vec<String>,
    pub created_at: DateTime<Utc>,
    pub last_seen_at: DateTime<Utc>,
}

impl CircleIdentity {
    pub fn new(
        identity_id: impl Into<String>,
        display_name: impl Into<String>,
        preferred_language: Option<String>,
        tier: IdentityTier,
        device_ids: Vec<String>,
        created_at: DateTime<Utc>,
        last_seen_at: DateTime<Utc>,
    ) -> Self {
        Self {
            identity_id: identity_id.into(),
            display_name: display_name.into(),
            preferred_language,
            tier,
            device_ids,
            created_at,
            last_seen_at,
        }
    }
}

/// A device registered to an identity.
///
/// `platform` is one of `"android"`, `"ios"`, `"windows"`, `"macos"`, `"linux"`,
/// `"web"`, `"watch"`, or `"iot"`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RegisteredDevice {
    pub device_id: String,
    pub identity_id: String,
    /// One of: `"android"` | `"ios"` | `"windows"` | `"macos"` | `"linux"` | `"web"` | `"watch"` | `"iot"`
    pub platform: String,
    pub device_name: Option<String>,
    pub registered_at: DateTime<Utc>,
    pub last_active_at: DateTime<Utc>,
}

impl RegisteredDevice {
    pub fn new(
        device_id: impl Into<String>,
        identity_id: impl Into<String>,
        platform: impl Into<String>,
        device_name: Option<String>,
        registered_at: DateTime<Utc>,
        last_active_at: DateTime<Utc>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            identity_id: identity_id.into(),
            platform: platform.into(),
            device_name,
            registered_at,
            last_active_at,
        }
    }
}
