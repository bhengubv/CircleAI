//! federation.rs
//!
//! Port of `CircleAI.Federation/` — federated-learning round bookkeeping for the
//! CircleAI mesh. Accepts signed deltas from peers, aggregates per round via
//! sample-size-weighted averaging, and exposes the aggregated update back to
//! participants. NO raw training data leaves the device — only the delta.
//!
//!   * [`ModelDelta`] — one participant's signed contribution to a round.
//!   * [`FederationRound`] / [`RoundStatus`] — one coordinated round's lifecycle.
//!   * [`IFederationAggregator`] — the coordinator side (open / submit / commit /
//!     get). [`InMemoryFederationAggregator`] is the in-process reference.
//!   * [`IFederationParticipant`] — the device side (produce delta / apply model).
//!   * [`IFederationDeltaDispatcher`] + [`DeltaDispatchOutcome`] — safe-by-default
//!     verify + dedup + submit composer. [`DefaultFederationDeltaDispatcher`] wires
//!     it over an aggregator.
//!   * [`federated_averaging`] — the little-endian IEEE-754 `f32[]` weighted
//!     averaging + encode/decode helpers.
//!
//! C# async (`Task<>`) maps to `#[async_trait]` methods. The `Func<ModelDelta,
//! bool>` signature-validator delegate maps to a boxed `Fn` closure. The C#
//! `ConcurrentDictionary` + per-round `lock` maps to a `Mutex<HashMap<..>>` with
//! per-round `Mutex` state.

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fmt;
use std::sync::Mutex;
use uuid::Uuid;

// ─────────────────────────────────────────────────────────────────────────────
// ModelDelta
// ─────────────────────────────────────────────────────────────────────────────

/// One participant's signed contribution to a federation round.
///
/// 1:1 with the C# `sealed record ModelDelta`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ModelDelta {
    /// Unique delta identifier.
    pub id: Uuid,
    /// Identifier of the [`FederationRound`] this delta belongs to.
    pub round_id: Uuid,
    /// Pseudonymous UHID (hashed). NEVER raw PII — always a one-way hash so the
    /// aggregator can deduplicate without learning the user's identity.
    pub contributor_uhid: String,
    /// Model the delta applies to.
    pub model_id: String,
    /// Base model version the participant trained on.
    pub from_version: String,
    /// Opaque byte blob carrying the weight deltas. The reference aggregator
    /// interprets this as a little-endian IEEE 754 `f32[]`.
    pub delta_payload: Vec<u8>,
    /// Number of local training samples the participant used to produce the delta.
    /// Used by federated averaging as the weighting factor.
    pub sample_count: i32,
    /// ECDSA-SHA256 signature over the delta payload produced by the contributor's
    /// key ring. Verified via a caller-supplied validator so this module does not
    /// depend on the key ring.
    pub signature: Vec<u8>,
    /// UTC timestamp of submission.
    pub submitted_at: DateTime<Utc>,
}

// ─────────────────────────────────────────────────────────────────────────────
// RoundStatus / FederationRound
// ─────────────────────────────────────────────────────────────────────────────

/// Lifecycle state of a [`FederationRound`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum RoundStatus {
    /// Round is accepting deltas from participants.
    Open,
    /// Round has the minimum delta count and is averaging.
    Aggregating,
    /// Round committed an aggregated model; further deltas rejected.
    Committed,
    /// Round was abandoned (timeout, insufficient participants, etc.).
    Aborted,
}

/// One coordinated round of federated learning, identified by [`FederationRound::id`]
/// and bound to a specific model version transition
/// ([`FederationRound::from_version`] → [`FederationRound::to_version`]).
///
/// 1:1 with the C# `sealed record FederationRound` (gated `CIRCLEAI_FED_001`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FederationRound {
    /// Unique round identifier.
    pub id: Uuid,
    /// Canonical model name shared by all participants.
    pub model_id: String,
    /// Semantic version of the base model participants train on.
    pub from_version: String,
    /// Semantic version the aggregated model will publish as.
    pub to_version: String,
    /// Minimum number of valid deltas required before the round may commit. Below
    /// this threshold, `try_commit` returns `None`.
    pub min_participants: i32,
    /// Hard upper bound on accepted deltas. Submissions beyond this are rejected.
    pub max_participants: i32,
    /// Number of deltas accepted so far.
    pub current_participant_count: i32,
    /// Current lifecycle state.
    pub status: RoundStatus,
    /// UTC timestamp the round was opened.
    pub opened_at: DateTime<Utc>,
    /// UTC timestamp the round was committed, or `None` if not yet committed.
    pub committed_at: Option<DateTime<Utc>>,
}

// ─────────────────────────────────────────────────────────────────────────────
// FederationError
// ─────────────────────────────────────────────────────────────────────────────

/// Error surface for the federation aggregator. Mirrors the C# exception cases:
/// `ArgumentException` (validation), `KeyNotFoundException` (unknown round), and
/// `InvalidOperationException` (round closed / at capacity).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FederationError {
    /// An argument failed validation (empty string, non-positive bound, etc.).
    InvalidArgument(String),
    /// The referenced round id is unknown to the aggregator.
    RoundNotFound(Uuid),
    /// The operation is not valid for the round's current state.
    InvalidOperation(String),
}

impl fmt::Display for FederationError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            FederationError::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            FederationError::RoundNotFound(id) => write!(f, "round {id} is unknown"),
            FederationError::InvalidOperation(m) => write!(f, "invalid operation: {m}"),
        }
    }
}

impl std::error::Error for FederationError {}

// ─────────────────────────────────────────────────────────────────────────────
// federated_averaging — sample-size-weighted f32[] averaging.
// ─────────────────────────────────────────────────────────────────────────────

/// Sample-size-weighted averaging over [`ModelDelta::delta_payload`] arrays
/// interpreted as little-endian IEEE 754 `f32[]`, plus encode/decode helpers.
///
/// Port of the C# static `FederatedAveraging`.
pub mod federated_averaging {
    use super::ModelDelta;

    const F32_SIZE: usize = std::mem::size_of::<f32>(); // 4

    /// Error raised by the averaging helpers. Mirrors the C# `ArgumentException`
    /// cases (empty list, inconsistent/short payloads, zero total weight).
    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct AveragingError(pub String);

    impl std::fmt::Display for AveragingError {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            f.write_str(&self.0)
        }
    }

    impl std::error::Error for AveragingError {}

    /// Computes the sample-size-weighted average of the supplied deltas and returns
    /// the encoded result as little-endian IEEE 754 bytes.
    ///
    /// # Errors
    /// When `deltas` is empty, when payload byte lengths are inconsistent, when a
    /// payload length is not a multiple of 4 bytes, when a `sample_count` is
    /// negative, or when total sample weight is zero.
    pub fn average(deltas: &[ModelDelta]) -> Result<Vec<u8>, AveragingError> {
        if deltas.is_empty() {
            return Err(AveragingError("Cannot average an empty delta list.".into()));
        }

        let expected_bytes = deltas[0].delta_payload.len();
        if expected_bytes == 0 {
            return Err(AveragingError("Delta payloads must be non-empty.".into()));
        }
        if expected_bytes % F32_SIZE != 0 {
            return Err(AveragingError(format!(
                "Delta payload length ({expected_bytes}) must be a multiple of {F32_SIZE} bytes."
            )));
        }

        for (i, d) in deltas.iter().enumerate().skip(1) {
            if d.delta_payload.len() != expected_bytes {
                return Err(AveragingError(format!(
                    "Delta payload length mismatch: index 0 = {expected_bytes} bytes, index {i} = {} bytes.",
                    d.delta_payload.len()
                )));
            }
        }

        let float_count = expected_bytes / F32_SIZE;
        let mut total_samples: i64 = 0;
        for d in deltas {
            if d.sample_count < 0 {
                return Err(AveragingError(format!(
                    "SampleCount must be non-negative; delta {} reported {}.",
                    d.id, d.sample_count
                )));
            }
            total_samples += d.sample_count as i64;
        }
        if total_samples == 0 {
            return Err(AveragingError(
                "Total sample weight across deltas is zero — cannot perform weighted average."
                    .into(),
            ));
        }

        let mut accumulator = vec![0f64; float_count];
        for d in deltas {
            let weight = d.sample_count as f64 / total_samples as f64;
            for i in 0..float_count {
                let value = read_f32_le(&d.delta_payload, i * F32_SIZE);
                accumulator[i] += value as f64 * weight;
            }
        }

        let mut output = vec![0u8; expected_bytes];
        for i in 0..float_count {
            write_f32_le(&mut output, i * F32_SIZE, accumulator[i] as f32);
        }
        Ok(output)
    }

    /// Encodes an `f32` slice as little-endian IEEE 754 bytes.
    pub fn encode_floats(values: &[f32]) -> Vec<u8> {
        let mut output = vec![0u8; values.len() * F32_SIZE];
        for (i, &v) in values.iter().enumerate() {
            write_f32_le(&mut output, i * F32_SIZE, v);
        }
        output
    }

    /// Decodes little-endian IEEE 754 bytes into an `f32` vector.
    ///
    /// # Errors
    /// When `payload` length is not a multiple of 4 bytes.
    pub fn decode_floats(payload: &[u8]) -> Result<Vec<f32>, AveragingError> {
        if payload.len() % F32_SIZE != 0 {
            return Err(AveragingError(format!(
                "Payload length ({}) must be a multiple of {F32_SIZE} bytes.",
                payload.len()
            )));
        }
        let count = payload.len() / F32_SIZE;
        let mut output = vec![0f32; count];
        for i in 0..count {
            output[i] = read_f32_le(payload, i * F32_SIZE);
        }
        Ok(output)
    }

    #[inline]
    fn read_f32_le(bytes: &[u8], offset: usize) -> f32 {
        let mut buf = [0u8; F32_SIZE];
        buf.copy_from_slice(&bytes[offset..offset + F32_SIZE]);
        f32::from_le_bytes(buf)
    }

    #[inline]
    fn write_f32_le(bytes: &mut [u8], offset: usize, value: f32) {
        bytes[offset..offset + F32_SIZE].copy_from_slice(&value.to_le_bytes());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IFederationAggregator
// ─────────────────────────────────────────────────────────────────────────────

/// Coordinator for federation rounds. Implementations may store deltas in memory
/// (tests, edge), in SQLite (on-device), or in a distributed mesh (production
/// over Aether). The contract is the same.
#[async_trait]
pub trait IFederationAggregator {
    /// Opens a new round for `model_id` moving from `from_version` to `to_version`.
    async fn open_round(
        &self,
        model_id: &str,
        from_version: &str,
        to_version: &str,
        min_participants: i32,
        max_participants: i32,
    ) -> Result<FederationRound, FederationError>;

    /// Submits a signed delta to its associated round. Rejects deltas whose
    /// `round_id` does not match an open round, and errors with
    /// [`FederationError::InvalidOperation`] when the round has already reached
    /// `max_participants`.
    async fn submit_delta(&self, delta: ModelDelta) -> Result<(), FederationError>;

    /// Attempts to commit the round. Returns the aggregated payload when
    /// `min_participants` valid deltas have been collected; returns `None`
    /// otherwise. On success the round flips to [`RoundStatus::Committed`].
    async fn try_commit(&self, round_id: Uuid) -> Result<Option<Vec<u8>>, FederationError>;

    /// Returns the current [`FederationRound`] snapshot. Errors with
    /// [`FederationError::RoundNotFound`] if the round is unknown.
    async fn get_round(&self, round_id: Uuid) -> Result<FederationRound, FederationError>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IFederationParticipant
// ─────────────────────────────────────────────────────────────────────────────

/// Contract for a device that contributes to federation rounds. The participant
/// is responsible for local training, producing the signed delta, and accepting
/// an aggregated model when the aggregator publishes one.
#[async_trait]
pub trait IFederationParticipant {
    /// Error surface for the participant.
    type Error: std::error::Error;

    /// Trains locally against the participant's private data and returns the
    /// resulting signed [`ModelDelta`]. Implementations MUST NOT transmit raw
    /// training data — only the delta payload leaves the device.
    async fn produce_delta(&self, round: &FederationRound) -> Result<ModelDelta, Self::Error>;

    /// Applies an aggregated model published by the aggregator and reports whether
    /// the application succeeded (checksum validation passed, the engine accepted
    /// the new weights).
    async fn apply_aggregated_model(
        &self,
        model_id: &str,
        new_version: &str,
        aggregated_payload: &[u8],
    ) -> Result<bool, Self::Error>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IFederationDeltaDispatcher
// ─────────────────────────────────────────────────────────────────────────────

/// Outcome of a [`IFederationDeltaDispatcher::verify_and_submit`] call.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeltaDispatchOutcome {
    /// Delta accepted and recorded for the round.
    Accepted = 0,
    /// Signature did not verify against the contributor's UHID key.
    SignatureInvalid = 1,
    /// This delta id was already recorded for the round (replay).
    Duplicate = 2,
    /// The round id is unknown to the aggregator.
    RoundUnknown = 3,
    /// The round is not currently accepting deltas (e.g. already committed).
    RoundClosed = 4,
}

/// Safe-by-default federation delta dispatcher. Verify, dedup, and submit in one
/// call so consumers cannot skip a step.
///
/// The bare [`IFederationAggregator::submit_delta`] path requires the caller to
/// remember to verify signatures and check for duplicate deltas. The dispatcher
/// composes those steps so a production consumer cannot accidentally accept an
/// unsigned or replayed delta.
#[async_trait]
pub trait IFederationDeltaDispatcher {
    /// Verify the delta's signature, check it has not already been recorded for the
    /// round, and submit it. Returns a [`DeltaDispatchOutcome`] describing what
    /// happened — no error is returned on rejection so the caller can branch on the
    /// outcome without try/catch.
    async fn verify_and_submit(&self, delta: ModelDelta) -> DeltaDispatchOutcome;
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryFederationAggregator
// ─────────────────────────────────────────────────────────────────────────────

/// In-process reference [`IFederationAggregator`]. Stores all round and delta
/// state in memory; not durable across process restarts. Use for tests, edge
/// devices, or as a starting point for a real implementation.
///
/// Signature verification is delegated to a caller-supplied validator closure so
/// this module does not depend on the key ring directly — that keeps the
/// federation API engine-agnostic and testable in isolation. Gated
/// `CIRCLEAI_FED_001` in the C#.
pub struct InMemoryFederationAggregator {
    rounds: Mutex<HashMap<Uuid, RoundState>>,
    signature_validator: Box<dyn Fn(&ModelDelta) -> bool + Send + Sync>,
}

/// Per-round mutable state held behind the aggregator's map lock.
struct RoundState {
    snapshot: FederationRound,
    deltas: Vec<ModelDelta>,
    committed_payload: Option<Vec<u8>>,
}

impl RoundState {
    fn new(initial: FederationRound) -> Self {
        Self {
            snapshot: initial,
            deltas: Vec::new(),
            committed_payload: None,
        }
    }
}

impl InMemoryFederationAggregator {
    /// Component name, mirroring the C# `ComponentName` override.
    pub const COMPONENT_NAME: &'static str = "InMemoryFederationAggregator";

    /// Constructs the aggregator with a signature validator. Pass `|_| true` in
    /// tests where signatures are not the subject of test. The aggregator drops
    /// deltas whose validator returns `false` at commit time.
    pub fn new<F>(signature_validator: F) -> Self
    where
        F: Fn(&ModelDelta) -> bool + Send + Sync + 'static,
    {
        Self {
            rounds: Mutex::new(HashMap::new()),
            signature_validator: Box::new(signature_validator),
        }
    }

    /// Total number of rounds currently tracked. Diagnostic only.
    pub fn round_count(&self) -> usize {
        self.rounds.lock().unwrap().len()
    }

    /// Fallback used when payload encodings are inconsistent: the median delta by
    /// `sample_count`, copied. Mirrors the C# `FallbackMedianPayload`.
    fn fallback_median_payload(deltas: &[ModelDelta]) -> Vec<u8> {
        let mut ordered: Vec<&ModelDelta> = deltas.iter().collect();
        ordered.sort_by_key(|d| d.sample_count);
        ordered[ordered.len() / 2].delta_payload.clone()
    }
}

#[async_trait]
impl IFederationAggregator for InMemoryFederationAggregator {
    async fn open_round(
        &self,
        model_id: &str,
        from_version: &str,
        to_version: &str,
        min_participants: i32,
        max_participants: i32,
    ) -> Result<FederationRound, FederationError> {
        if model_id.is_empty() {
            return Err(FederationError::InvalidArgument("modelId".into()));
        }
        if from_version.is_empty() {
            return Err(FederationError::InvalidArgument("fromVersion".into()));
        }
        if to_version.is_empty() {
            return Err(FederationError::InvalidArgument("toVersion".into()));
        }
        if min_participants <= 0 {
            return Err(FederationError::InvalidArgument(
                "minParticipants must be positive.".into(),
            ));
        }
        if max_participants < min_participants {
            return Err(FederationError::InvalidArgument(format!(
                "maxParticipants ({max_participants}) must be >= minParticipants ({min_participants})."
            )));
        }

        let round = FederationRound {
            id: Uuid::new_v4(),
            model_id: model_id.to_string(),
            from_version: from_version.to_string(),
            to_version: to_version.to_string(),
            min_participants,
            max_participants,
            current_participant_count: 0,
            status: RoundStatus::Open,
            opened_at: Utc::now(),
            committed_at: None,
        };

        let mut rounds = self.rounds.lock().unwrap();
        let snapshot = round.clone();
        rounds.insert(round.id, RoundState::new(round));
        Ok(snapshot)
    }

    async fn submit_delta(&self, delta: ModelDelta) -> Result<(), FederationError> {
        let mut rounds = self.rounds.lock().unwrap();
        let state = rounds
            .get_mut(&delta.round_id)
            .ok_or(FederationError::RoundNotFound(delta.round_id))?;

        // Treat empty payloads as invalid: do not store, do not count. The
        // aggregator does not raise — callers may legitimately submit an "empty"
        // gradient if their local data was insufficient, keeping the round viable.
        if delta.delta_payload.is_empty() {
            return Ok(());
        }

        if state.snapshot.status != RoundStatus::Open {
            return Err(FederationError::InvalidOperation(format!(
                "Round {} is {:?}; not accepting deltas.",
                delta.round_id, state.snapshot.status
            )));
        }
        if state.deltas.len() as i32 >= state.snapshot.max_participants {
            return Err(FederationError::InvalidOperation(format!(
                "Round {} has reached MaxParticipants ({}).",
                delta.round_id, state.snapshot.max_participants
            )));
        }

        state.deltas.push(delta);
        state.snapshot.current_participant_count = state.deltas.len() as i32;
        Ok(())
    }

    async fn try_commit(&self, round_id: Uuid) -> Result<Option<Vec<u8>>, FederationError> {
        let mut rounds = self.rounds.lock().unwrap();
        let state = rounds
            .get_mut(&round_id)
            .ok_or(FederationError::RoundNotFound(round_id))?;

        match state.snapshot.status {
            // Idempotent: re-return the previously committed payload.
            RoundStatus::Committed => return Ok(state.committed_payload.clone()),
            RoundStatus::Aborted => return Ok(None),
            _ => {}
        }

        let valid_deltas: Vec<ModelDelta> = state
            .deltas
            .iter()
            .filter(|d| (self.signature_validator)(d))
            .cloned()
            .collect();
        if (valid_deltas.len() as i32) < state.snapshot.min_participants {
            return Ok(None);
        }

        state.snapshot.status = RoundStatus::Aggregating;

        // Payload encoding inconsistent — fall back to the median delta by
        // SampleCount as documented in the contract.
        let aggregated = match federated_averaging::average(&valid_deltas) {
            Ok(bytes) => bytes,
            Err(_) => Self::fallback_median_payload(&valid_deltas),
        };

        state.committed_payload = Some(aggregated.clone());
        state.snapshot.status = RoundStatus::Committed;
        state.snapshot.committed_at = Some(Utc::now());
        Ok(Some(aggregated))
    }

    async fn get_round(&self, round_id: Uuid) -> Result<FederationRound, FederationError> {
        let rounds = self.rounds.lock().unwrap();
        rounds
            .get(&round_id)
            .map(|s| s.snapshot.clone())
            .ok_or(FederationError::RoundNotFound(round_id))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DefaultFederationDeltaDispatcher
// ─────────────────────────────────────────────────────────────────────────────

/// Safe-by-default [`IFederationDeltaDispatcher`] over an
/// [`InMemoryFederationAggregator`]. Composes signature verification, per-round
/// duplicate detection, and submission so a production consumer cannot skip a
/// step.
///
/// The signature validator is supplied at construction (same shape as the
/// aggregator's). Duplicate detection tracks recorded delta ids per round.
pub struct DefaultFederationDeltaDispatcher {
    aggregator: std::sync::Arc<InMemoryFederationAggregator>,
    signature_validator: Box<dyn Fn(&ModelDelta) -> bool + Send + Sync>,
    seen: Mutex<HashMap<Uuid, std::collections::HashSet<Uuid>>>,
}

impl DefaultFederationDeltaDispatcher {
    /// Creates a dispatcher over `aggregator` with a signature validator closure.
    pub fn new<F>(
        aggregator: std::sync::Arc<InMemoryFederationAggregator>,
        signature_validator: F,
    ) -> Self
    where
        F: Fn(&ModelDelta) -> bool + Send + Sync + 'static,
    {
        Self {
            aggregator,
            signature_validator: Box::new(signature_validator),
            seen: Mutex::new(HashMap::new()),
        }
    }
}

#[async_trait]
impl IFederationDeltaDispatcher for DefaultFederationDeltaDispatcher {
    async fn verify_and_submit(&self, delta: ModelDelta) -> DeltaDispatchOutcome {
        // 1. Verify signature.
        if !(self.signature_validator)(&delta) {
            return DeltaDispatchOutcome::SignatureInvalid;
        }

        // 2. Dedup within the round (replay protection).
        {
            let mut seen = self.seen.lock().unwrap();
            let round_seen = seen.entry(delta.round_id).or_default();
            if round_seen.contains(&delta.id) {
                return DeltaDispatchOutcome::Duplicate;
            }
            round_seen.insert(delta.id);
        }

        // 3. Submit, mapping the aggregator error surface onto the outcome enum.
        match self.aggregator.submit_delta(delta).await {
            Ok(()) => DeltaDispatchOutcome::Accepted,
            Err(FederationError::RoundNotFound(_)) => DeltaDispatchOutcome::RoundUnknown,
            Err(FederationError::InvalidOperation(_)) => DeltaDispatchOutcome::RoundClosed,
            Err(FederationError::InvalidArgument(_)) => DeltaDispatchOutcome::RoundClosed,
        }
    }
}
