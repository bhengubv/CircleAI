// games_runtime.go
//
// Ports the CircleAI.Games runtime (Contracts.cs, InMemoryGames.cs,
// NullImplementations.cs):
//   GameTick / InputEvent / SceneNode (records)   -> value structs
//   IGameLoop / IInputMap / ISceneGraph (contracts)-> interfaces (I dropped)
//   TimerGameLoop / InMemoryInputMap / InMemorySceneGraph -> in-memory impls
//   NullGameLoop / NullInputMap / NullSceneGraph   -> null impls
//
// Go mapping of the C# async surface:
//   Func<GameTick, ValueTask>  -> GameTickHandler  func(GameTick)
//   Func<InputEvent, ValueTask>-> InputEventHandler func(InputEvent)
//   IDisposable (from Subscribe)-> func() unsubscribe (package convention,
//                                  mirrors aether_events.go)
//   ValueTask / Task           -> synchronous methods (the C# bodies complete
//                                  synchronously) taking a context.Context in
//                                  place of the CancellationToken.
//
// Subscribers are held in an id-keyed map so the returned unsubscribe func
// removes exactly its own handler (Go funcs are not comparable, so the C#
// remove-by-delegate becomes remove-by-token).
//
// CONCURRENCY: the ticker/input fan-out snapshots the subscriber set under the
// lock, unlocks, then invokes each handler in its own goroutine (matching the
// C# `_ = s(tick)` fire-and-forget that never blocks the timer and swallows
// handler panics). Subscribe/unsubscribe never runs a handler while holding the
// lock. StartAsync spawns the ticker goroutine; the frame counter is read
// atomically so stop is race-free.

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// GameTick is one simulation frame. Ports the GameTick record. Elapsed is time
// since the loop started.
type GameTick struct {
	Frame   int
	Elapsed time.Duration
}

// InputEvent is a raised input action. Ports the InputEvent record. Payload is
// an optional string map (nil when absent).
type InputEvent struct {
	Action  string
	Payload map[string]string
}

// SceneNode is a positioned node in the scene graph. Ports the SceneNode record.
type SceneNode struct {
	NodeId string
	Kind   string
	X      float64
	Y      float64
	Z      float64
}

// GameTickHandler receives simulation ticks. Ports Func<GameTick, ValueTask>.
type GameTickHandler func(GameTick)

// InputEventHandler receives input events. Ports Func<InputEvent, ValueTask>.
type InputEventHandler func(InputEvent)

// GameLoop drives a simulation clock and fans ticks out to subscribers. Ports
// IGameLoop (IAsyncDisposable -> Close).
type GameLoop interface {
	BackendId() string
	// StartAsync starts the loop at targetFps. Errors if already started or fps<=0.
	StartAsync(ctx context.Context, targetFps float64) error
	// StopAsync stops the loop.
	StopAsync(ctx context.Context) error
	// Subscribe registers a tick handler and returns an unsubscribe func.
	Subscribe(handler GameTickHandler) func()
	// Close disposes the loop (ports DisposeAsync); stops the ticker.
	Close() error
}

// InputMap fans raised input events out to subscribers. Ports IInputMap.
type InputMap interface {
	BackendId() string
	// Subscribe registers an input handler and returns an unsubscribe func.
	Subscribe(handler InputEventHandler) func()
}

// SceneGraph is a mutable set of scene nodes. Ports ISceneGraph.
type SceneGraph interface {
	BackendId() string
	// Add stores (or replaces by NodeId) a node. Errors on blank NodeId.
	Add(ctx context.Context, node SceneNode) error
	// Remove deletes a node by id. Errors on blank id.
	Remove(ctx context.Context, nodeId string) error
	// Snapshot returns all nodes.
	Snapshot(ctx context.Context) ([]SceneNode, error)
}

// ---------------------------------------------------------------------------
// TimerGameLoop
// ---------------------------------------------------------------------------

// TimerGameLoop is a ticker-driven GameLoop. Ports TimerGameLoop.
type TimerGameLoop struct {
	mu      sync.Mutex
	subs    map[int64]GameTickHandler
	nextID  int64
	frame   int64
	start   time.Time
	stop    chan struct{}
	ticker  *time.Ticker
	running bool
}

// NewTimerGameLoop constructs a stopped loop with no subscribers.
func NewTimerGameLoop() *TimerGameLoop {
	return &TimerGameLoop{subs: make(map[int64]GameTickHandler)}
}

// BackendId returns "timer". Ports BackendId.
func (l *TimerGameLoop) BackendId() string { return "timer" }

// StartAsync starts the ticker at targetFps. Ports StartAsync (throws on
// fps<=0 or already-started -> error).
func (l *TimerGameLoop) StartAsync(ctx context.Context, targetFps float64) error {
	if targetFps <= 0 {
		return errors.New("targetFps out of range")
	}
	l.mu.Lock()
	if l.running {
		l.mu.Unlock()
		return errors.New("already started")
	}
	ms := int(1000.0 / targetFps)
	if ms < 1 {
		ms = 1
	}
	l.start = time.Now().UTC()
	l.stop = make(chan struct{})
	l.ticker = time.NewTicker(time.Duration(ms) * time.Millisecond)
	l.running = true
	stop := l.stop
	ticker := l.ticker
	l.mu.Unlock()

	go func() {
		for {
			select {
			case <-stop:
				return
			case <-ticker.C:
				l.onTick()
			}
		}
	}()
	return nil
}

// StopAsync stops the ticker. Ports StopAsync (idempotent).
func (l *TimerGameLoop) StopAsync(ctx context.Context) error {
	l.mu.Lock()
	if l.running {
		l.ticker.Stop()
		close(l.stop)
		l.ticker = nil
		l.running = false
	}
	l.mu.Unlock()
	return nil
}

// Subscribe registers a tick handler and returns an unsubscribe func. Ports
// Subscribe (IDisposable -> func()).
func (l *TimerGameLoop) Subscribe(handler GameTickHandler) func() {
	if handler == nil {
		panic("handler required")
	}
	l.mu.Lock()
	id := l.nextID
	l.nextID++
	l.subs[id] = handler
	l.mu.Unlock()
	return func() {
		l.mu.Lock()
		delete(l.subs, id)
		l.mu.Unlock()
	}
}

// Close disposes the loop. Ports DisposeAsync.
func (l *TimerGameLoop) Close() error { return l.StopAsync(context.Background()) }

// onTick increments the frame, snapshots subscribers, and fans the tick out to
// each in its own goroutine (matching the fire-and-forget C# fan-out).
func (l *TimerGameLoop) onTick() {
	frame := atomic.AddInt64(&l.frame, 1)
	l.mu.Lock()
	tick := GameTick{Frame: int(frame), Elapsed: time.Since(l.start)}
	snap := make([]GameTickHandler, 0, len(l.subs))
	for _, h := range l.subs {
		snap = append(snap, h)
	}
	l.mu.Unlock()
	for _, h := range snap {
		h := h
		go func() {
			defer func() { _ = recover() }()
			h(tick)
		}()
	}
}

// ---------------------------------------------------------------------------
// InMemoryInputMap
// ---------------------------------------------------------------------------

// InMemoryInputMap is an in-memory InputMap. Ports InMemoryInputMap.
type InMemoryInputMap struct {
	mu     sync.Mutex
	subs   map[int64]InputEventHandler
	nextID int64
}

// NewInMemoryInputMap constructs an empty input map.
func NewInMemoryInputMap() *InMemoryInputMap {
	return &InMemoryInputMap{subs: make(map[int64]InputEventHandler)}
}

// BackendId returns "in-memory". Ports BackendId.
func (m *InMemoryInputMap) BackendId() string { return "in-memory" }

// Raise fans an input event out to all subscribers. Ports Raise. Each handler
// runs in its own goroutine; panics are swallowed (matching the C# try/catch).
func (m *InMemoryInputMap) Raise(ev InputEvent) {
	m.mu.Lock()
	snap := make([]InputEventHandler, 0, len(m.subs))
	for _, h := range m.subs {
		snap = append(snap, h)
	}
	m.mu.Unlock()
	for _, h := range snap {
		h := h
		go func() {
			defer func() { _ = recover() }()
			h(ev)
		}()
	}
}

// Subscribe registers an input handler and returns an unsubscribe func. Ports
// Subscribe (IDisposable -> func()).
func (m *InMemoryInputMap) Subscribe(handler InputEventHandler) func() {
	if handler == nil {
		panic("handler required")
	}
	m.mu.Lock()
	id := m.nextID
	m.nextID++
	m.subs[id] = handler
	m.mu.Unlock()
	return func() {
		m.mu.Lock()
		delete(m.subs, id)
		m.mu.Unlock()
	}
}

// ---------------------------------------------------------------------------
// InMemorySceneGraph
// ---------------------------------------------------------------------------

// InMemorySceneGraph is an in-memory SceneGraph. Ports InMemorySceneGraph.
type InMemorySceneGraph struct {
	mu    sync.RWMutex
	nodes map[string]SceneNode
}

// NewInMemorySceneGraph constructs an empty scene graph.
func NewInMemorySceneGraph() *InMemorySceneGraph {
	return &InMemorySceneGraph{nodes: make(map[string]SceneNode)}
}

// BackendId returns "in-memory". Ports BackendId.
func (g *InMemorySceneGraph) BackendId() string { return "in-memory" }

// Add stores (or replaces by NodeId) a node. Ports AddAsync (throws on blank
// NodeId -> error).
func (g *InMemorySceneGraph) Add(ctx context.Context, node SceneNode) error {
	if strings.TrimSpace(node.NodeId) == "" {
		return errors.New("NodeId required")
	}
	g.mu.Lock()
	g.nodes[node.NodeId] = node
	g.mu.Unlock()
	return nil
}

// Remove deletes a node by id. Ports RemoveAsync (throws on blank id -> error).
func (g *InMemorySceneGraph) Remove(ctx context.Context, nodeId string) error {
	if strings.TrimSpace(nodeId) == "" {
		return errors.New("nodeId required")
	}
	g.mu.Lock()
	delete(g.nodes, nodeId)
	g.mu.Unlock()
	return nil
}

// Snapshot returns all nodes. Ports SnapshotAsync.
func (g *InMemorySceneGraph) Snapshot(ctx context.Context) ([]SceneNode, error) {
	g.mu.RLock()
	out := make([]SceneNode, 0, len(g.nodes))
	for _, n := range g.nodes {
		out = append(out, n)
	}
	g.mu.RUnlock()
	return out, nil
}

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullGameLoop is a no-op GameLoop. Ports NullGameLoop.
type NullGameLoop struct{}

func (NullGameLoop) BackendId() string                                 { return "null" }
func (NullGameLoop) StartAsync(ctx context.Context, fps float64) error { return nil }
func (NullGameLoop) StopAsync(ctx context.Context) error               { return nil }
func (NullGameLoop) Subscribe(handler GameTickHandler) func()          { return func() {} }
func (NullGameLoop) Close() error                                      { return nil }

// NullInputMap is a no-op InputMap. Ports NullInputMap.
type NullInputMap struct{}

func (NullInputMap) BackendId() string                          { return "null" }
func (NullInputMap) Subscribe(handler InputEventHandler) func() { return func() {} }

// NullSceneGraph is a no-op SceneGraph. Ports NullSceneGraph.
type NullSceneGraph struct{}

func (NullSceneGraph) BackendId() string                           { return "null" }
func (NullSceneGraph) Add(ctx context.Context, n SceneNode) error  { return nil }
func (NullSceneGraph) Remove(ctx context.Context, id string) error { return nil }
func (NullSceneGraph) Snapshot(ctx context.Context) ([]SceneNode, error) {
	return []SceneNode{}, nil
}

// Interface guards.
var (
	_ GameLoop   = (*TimerGameLoop)(nil)
	_ InputMap   = (*InMemoryInputMap)(nil)
	_ SceneGraph = (*InMemorySceneGraph)(nil)
	_ GameLoop   = NullGameLoop{}
	_ InputMap   = NullInputMap{}
	_ SceneGraph = NullSceneGraph{}
)
