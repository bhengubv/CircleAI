//! games — CircleAI game-runtime primitives.
//!
//! Full Rust port of `src/CircleAI.Games/` (`Contracts.cs`, `InMemoryGames.cs`,
//! `NullImplementations.cs`):
//!
//! - Records [`GameTick`] / [`InputEvent`] / [`SceneNode`].
//! - Traits [`IGameLoop`], [`IInputMap`], [`ISceneGraph`].
//! - Real backends [`TimerGameLoop`] (a background-thread ticker fanning ticks
//!   out to subscribers), [`InMemoryInputMap`], [`InMemorySceneGraph`].
//! - Fail-closed [`NullGameLoop`], [`NullInputMap`], [`NullSceneGraph`].
//!
//! The C# async surface is projected sync-only: the `Func<T, ValueTask>`
//! subscriber becomes `Arc<dyn Fn(&T) + Send + Sync>`, the `IDisposable`
//! unsubscribe handle becomes the drop-based [`GameSubscription`], and the
//! `Task`/`ValueTask` returns collapse to synchronous calls. The timer loop uses
//! a [`std::thread`] rather than a `System.Threading.Timer`; subscribers are
//! snapshotted under the lock and invoked after releasing it (so a callback that
//! unsubscribes cannot deadlock).

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration as StdDuration, Instant};

use chrono::Duration;

/// (Games) A single game-loop tick.
///
/// Mirrors `sealed record GameTick(int Frame, TimeSpan Elapsed)`. `Elapsed` is a
/// [`chrono::Duration`] (the C# `TimeSpan`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GameTick {
    pub frame: i32,
    pub elapsed: Duration,
}

impl GameTick {
    /// Constructs a tick, mirroring the positional C# record constructor.
    pub fn new(frame: i32, elapsed: Duration) -> Self {
        Self { frame, elapsed }
    }
}

/// (Games) An input event.
///
/// Mirrors `sealed record InputEvent(string Action,
/// IReadOnlyDictionary<string,string>? Payload = null)`. The optional payload
/// becomes `Option<HashMap<String, String>>`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InputEvent {
    pub action: String,
    pub payload: Option<HashMap<String, String>>,
}

impl InputEvent {
    /// Constructs an input event, mirroring the positional C# record constructor
    /// (payload defaults to `None`).
    pub fn new(action: impl Into<String>, payload: Option<HashMap<String, String>>) -> Self {
        Self {
            action: action.into(),
            payload,
        }
    }
}

/// (Games) A scene-graph node.
///
/// Mirrors `sealed record SceneNode(string NodeId, string Kind, double X,
/// double Y, double Z)`.
#[derive(Debug, Clone, PartialEq)]
pub struct SceneNode {
    pub node_id: String,
    pub kind: String,
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

impl SceneNode {
    /// Constructs a node, mirroring the positional C# record constructor.
    pub fn new(node_id: impl Into<String>, kind: impl Into<String>, x: f64, y: f64, z: f64) -> Self {
        Self {
            node_id: node_id.into(),
            kind: kind.into(),
            x,
            y,
            z,
        }
    }
}

/// A synchronous game-tick handler — the sync-only analogue of the C#
/// `Func<GameTick, ValueTask>`.
pub type TickHandler = Arc<dyn Fn(&GameTick) + Send + Sync>;
/// A synchronous input handler — the sync-only analogue of the C#
/// `Func<InputEvent, ValueTask>`.
pub type InputHandler = Arc<dyn Fn(&InputEvent) + Send + Sync>;

/// Drop-based unsubscribe handle. Dropping it (or calling
/// [`GameSubscription::unsubscribe`]) removes the associated handler. Mirrors the
/// C# `IDisposable` returned by `Subscribe`.
pub struct GameSubscription {
    remover: Option<Box<dyn FnOnce() + Send + Sync>>,
}

impl GameSubscription {
    fn new(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self {
            remover: Some(Box::new(remover)),
        }
    }

    /// A subscription that does nothing on drop (used by the `Null*` backends).
    pub fn noop() -> Self {
        Self { remover: None }
    }

    /// Explicit unsubscribe (equivalent to dropping; idempotent).
    pub fn unsubscribe(mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

impl Drop for GameSubscription {
    fn drop(&mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

/// (Games) A frame-driving game loop.
///
/// Mirrors `interface IGameLoop : IAsyncDisposable`. `IAsyncDisposable` becomes
/// plain `Drop`.
pub trait IGameLoop {
    /// A stable identifier for the backing implementation.
    fn backend_id(&self) -> &str;
    /// Starts the loop at `target_fps`. Panics on `target_fps <= 0` or when
    /// already started (mirrors the C# `ArgumentOutOfRangeException` /
    /// `InvalidOperationException`).
    fn start(&self, target_fps: f64);
    /// Stops the loop (idempotent).
    fn stop(&self);
    /// Subscribes `handler` to future ticks; returns a drop-based unsubscribe handle.
    fn subscribe(&self, handler: TickHandler) -> GameSubscription;
}

/// (Games) A source of input events.
///
/// Mirrors `interface IInputMap`.
pub trait IInputMap {
    /// A stable identifier for the backing implementation.
    fn backend_id(&self) -> &str;
    /// Subscribes `handler` to future input events; returns a drop-based handle.
    fn subscribe(&self, handler: InputHandler) -> GameSubscription;
}

/// (Games) A scene graph.
///
/// Mirrors `interface ISceneGraph`.
pub trait ISceneGraph {
    /// A stable identifier for the backing implementation.
    fn backend_id(&self) -> &str;
    /// Adds (or overwrites) a node. Panics on a blank node id (mirrors the C#
    /// `ArgumentException`).
    fn add(&self, node: SceneNode);
    /// Removes a node by id. Panics on a blank id (mirrors the C#
    /// `ArgumentException`).
    fn remove(&self, node_id: &str);
    /// A snapshot of all current nodes.
    fn snapshot(&self) -> Vec<SceneNode>;
}

// ── TimerGameLoop ────────────────────────────────────────────────────────────

struct LoopShared {
    subs: Mutex<Vec<(u64, TickHandler)>>,
    frame: AtomicU64,
    running: AtomicBool,
}

/// (Games) Real [`IGameLoop`] driven by a background thread.
///
/// Mirrors `sealed class TimerGameLoop`. The C# `System.Threading.Timer` becomes
/// a [`std::thread`] that sleeps `max(1, 1000/fps)` ms per frame and fans each
/// [`GameTick`] out to every current subscriber (snapshotting under the lock,
/// invoking after release, swallowing panics like the C# try/catch).
pub struct TimerGameLoop {
    shared: Arc<LoopShared>,
    thread: Mutex<Option<JoinHandle<()>>>,
    next_id: AtomicU64,
}

impl TimerGameLoop {
    /// Creates a stopped loop.
    pub fn new() -> Self {
        Self {
            shared: Arc::new(LoopShared {
                subs: Mutex::new(Vec::new()),
                frame: AtomicU64::new(0),
                running: AtomicBool::new(false),
            }),
            thread: Mutex::new(None),
            next_id: AtomicU64::new(0),
        }
    }
}

impl Default for TimerGameLoop {
    fn default() -> Self {
        Self::new()
    }
}

impl IGameLoop for TimerGameLoop {
    fn backend_id(&self) -> &str {
        "timer"
    }

    fn start(&self, target_fps: f64) {
        if target_fps <= 0.0 {
            panic!("target_fps must be positive");
        }
        let mut guard = self.thread.lock().unwrap();
        if guard.is_some() {
            panic!("already started");
        }
        let ms = ((1000.0 / target_fps) as u64).max(1);
        let shared = Arc::clone(&self.shared);
        shared.running.store(true, Ordering::SeqCst);
        shared.frame.store(0, Ordering::SeqCst);
        let start = Instant::now();
        let handle = std::thread::spawn(move || {
            while shared.running.load(Ordering::SeqCst) {
                std::thread::sleep(StdDuration::from_millis(ms));
                if !shared.running.load(Ordering::SeqCst) {
                    break;
                }
                let frame = shared.frame.fetch_add(1, Ordering::SeqCst) as i32 + 1;
                let elapsed = Duration::from_std(start.elapsed()).unwrap_or_else(|_| Duration::zero());
                let tick = GameTick::new(frame, elapsed);
                // Snapshot subscribers under the lock, invoke after releasing it.
                let snap: Vec<TickHandler> = {
                    let subs = shared.subs.lock().unwrap();
                    subs.iter().map(|(_, h)| Arc::clone(h)).collect()
                };
                for h in snap {
                    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| h(&tick)));
                }
            }
        });
        *guard = Some(handle);
    }

    fn stop(&self) {
        self.shared.running.store(false, Ordering::SeqCst);
        let handle = self.thread.lock().unwrap().take();
        if let Some(h) = handle {
            let _ = h.join();
        }
    }

    fn subscribe(&self, handler: TickHandler) -> GameSubscription {
        let id = self.next_id.fetch_add(1, Ordering::SeqCst);
        self.shared.subs.lock().unwrap().push((id, handler));
        let shared = Arc::clone(&self.shared);
        GameSubscription::new(move || {
            shared.subs.lock().unwrap().retain(|(hid, _)| *hid != id);
        })
    }
}

impl Drop for TimerGameLoop {
    fn drop(&mut self) {
        // Mirrors the C# `DisposeAsync` → `StopAsync`.
        self.stop();
    }
}

// ── InMemoryInputMap ─────────────────────────────────────────────────────────

/// (Games) Real [`IInputMap`] that fans raised events out to subscribers.
///
/// Mirrors `sealed class InMemoryInputMap`. The subscriber list lives behind an
/// `Arc<Mutex<..>>` so each subscription's drop-based remover can reach it.
pub struct InMemoryInputMap {
    subs: Arc<Mutex<Vec<(u64, InputHandler)>>>,
    next_id: AtomicU64,
}

impl InMemoryInputMap {
    /// Creates an empty input map.
    pub fn new() -> Self {
        Self {
            subs: Arc::new(Mutex::new(Vec::new())),
            next_id: AtomicU64::new(0),
        }
    }

    /// Raises `ev`, invoking every current subscriber (snapshotted under the lock,
    /// invoked after release; panics swallowed like the C# try/catch).
    pub fn raise(&self, ev: InputEvent) {
        let snap: Vec<InputHandler> = {
            let subs = self.subs.lock().unwrap();
            subs.iter().map(|(_, h)| Arc::clone(h)).collect()
        };
        for h in snap {
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| h(&ev)));
        }
    }
}

impl Default for InMemoryInputMap {
    fn default() -> Self {
        Self::new()
    }
}

impl IInputMap for InMemoryInputMap {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn subscribe(&self, handler: InputHandler) -> GameSubscription {
        let id = self.next_id.fetch_add(1, Ordering::SeqCst);
        self.subs.lock().unwrap().push((id, handler));
        let subs = Arc::clone(&self.subs);
        GameSubscription::new(move || {
            subs.lock().unwrap().retain(|(hid, _)| *hid != id);
        })
    }
}

// ── InMemorySceneGraph ───────────────────────────────────────────────────────

/// (Games) Real [`ISceneGraph`] backed by an id-keyed map.
///
/// Mirrors `sealed class InMemorySceneGraph`.
pub struct InMemorySceneGraph {
    nodes: Mutex<HashMap<String, SceneNode>>,
}

impl InMemorySceneGraph {
    /// Creates an empty scene graph.
    pub fn new() -> Self {
        Self {
            nodes: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemorySceneGraph {
    fn default() -> Self {
        Self::new()
    }
}

impl ISceneGraph for InMemorySceneGraph {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn add(&self, node: SceneNode) {
        if node.node_id.trim().is_empty() {
            panic!("NodeId required");
        }
        self.nodes.lock().unwrap().insert(node.node_id.clone(), node);
    }

    fn remove(&self, node_id: &str) {
        if node_id.trim().is_empty() {
            panic!("nodeId required");
        }
        self.nodes.lock().unwrap().remove(node_id);
    }

    fn snapshot(&self) -> Vec<SceneNode> {
        self.nodes.lock().unwrap().values().cloned().collect()
    }
}

// ── Null backends ────────────────────────────────────────────────────────────

/// (Games) Fail-closed [`IGameLoop`] — starts/stops/subscribes but emits nothing.
///
/// Mirrors `sealed class NullGameLoop`.
pub struct NullGameLoop;

impl NullGameLoop {
    /// Creates the null loop.
    pub fn new() -> Self {
        Self
    }
}

impl Default for NullGameLoop {
    fn default() -> Self {
        Self::new()
    }
}

impl IGameLoop for NullGameLoop {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn start(&self, _target_fps: f64) {}
    fn stop(&self) {}
    fn subscribe(&self, _handler: TickHandler) -> GameSubscription {
        GameSubscription::noop()
    }
}

/// (Games) Fail-closed [`IInputMap`].
///
/// Mirrors `sealed class NullInputMap`.
pub struct NullInputMap;

impl NullInputMap {
    /// Creates the null input map.
    pub fn new() -> Self {
        Self
    }
}

impl Default for NullInputMap {
    fn default() -> Self {
        Self::new()
    }
}

impl IInputMap for NullInputMap {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn subscribe(&self, _handler: InputHandler) -> GameSubscription {
        GameSubscription::noop()
    }
}

/// (Games) Fail-closed [`ISceneGraph`].
///
/// Mirrors `sealed class NullSceneGraph`.
pub struct NullSceneGraph;

impl NullSceneGraph {
    /// Creates the null scene graph.
    pub fn new() -> Self {
        Self
    }
}

impl Default for NullSceneGraph {
    fn default() -> Self {
        Self::new()
    }
}

impl ISceneGraph for NullSceneGraph {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn add(&self, _node: SceneNode) {}
    fn remove(&self, _node_id: &str) {}
    fn snapshot(&self) -> Vec<SceneNode> {
        Vec::new()
    }
}
