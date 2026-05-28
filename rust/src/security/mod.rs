//! security — Local-runtime threat classification and anomaly signalling.
//!
//! Portable schema only. The actual watchdog implementation (response policy,
//! quarantine, host integration) stays C# host-side. This module ports just
//! the data types that detection sites in any language need to emit, so the
//! signals are interchangeable across the polyglot SDK.

pub mod anomaly_signal;
pub mod threat_vector;

pub use anomaly_signal::AnomalySignal;
pub use threat_vector::ThreatVector;
