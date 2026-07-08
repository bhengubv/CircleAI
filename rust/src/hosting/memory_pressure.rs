//! memory_pressure.rs
//!
//! (RT-04) Platform-published memory-pressure signal. Ported from
//! `IMemoryPressureSource.cs`. Hosting wires the platform-specific source
//! (Android `onTrimMemory`, iOS memory warning) into the pipeline;
//! [`AIService`](crate::hosting::AIService) listens and triggers the
//! fallback-chain swap when the level reaches Critical.
//!
//! The C# handler is `Func<old,new,ValueTask>` and `Subscribe` returns an
//! `IDisposable`. The sync port uses an `Fn(old, new)` handler and returns a
//! [`PressureSubscription`] whose `Drop` unsubscribes.

use std::sync::{Arc, Mutex};

/// Coarse memory-pressure level. Mirrors Android's onTrimMemory contract and
/// iOS's memory warning. 1:1 with the C# `MemoryPressureLevel`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum MemoryPressureLevel {
    /// Plenty of headroom; no action.
    Normal = 0,
    /// OS asked apps to release optional caches. Drop prefix cache.
    Trim = 1,
    /// OS is about to kill the process. Drop everything; consider downshifting.
    Critical = 2,
}

/// A memory-pressure handler: `(old_level, new_level)`.
pub type PressureHandler = Arc<dyn Fn(MemoryPressureLevel, MemoryPressureLevel) + Send + Sync>;

/// (RT-04) A platform-published memory-pressure signal. Implementations notify
/// subscribers on a worker thread; subscribers must be thread-safe. 1:1 with
/// the C# `IMemoryPressureSource`.
pub trait IMemoryPressureSource: Send + Sync {
    /// Current pressure level as last observed.
    fn current(&self) -> MemoryPressureLevel;

    /// Subscribe to pressure-level transitions. Returns an unsubscribe handle;
    /// dropping it removes the handler.
    fn subscribe(&self, handler: PressureHandler) -> PressureSubscription;
}

/// Unsubscribe handle. Dropping it removes the associated handler from its
/// source. Modelled on the C# `IDisposable` returned by `Subscribe`.
pub struct PressureSubscription {
    remover: Option<Box<dyn FnOnce() + Send + Sync>>,
}

impl PressureSubscription {
    fn new(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self {
            remover: Some(Box::new(remover)),
        }
    }

    /// A no-op subscription (used by [`NullMemoryPressureSource`]).
    fn empty() -> Self {
        Self { remover: None }
    }

    /// Explicit unsubscribe (equivalent to dropping; idempotent).
    pub fn unsubscribe(mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

impl Drop for PressureSubscription {
    fn drop(&mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

/// Default [`IMemoryPressureSource`] that always reports Normal pressure and
/// never raises events. 1:1 with the C# `NullMemoryPressureSource`.
#[derive(Debug, Default)]
pub struct NullMemoryPressureSource;

impl NullMemoryPressureSource {
    pub fn new() -> Self {
        Self
    }
}

impl IMemoryPressureSource for NullMemoryPressureSource {
    fn current(&self) -> MemoryPressureLevel {
        MemoryPressureLevel::Normal
    }
    fn subscribe(&self, _handler: PressureHandler) -> PressureSubscription {
        PressureSubscription::empty()
    }
}

/// Handler registry shared between the source and its subscriptions. Each
/// handler is stored under a monotonically-increasing id so a subscription can
/// remove exactly its own entry.
#[derive(Default)]
struct HandlerRegistry {
    next_id: u64,
    handlers: Vec<(u64, PressureHandler)>,
}

/// Manually-driven [`IMemoryPressureSource`]. Hosting layers (or tests)
/// construct one and call [`ManualMemoryPressureSource::raise`] when the
/// platform publishes a pressure event. Thread-safe. 1:1 with the C#
/// `ManualMemoryPressureSource`.
pub struct ManualMemoryPressureSource {
    current: Mutex<MemoryPressureLevel>,
    registry: Arc<Mutex<HandlerRegistry>>,
}

impl Default for ManualMemoryPressureSource {
    fn default() -> Self {
        Self::new()
    }
}

impl ManualMemoryPressureSource {
    pub fn new() -> Self {
        Self {
            current: Mutex::new(MemoryPressureLevel::Normal),
            registry: Arc::new(Mutex::new(HandlerRegistry::default())),
        }
    }

    /// Publish a new pressure level. Idempotent for the same level — only
    /// transitions fire handlers. 1:1 with the C# `Raise`.
    pub fn raise(&self, level: MemoryPressureLevel) {
        let (previous, snapshot) = {
            let mut cur = self.current.lock().unwrap();
            if *cur == level {
                return;
            }
            let previous = *cur;
            *cur = level;
            let snapshot: Vec<PressureHandler> = self
                .registry
                .lock()
                .unwrap()
                .handlers
                .iter()
                .map(|(_, h)| Arc::clone(h))
                .collect();
            (previous, snapshot)
        };
        for h in snapshot {
            // error-isolated; pressure handlers must not break the source.
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| h(previous, level)));
        }
    }
}

impl IMemoryPressureSource for ManualMemoryPressureSource {
    fn current(&self) -> MemoryPressureLevel {
        *self.current.lock().unwrap()
    }

    fn subscribe(&self, handler: PressureHandler) -> PressureSubscription {
        let id = {
            let mut reg = self.registry.lock().unwrap();
            let id = reg.next_id;
            reg.next_id += 1;
            reg.handlers.push((id, handler));
            id
        };
        let registry = Arc::clone(&self.registry);
        PressureSubscription::new(move || {
            registry.lock().unwrap().handlers.retain(|(hid, _)| *hid != id);
        })
    }
}
