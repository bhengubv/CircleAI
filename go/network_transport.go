// network_transport.go
//
// Ports CircleAI.Networking.INetworkTransport (INetworkTransport.cs) and its
// mandated working in-memory implementation.
//
// INetworkTransport is the unified send/receive abstraction for ONE transport
// kind — the seam the 10 concrete transports implement. A real socket is
// injected behind it; InMemoryNetworkTransport plays the socket's role with a
// deterministic in-process fabric so the abstraction can be exercised without a
// wire.
//
// Go modelling of the C# surface:
//   Task StartAsync/StopAsync/SendAsync(ct)      -> Start/Stop/Send(ctx) error
//   IAsyncEnumerable<NetworkPayload> ReceiveAsync -> Receive(ctx) <-chan NetworkPayload
// The receive stream closes when ctx is cancelled or the transport is stopped,
// exactly as an IAsyncEnumerable completes on its enumerator's cancellation.
//
// Concurrency (Wave-1 lessons applied):
//   - Deliveries into a transport go through an unboundedChannel: a payload
//     published before any Receive() consumer attaches is BUFFERED, never lost.
//   - Snapshotting of fabric membership happens under the fabric lock; the
//     actual enqueue onto a peer happens after the lock is released, so a slow
//     or (dis)connecting peer can never deadlock the sender.

package circleai

import (
	"context"
	"errors"
	"sync"
)

// ---------------------------------------------------------------------------
// INetworkTransport — INetworkTransport.cs
// ---------------------------------------------------------------------------

// INetworkTransport is the unified send/receive abstraction for a single
// transport kind.
type INetworkTransport interface {
	// Kind identifies which transport this instance speaks.
	Kind() TransportKind
	// IsAvailable reports whether the transport can currently carry traffic
	// (started and not stopped).
	IsAvailable() bool

	// Start brings the transport up. Idempotent.
	Start(ctx context.Context) error
	// Stop tears the transport down and completes any active Receive streams.
	// Idempotent.
	Stop(ctx context.Context) error

	// Send transmits payload. Returns an error if the transport is not started,
	// or ctx is cancelled.
	Send(ctx context.Context, payload NetworkPayload) error

	// Receive returns a stream of inbound payloads. The channel is closed when
	// ctx is cancelled or the transport is stopped. Payloads delivered before
	// the first Receive call are buffered and replayed in FIFO order.
	Receive(ctx context.Context) <-chan NetworkPayload
}

// ---------------------------------------------------------------------------
// InMemoryTransportFabric — the injected "socket" switchboard
// ---------------------------------------------------------------------------

// InMemoryTransportFabric is the in-process substitute for a physical medium.
// Every InMemoryNetworkTransport built against the same fabric+kind shares a
// broadcast domain: a Send on one is delivered to every OTHER started transport
// of the same TransportKind on the fabric (loopback excludes the sender), plus
// any transport that registered as a wildcard sink. This mirrors how a real
// transport medium fans a frame to its peers, letting two endpoints converge in
// tests with no OS sockets.
//
// It is the dependency injected behind INetworkTransport, honouring the port's
// "a real socket is injected behind INetworkTransport" contract.
type InMemoryTransportFabric struct {
	mu      sync.Mutex
	members map[TransportKind]map[*InMemoryNetworkTransport]struct{}
}

// NewInMemoryTransportFabric returns an empty fabric.
func NewInMemoryTransportFabric() *InMemoryTransportFabric {
	return &InMemoryTransportFabric{
		members: make(map[TransportKind]map[*InMemoryNetworkTransport]struct{}),
	}
}

func (f *InMemoryTransportFabric) join(t *InMemoryNetworkTransport) {
	f.mu.Lock()
	defer f.mu.Unlock()
	set, ok := f.members[t.kind]
	if !ok {
		set = make(map[*InMemoryNetworkTransport]struct{})
		f.members[t.kind] = set
	}
	set[t] = struct{}{}
}

func (f *InMemoryTransportFabric) leave(t *InMemoryNetworkTransport) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if set, ok := f.members[t.kind]; ok {
		delete(set, t)
	}
}

// peersOf snapshots the other transports of kind under the lock, so delivery
// happens off-lock.
func (f *InMemoryTransportFabric) peersOf(kind TransportKind, sender *InMemoryNetworkTransport) []*InMemoryNetworkTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	set := f.members[kind]
	peers := make([]*InMemoryNetworkTransport, 0, len(set))
	for m := range set {
		if m != sender {
			peers = append(peers, m)
		}
	}
	return peers
}

// deliver routes payload from sender to every same-kind peer. Membership is
// snapshotted under the lock; enqueue onto each peer happens off-lock.
func (f *InMemoryTransportFabric) deliver(sender *InMemoryNetworkTransport, payload NetworkPayload) {
	for _, peer := range f.peersOf(sender.kind, sender) {
		peer.inbox.Write(payload)
	}
}

// ---------------------------------------------------------------------------
// InMemoryNetworkTransport — working INetworkTransport
// ---------------------------------------------------------------------------

// InMemoryNetworkTransport is a deterministic INetworkTransport backed by an
// InMemoryTransportFabric. It is safe for concurrent use.
type InMemoryNetworkTransport struct {
	kind   TransportKind
	fabric *InMemoryTransportFabric

	mu      sync.Mutex
	started bool
	inbox   *unboundedChannel[NetworkPayload]
}

// NewInMemoryNetworkTransport creates a transport of kind bound to fabric. Pass
// a fresh fabric for an isolated loopback, or a shared fabric to wire several
// endpoints together. fabric must not be nil.
func NewInMemoryNetworkTransport(kind TransportKind, fabric *InMemoryTransportFabric) (*InMemoryNetworkTransport, error) {
	if fabric == nil {
		return nil, errors.New("fabric required")
	}
	return &InMemoryNetworkTransport{
		kind:   kind,
		fabric: fabric,
		inbox:  newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns the transport kind.
func (t *InMemoryNetworkTransport) Kind() TransportKind { return t.kind }

// IsAvailable reports whether the transport is started.
func (t *InMemoryNetworkTransport) IsAvailable() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.started
}

// Start joins the fabric and marks the transport available. Idempotent.
func (t *InMemoryNetworkTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.started {
		t.mu.Unlock()
		return nil
	}
	// A fresh inbox on (re)start so a previous Stop's completion does not leak
	// into the new session; readers from the old session already drained/closed.
	t.inbox = newUnboundedChannel[NetworkPayload]()
	t.started = true
	t.mu.Unlock()

	t.fabric.join(t)
	return nil
}

// Stop leaves the fabric, marks the transport unavailable, and completes the
// inbox so every active Receive stream drains and closes. Idempotent.
func (t *InMemoryNetworkTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.started {
		t.mu.Unlock()
		return nil
	}
	t.started = false
	inbox := t.inbox
	t.mu.Unlock()

	t.fabric.leave(t)
	inbox.Complete()
	return nil
}

// Send delivers payload to same-kind peers on the fabric. It stamps SourceID
// only if unset is not required — the payload is forwarded as-is (the mesh /
// message layers own addressing). Returns an error if the transport is not
// started or ctx is cancelled.
func (t *InMemoryNetworkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	started := t.started
	t.mu.Unlock()
	if !started {
		return errors.New("transport not started")
	}
	t.fabric.deliver(t, payload)
	return nil
}

// Receive returns a stream of inbound payloads for this transport. Payloads
// buffered before this call are replayed first (unbounded buffering upholds the
// "published-before-subscribe is not lost" guarantee). The stream closes on
// ctx cancellation or Stop.
func (t *InMemoryNetworkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbox := t.inbox
	t.mu.Unlock()
	return inbox.ReadAll(ctx)
}

var _ INetworkTransport = (*InMemoryNetworkTransport)(nil)
