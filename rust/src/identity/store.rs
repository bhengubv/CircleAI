//! store.rs
//!
//! IIdentityStore and IIdentityProvider traits.

use super::types::{CircleIdentity, RegisteredDevice};

/// Persistent store for Circle AI identities and device registrations.
pub trait IIdentityStore {
    type Error: std::error::Error;

    fn get(&self, identity_id: &str) -> Result<Option<CircleIdentity>, Self::Error>;
    fn save(&mut self, identity: &CircleIdentity) -> Result<(), Self::Error>;
    fn get_devices(&self, identity_id: &str) -> Result<Vec<RegisteredDevice>, Self::Error>;
    fn register_device(&mut self, device: RegisteredDevice) -> Result<(), Self::Error>;
    fn get_by_device(&self, device_id: &str) -> Result<Option<CircleIdentity>, Self::Error>;
}

/// Resolves the active identity for the current device/session.
///
/// Implementations may use local storage, biometrics, or mesh-distributed keys.
pub trait IIdentityProvider {
    type Error: std::error::Error;

    fn get_current_identity(&self) -> Result<Option<CircleIdentity>, Self::Error>;
    fn is_authenticated(&self) -> Result<bool, Self::Error>;
    fn create_identity(
        &mut self,
        display_name: &str,
        preferred_language: Option<&str>,
    ) -> Result<CircleIdentity, Self::Error>;
}
