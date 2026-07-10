// network_connectivity.go
//
// Ports CircleAI.Networking.IConnectivityMonitor (IConnectivityMonitor.cs) and
// its mandated working in-memory implementation.
//
// IConnectivityMonitor observes connectivity state and emits changes. The C#
// WatchAsync is an IAsyncEnumerable that every watcher enumerates independently
// — a FAN-OUT stream: each subscriber sees every state change, not a competing
// slice of them.
//
// Go modelling:
//   ConnectivityState CurrentState { get; } -> CurrentState() ConnectivityState
//   NetworkContext GetSnapshot()            -> GetSnapshot() NetworkContext
//   IAsyncEnumerable<NetworkContext> Watch  -> Watch(ctx) <-chan NetworkContext
//
// Concurrency (Wave-1 lessons applied):
//   - Watch REGISTERS its subscriber synchronously before returning the channel,
//     so a Publish that races immediately after Watch() returns is not lost.
//   - Each subscriber owns an UNBOUNDED buffer, so Publish never blocks on a
//     slow watcher and no update is dropped under back-pressure.
//   - Publish snapshots the subscriber list UNDER the lock and writes to each
//     buffer OFF the lock; a watcher unsubscribing from within its own consumer
//     loop cannot deadlock the publisher.

package circleai

import (
	"context"
	"sync"
)

// ---------------------------------------------------------------------------
// IConnectivityMonitor — IConnectivityMonitor.cs
// ---------------------------------------------------------------------------

// IConnectivityMonitor observes connectivity state and emits changes.
type IConnectivityMonitor interface {
	// CurrentState returns the latest connectivity classification.
	CurrentState() ConnectivityState
	// GetSnapshot returns the full current NetworkContext.
	GetSnapshot() NetworkContext
	// Watch returns a fan-out stream of NetworkContext snapshots, one per state
	// change. The channel closes when ctx is cancelled or the monitor is closed.
	Watch(ctx context.Context) <-chan NetworkContext
}

// ---------------------------------------------------------------------------
// InMemoryConnectivityMonitor — working IConnectivityMonitor
// ---------------------------------------------------------------------------

// InMemoryConnectivityMonitor is a deterministic, driveable IConnectivityMonitor.
// Tests (or a real platform probe adapter) call Publish to feed it snapshots; it
// fans each snapshot out to every active Watch subscriber. Safe for concurrent
// use.
type InMemoryConnectivityMonitor struct {
	mu      sync.Mutex
	current NetworkContext
	subs    map[*connectivitySub]struct{}
	closed  bool
}

// connectivitySub is one Watch subscription: an unbounded buffer feeding a
// single consumer.
type connectivitySub struct {
	buf *unboundedChannel[NetworkContext]
}

// NewInMemoryConnectivityMonitor returns a monitor seeded with initial. Pass
// NewNetworkContextOffline() for a cold start.
func NewInMemoryConnectivityMonitor(initial NetworkContext) *InMemoryConnectivityMonitor {
	return &InMemoryConnectivityMonitor{
		current: initial,
		subs:    make(map[*connectivitySub]struct{}),
	}
}

// CurrentState returns the State of the latest snapshot.
func (m *InMemoryConnectivityMonitor) CurrentState() ConnectivityState {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.current.State
}

// GetSnapshot returns the latest NetworkContext.
func (m *InMemoryConnectivityMonitor) GetSnapshot() NetworkContext {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.current
}

// Publish updates the current snapshot and fans it out to every active watcher.
// Publishing after Close is a no-op. Membership is snapshotted under the lock;
// the per-subscriber writes happen off-lock so a watcher cannot deadlock the
// publisher.
func (m *InMemoryConnectivityMonitor) Publish(ctx NetworkContext) {
	m.mu.Lock()
	if m.closed {
		m.mu.Unlock()
		return
	}
	m.current = ctx
	subs := make([]*connectivitySub, 0, len(m.subs))
	for s := range m.subs {
		subs = append(subs, s)
	}
	m.mu.Unlock()

	for _, s := range subs {
		s.buf.Write(ctx)
	}
}

// Watch registers a new fan-out subscriber and returns its stream. The
// registration is synchronous: any Publish after this returns is seen. The
// stream closes when ctx is cancelled, or when Close completes the monitor.
func (m *InMemoryConnectivityMonitor) Watch(ctx context.Context) <-chan NetworkContext {
	sub := &connectivitySub{buf: newUnboundedChannel[NetworkContext]()}

	m.mu.Lock()
	if m.closed {
		// Already closed: hand back a completed, empty stream.
		m.mu.Unlock()
		sub.buf.Complete()
		return sub.buf.ReadAll(ctx)
	}
	m.subs[sub] = struct{}{}
	m.mu.Unlock()

	out := sub.buf.ReadAll(ctx)

	// When ctx is cancelled, deregister the subscriber so the monitor does not
	// leak buffers for gone watchers. Completing the buffer also unblocks its
	// reader if it were parked.
	if ctx.Done() != nil {
		go func() {
			<-ctx.Done()
			m.mu.Lock()
			delete(m.subs, sub)
			m.mu.Unlock()
			sub.buf.Complete()
		}()
	}
	return out
}

// SubscriberCount returns the number of active watchers. Useful in tests.
func (m *InMemoryConnectivityMonitor) SubscriberCount() int {
	m.mu.Lock()
	defer m.mu.Unlock()
	return len(m.subs)
}

// Close completes every watcher stream and rejects further Publish/Watch.
// Idempotent.
func (m *InMemoryConnectivityMonitor) Close() {
	m.mu.Lock()
	if m.closed {
		m.mu.Unlock()
		return
	}
	m.closed = true
	subs := make([]*connectivitySub, 0, len(m.subs))
	for s := range m.subs {
		subs = append(subs, s)
	}
	m.subs = make(map[*connectivitySub]struct{})
	m.mu.Unlock()

	for _, s := range subs {
		s.buf.Complete()
	}
}

var _ IConnectivityMonitor = (*InMemoryConnectivityMonitor)(nil)
