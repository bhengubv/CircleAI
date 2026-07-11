//! games_test.rs
//!
//! Ports the behaviour of `CircleAI.Games`: the timer game loop fanning ticks
//! out to subscribers, the in-memory input map, the scene graph, and the
//! fail-closed `Null*` backends.

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;
use std::time::{Duration as StdDuration, Instant};

use circle_ai::games::{
    GameTick, IGameLoop, IInputMap, ISceneGraph, InMemoryInputMap, InMemorySceneGraph, InputEvent,
    NullGameLoop, NullInputMap, NullSceneGraph, SceneNode, TimerGameLoop,
};

#[test]
fn timer_loop_fires_ticks_to_subscribers() {
    let loop_ = TimerGameLoop::new();
    assert_eq!(loop_.backend_id(), "timer");
    let count = Arc::new(AtomicU32::new(0));
    let c2 = Arc::clone(&count);
    let _sub = loop_.subscribe(Arc::new(move |_t: &GameTick| {
        c2.fetch_add(1, Ordering::SeqCst);
    }));

    loop_.start(100.0); // ~10ms/frame
    // Spin until we observe at least a couple of ticks (or time out).
    let deadline = Instant::now() + StdDuration::from_secs(2);
    while count.load(Ordering::SeqCst) < 2 && Instant::now() < deadline {
        std::thread::sleep(StdDuration::from_millis(5));
    }
    loop_.stop();
    assert!(count.load(Ordering::SeqCst) >= 2, "expected the loop to fire ticks");
}

#[test]
fn timer_loop_unsubscribe_stops_delivery() {
    let loop_ = TimerGameLoop::new();
    let count = Arc::new(AtomicU32::new(0));
    let c2 = Arc::clone(&count);
    let sub = loop_.subscribe(Arc::new(move |_t: &GameTick| {
        c2.fetch_add(1, Ordering::SeqCst);
    }));
    sub.unsubscribe(); // drop-based removal before start
    loop_.start(200.0);
    std::thread::sleep(StdDuration::from_millis(60));
    loop_.stop();
    assert_eq!(count.load(Ordering::SeqCst), 0);
}

#[test]
#[should_panic(expected = "target_fps must be positive")]
fn timer_loop_bad_fps_panics() {
    TimerGameLoop::new().start(0.0);
}

#[test]
fn input_map_fans_out_raised_events() {
    let map = InMemoryInputMap::new();
    assert_eq!(map.backend_id(), "in-memory");
    let seen = Arc::new(AtomicU32::new(0));
    let s2 = Arc::clone(&seen);
    let sub = map.subscribe(Arc::new(move |_e: &InputEvent| {
        s2.fetch_add(1, Ordering::SeqCst);
    }));
    map.raise(InputEvent::new("jump", None));
    map.raise(InputEvent::new("fire", None));
    assert_eq!(seen.load(Ordering::SeqCst), 2);

    sub.unsubscribe();
    map.raise(InputEvent::new("crouch", None));
    assert_eq!(seen.load(Ordering::SeqCst), 2); // no more delivery
}

#[test]
fn scene_graph_add_remove_snapshot() {
    let sg = InMemorySceneGraph::new();
    sg.add(SceneNode::new("n1", "cube", 1.0, 2.0, 3.0));
    sg.add(SceneNode::new("n2", "sphere", 0.0, 0.0, 0.0));
    assert_eq!(sg.snapshot().len(), 2);
    sg.remove("n1");
    let snap = sg.snapshot();
    assert_eq!(snap.len(), 1);
    assert_eq!(snap[0].node_id, "n2");
}

#[test]
#[should_panic(expected = "NodeId required")]
fn scene_graph_blank_node_panics() {
    InMemorySceneGraph::new().add(SceneNode::new("  ", "cube", 0.0, 0.0, 0.0));
}

#[test]
fn null_backends_are_inert() {
    let loop_ = NullGameLoop::new();
    assert_eq!(loop_.backend_id(), "null");
    loop_.start(60.0);
    let _sub = loop_.subscribe(Arc::new(|_t: &GameTick| {}));
    loop_.stop();

    let map = NullInputMap::new();
    assert_eq!(map.backend_id(), "null");
    let _s = map.subscribe(Arc::new(|_e: &InputEvent| {}));

    let sg = NullSceneGraph::new();
    assert_eq!(sg.backend_id(), "null");
    sg.add(SceneNode::new("n1", "cube", 0.0, 0.0, 0.0));
    assert!(sg.snapshot().is_empty());
}
