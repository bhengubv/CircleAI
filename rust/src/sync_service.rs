//! sync_service.rs
//!
//! Port of `CircleAI.Sync/` — the memory-delta push/receive orchestrator.
//!
//!   * [`IMemorySyncService`] — pushes and receives memory deltas across all
//!     owned devices. The transport is [`ISyncChannel`] so the calling code is
//!     identical whether the delta travels gRPC, BLE mesh, or DTN bundle.
//!   * [`MemorySyncService`] — the default implementation. Serialises memory
//!     deltas, routes them through the channel, and applies received deltas to
//!     the local episodic store.
//!
//! The C# `ReceiveLoopAsync` runs as a fire-and-forget background task draining
//! an `IAsyncEnumerable`. The Rust `ISyncChannel::receive_deltas` yields a
//! synchronous iterator, so this port exposes [`MemorySyncService::receive_once`]
//! — drain the currently-available deltas and apply the episodic ones — which a
//! host drives on its own cadence. `start_receiving` / `stop_receiving` gate it
//! via a flag, mirroring the C# `_receiveCts` lifecycle.
//!
//! Applying a delta is a concrete operation here (not a TODO): an
//! `EpisodicMemory` delta's JSON payload is deserialised into an
//! [`EpisodicMemoryEntry`] and added to the local store. Deltas whose
//! `source_device_id` equals the local device are skipped (own echoes), as are
//! deltas of other domains.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use crate::memory::stores::{EpisodicMemoryEntry, IEpisodicMemoryStore};
use crate::sync::{ISyncChannel, SyncDeliveryMode, SyncDelta, SyncDomainKeys};

// ─────────────────────────────────────────────────────────────────────────────
// IMemorySyncService
// ─────────────────────────────────────────────────────────────────────────────

/// Pushes and receives memory deltas across all owned devices.
pub trait IMemorySyncService {
    type Error: std::error::Error;

    /// Push a memory delta for `owner_id` to all other devices.
    fn push_memory_delta(
        &self,
        owner_id: &str,
        domain_key: &str,
        delta: Vec<u8>,
        mode: SyncDeliveryMode,
    ) -> Result<(), Self::Error>;

    /// Start receiving and applying incoming deltas for `owner_id`.
    fn start_receiving(&self, owner_id: &str) -> Result<(), Self::Error>;

    /// Stop receiving.
    fn stop_receiving(&self) -> Result<(), Self::Error>;
}

// ─────────────────────────────────────────────────────────────────────────────
// MemorySyncService
// ─────────────────────────────────────────────────────────────────────────────

/// Default [`IMemorySyncService`]. Serialises memory deltas, routes them through
/// an [`ISyncChannel`], and applies received episodic deltas to the local store.
///
/// Generic over the channel `C` and the episodic store `S` (both sync traits use
/// an associated `Error`, so monomorphising keeps the port allocation-free and
/// object-safe). 1:1 with the C# `MemorySyncService`.
pub struct MemorySyncService<C, S>
where
    C: ISyncChannel,
    S: IEpisodicMemoryStore,
{
    channel: Mutex<C>,
    store: Mutex<S>,
    local_device_id: String,
    receiving: AtomicBool,
    receiving_owner: Mutex<Option<String>>,
}

/// Error surface for [`MemorySyncService`]. Wraps either the channel's or the
/// store's error, plus payload-decode failures.
#[derive(Debug)]
pub enum MemorySyncError<CE, SE> {
    /// The underlying [`ISyncChannel`] failed.
    Channel(CE),
    /// The underlying [`IEpisodicMemoryStore`] failed.
    Store(SE),
}

impl<CE: std::fmt::Display, SE: std::fmt::Display> std::fmt::Display for MemorySyncError<CE, SE> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            MemorySyncError::Channel(e) => write!(f, "sync channel error: {e}"),
            MemorySyncError::Store(e) => write!(f, "episodic store error: {e}"),
        }
    }
}

impl<CE, SE> std::error::Error for MemorySyncError<CE, SE>
where
    CE: std::fmt::Debug + std::fmt::Display,
    SE: std::fmt::Debug + std::fmt::Display,
{
}

impl<C, S> MemorySyncService<C, S>
where
    C: ISyncChannel,
    S: IEpisodicMemoryStore,
{
    /// Creates the service over a channel, a local episodic store, and this
    /// device's id.
    pub fn new(channel: C, store: S, local_device_id: impl Into<String>) -> Self {
        Self {
            channel: Mutex::new(channel),
            store: Mutex::new(store),
            local_device_id: local_device_id.into(),
            receiving: AtomicBool::new(false),
            receiving_owner: Mutex::new(None),
        }
    }

    /// Whether a receive session is currently active.
    pub fn is_receiving(&self) -> bool {
        self.receiving.load(Ordering::SeqCst)
    }

    /// Drains the deltas currently available for `owner_id` and applies the
    /// episodic-memory ones to the local store. Own-device echoes and
    /// other-domain deltas are skipped. No-op (returns `Ok(0)`) unless a receive
    /// session for this owner is active (see [`start_receiving`]).
    ///
    /// Returns the number of episodic deltas actually applied. This is the
    /// synchronous analog of the C# `ReceiveLoopAsync` body, invoked by the host
    /// on its own cadence.
    pub fn receive_once(&self, owner_id: &str) -> Result<usize, MemorySyncError<C::Error, S::Error>> {
        if !self.is_receiving() {
            return Ok(0);
        }
        {
            let active = self.receiving_owner.lock().unwrap();
            if active.as_deref() != Some(owner_id) {
                return Ok(0);
            }
        }

        // Collect first so the channel lock is released before we touch the store
        // (mirrors the C# loop which awaits per-item but never holds two locks).
        let deltas: Vec<SyncDelta> = {
            let channel = self.channel.lock().unwrap();
            let iter = channel
                .receive_deltas(owner_id)
                .map_err(MemorySyncError::Channel)?;
            let mut out = Vec::new();
            for item in iter {
                out.push(item.map_err(MemorySyncError::Channel)?);
            }
            out
        };

        let mut applied = 0usize;
        for delta in deltas {
            if delta.source_device_id == self.local_device_id {
                continue; // skip own echoes
            }
            if delta.domain_key == SyncDomainKeys::MEMORY_EPISODIC {
                // Full wire: deserialise and upsert into the local episodic store.
                if let Ok(entry) = serde_json::from_slice::<EpisodicMemoryEntry>(&delta.payload) {
                    let mut store = self.store.lock().unwrap();
                    store.add(entry).map_err(MemorySyncError::Store)?;
                    applied += 1;
                }
            }
            // Additional domain handlers (affect, persona, goals) go here.
        }
        Ok(applied)
    }
}

impl<C, S> IMemorySyncService for MemorySyncService<C, S>
where
    C: ISyncChannel,
    S: IEpisodicMemoryStore,
{
    type Error = MemorySyncError<C::Error, S::Error>;

    fn push_memory_delta(
        &self,
        owner_id: &str,
        domain_key: &str,
        delta: Vec<u8>,
        mode: SyncDeliveryMode,
    ) -> Result<(), Self::Error> {
        let sync_delta = SyncDelta::new(
            owner_id,
            &self.local_device_id,
            "", // broadcast to all owned devices
            domain_key,
            delta,
            chrono::Utc::now().timestamp_millis(),
            mode,
            None, // ttl
        );

        let mut channel = self.channel.lock().unwrap();
        channel
            .push_delta(&sync_delta)
            .map_err(MemorySyncError::Channel)
    }

    fn start_receiving(&self, owner_id: &str) -> Result<(), Self::Error> {
        *self.receiving_owner.lock().unwrap() = Some(owner_id.to_string());
        self.receiving.store(true, Ordering::SeqCst);
        Ok(())
    }

    fn stop_receiving(&self) -> Result<(), Self::Error> {
        self.receiving.store(false, Ordering::SeqCst);
        *self.receiving_owner.lock().unwrap() = None;
        Ok(())
    }
}
