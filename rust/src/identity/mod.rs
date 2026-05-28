//! identity — CircleIdentity, RegisteredDevice, IdentityTier, BiometricProfile,
//! BiometricMatcher, and store/provider traits.

pub mod biometric;
pub mod store;
pub mod types;

// Re-export the flat surface that existing code and tests expect at
// `circle_ai::identity::`.
pub use biometric::{BiometricMatcher, BiometricProfile};
pub use store::{IIdentityProvider, IIdentityStore};
pub use types::{CircleIdentity, IdentityTier, RegisteredDevice};
