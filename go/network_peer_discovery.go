// network_peer_discovery.go
//
// Supplies a standalone deterministic implementation of the
// CircleAI.Networking.IPeerDiscovery abstraction (IPeerDiscovery.cs). The
// interface itself — and the transport-specific discoverers AetherPeerDiscovery
// (network_aethernet.go) and WiFiPeerDiscovery (network_wifi.go) — are already
// ported; this adds an InMemoryPeerDiscovery so the abstraction can be exercised
// without any transport wired in (mDNS group / BLE advertising domain modelled by
// an in-process PeerAnnouncePlane).
//
// Go modelling of the C# surface (as fixed by the existing interface):
//   IAsyncEnumerable<PeerInfo> DiscoverAsync([EnumeratorCancellation] ct)
//                                          -> Discover(ctx) <-chan PeerInfo
//   Task AnnounceAsync(PeerInfo localInfo, ct)
//                                          -> Announce(ctx, localInfo) error
// The discovery stream closes when ctx is cancelled (an IAsyncEnumerable
// completes on its enumerator's cancellation).
//
// Concurrency (Wave-1 lessons applied):
//   - Discover REGISTERS its subscriber synchronously before returning the
//     channel, so an Announce racing immediately after Discover() returns is
//     seen, never lost.
//   - Each subscriber owns an UNBOUNDED buffer, so Announce never blocks on a
//     slow discoverer and no peer announcement is dropped under back-pressure.
//   - Announce snapshots the subscriber list UNDER the lock and writes to each
//     buffer OFF the lock, so a subscriber unsubscribing from within its own
//     consumer loop cannot deadlock the announcer.

package circleai

import (
	"context"
	"sync"
)

// ---------------------------------------------------------------------------
// InMemoryPeerDiscovery — working IPeerDiscovery
// ---------------------------------------------------------------------------

// InMemoryPeerDiscovery is a deterministic IPeerDiscovery backed by an in-process
// presence plane. Several instances sharing one PeerAnnouncePlane form a discovery
// domain: an Announce on any instance is streamed to every Discover subscriber on
// the plane (including the announcer's own subscribers, mirroring how a node also
// hears its own beacon reflected on a real medium). Safe for concurrent use.
type InMemoryPeerDiscovery struct {
	plane *PeerAnnouncePlane

	mu     sync.Mutex
	subs   map[*peerDiscoverySub]struct{}
	closed bool
}

// peerDiscoverySub is one Discover subscription: an unbounded buffer feeding a
// single consumer.
type peerDiscoverySub struct {
	buf *unboundedChannel[PeerInfo]
}

// PeerAnnouncePlane is the in-process presence medium shared by
// InMemoryPeerDiscovery instances. It is the injected substitute for the physical
// discovery channel (mDNS multicast group, BLE advertising domain, ...). Safe for
// concurrent use.
type PeerAnnouncePlane struct {
	mu      sync.Mutex
	members map[*InMemoryPeerDiscovery]struct{}
}

// NewPeerAnnouncePlane returns an empty presence plane.
func NewPeerAnnouncePlane() *PeerAnnouncePlane {
	return &PeerAnnouncePlane{members: make(map[*InMemoryPeerDiscovery]struct{})}
}

func (p *PeerAnnouncePlane) join(d *InMemoryPeerDiscovery) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.members[d] = struct{}{}
}

func (p *PeerAnnouncePlane) leave(d *InMemoryPeerDiscovery) {
	p.mu.Lock()
	defer p.mu.Unlock()
	delete(p.members, d)
}

// membersSnapshot copies the plane membership under the lock so the fan-out
// happens off-lock.
func (p *PeerAnnouncePlane) membersSnapshot() []*InMemoryPeerDiscovery {
	p.mu.Lock()
	defer p.mu.Unlock()
	out := make([]*InMemoryPeerDiscovery, 0, len(p.members))
	for m := range p.members {
		out = append(out, m)
	}
	return out
}

// NewInMemoryPeerDiscovery creates a discoverer on plane. Pass a fresh plane for
// an isolated presence domain or a shared plane to wire several nodes together.
// plane must not be nil.
func NewInMemoryPeerDiscovery(plane *PeerAnnouncePlane) *InMemoryPeerDiscovery {
	if plane == nil {
		plane = NewPeerAnnouncePlane()
	}
	d := &InMemoryPeerDiscovery{
		plane: plane,
		subs:  make(map[*peerDiscoverySub]struct{}),
	}
	plane.join(d)
	return d
}

// Discover registers a new subscriber and returns its peer stream. The
// registration is synchronous: any Announce after this returns is seen. The
// stream closes when ctx is cancelled or the discoverer is closed.
func (d *InMemoryPeerDiscovery) Discover(ctx context.Context) <-chan PeerInfo {
	sub := &peerDiscoverySub{buf: newUnboundedChannel[PeerInfo]()}

	d.mu.Lock()
	if d.closed {
		d.mu.Unlock()
		sub.buf.Complete()
		return sub.buf.ReadAll(ctx)
	}
	d.subs[sub] = struct{}{}
	d.mu.Unlock()

	out := sub.buf.ReadAll(ctx)

	if ctx.Done() != nil {
		go func() {
			<-ctx.Done()
			d.mu.Lock()
			delete(d.subs, sub)
			d.mu.Unlock()
			sub.buf.Complete()
		}()
	}
	return out
}

// Announce publishes localInfo to every Discover subscriber across the plane
// (including this discoverer's own subscribers). Announcing after Close, or with
// a cancelled ctx, returns without emitting. Subscriber lists are snapshotted
// under each discoverer's lock; the per-subscriber writes happen off-lock.
func (d *InMemoryPeerDiscovery) Announce(ctx context.Context, localInfo PeerInfo) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	for _, member := range d.plane.membersSnapshot() {
		member.deliver(localInfo)
	}
	return nil
}

// deliver fans a discovered peer to this discoverer's subscribers.
func (d *InMemoryPeerDiscovery) deliver(peer PeerInfo) {
	d.mu.Lock()
	if d.closed {
		d.mu.Unlock()
		return
	}
	subs := make([]*peerDiscoverySub, 0, len(d.subs))
	for s := range d.subs {
		subs = append(subs, s)
	}
	d.mu.Unlock()

	for _, s := range subs {
		s.buf.Write(peer)
	}
}

// SubscriberCount returns the number of active Discover streams. Useful in tests.
func (d *InMemoryPeerDiscovery) SubscriberCount() int {
	d.mu.Lock()
	defer d.mu.Unlock()
	return len(d.subs)
}

// Close completes every Discover stream, leaves the plane, and rejects further
// Discover/Announce delivery. Idempotent.
func (d *InMemoryPeerDiscovery) Close() {
	d.mu.Lock()
	if d.closed {
		d.mu.Unlock()
		return
	}
	d.closed = true
	subs := make([]*peerDiscoverySub, 0, len(d.subs))
	for s := range d.subs {
		subs = append(subs, s)
	}
	d.subs = make(map[*peerDiscoverySub]struct{})
	d.mu.Unlock()

	d.plane.leave(d)
	for _, s := range subs {
		s.buf.Complete()
	}
}

var _ IPeerDiscovery = (*InMemoryPeerDiscovery)(nil)
