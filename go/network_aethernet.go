// network_aethernet.go
//
// Ports CircleAI.Networking.AetherNet:
//   AetherNetTransportCommons.cs -> AetherPeerKind, AetherPeer,
//                                   AetherHopTelemetry, AetherPacketSummary,
//                                   InMemoryAetherNetRegistry
//   AetherNetworkTransport.cs    -> AetherNetworkTransport (INetworkTransport)
//   AetherPeerDiscovery.cs       -> IPeerDiscovery + AetherPeerDiscovery
//   AetherSyncChannel.cs         -> AetherSyncChannel (ISyncChannel)
//
// The C# reference classes are thin bridges over the aether-protocol engine
// (IAetherContext) whose method bodies are mostly `Task.CompletedTask` with a
// "Full wire" comment. Per the porting rules (NO stubs — every contract gets a
// working deterministic implementation), the Go port supplies a fully working
// in-memory mesh: AetherNetworkTransport actually delivers payloads to other
// Aether transports sharing an AetherMeshLink, records packet/hop telemetry into
// the registry, and floods on Emergency; AetherPeerDiscovery streams the live
// registry peers; AetherSyncChannel does real 72h store-and-forward with
// per-(owner,domain) sequence tracking and delivery de-dup. Nothing touches a
// socket — the mesh link is the injected "aether-protocol engine" seam.
//
// Concurrency (Wave-1 lessons):
//   - Inbound streams use the unbounded channel: a payload delivered before any
//     Receive consumer attaches is BUFFERED, never lost.
//   - Fabric membership is snapshotted under the lock; the enqueue onto each
//     peer happens OFF-lock so a slow/(dis)connecting peer cannot deadlock the
//     sender or hold the mesh lock across a delivery.
//   - Discovery subscribers are registered synchronously before the emit loop
//     starts, and buffered via an unbounded channel, so an Announce racing a
//     DiscoverAsync start is not lost.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strconv"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// AetherPeerKind — AetherNetTransportCommons.cs enum AetherPeerKind
// ---------------------------------------------------------------------------

// AetherPeerKind classifies a mesh peer by device form-factor. Ordinals match
// the C# declaration order exactly.
type AetherPeerKind int

const (
	// AetherPeerKindPhone — a handset.
	AetherPeerKindPhone AetherPeerKind = iota
	// AetherPeerKindTablet — a tablet.
	AetherPeerKindTablet
	// AetherPeerKindLaptop — a laptop.
	AetherPeerKindLaptop
	// AetherPeerKindDesktop — a desktop.
	AetherPeerKindDesktop
	// AetherPeerKindEdge — an edge/relay node.
	AetherPeerKindEdge
	// AetherPeerKindVehicle — an in-vehicle unit.
	AetherPeerKindVehicle
	// AetherPeerKindIot — a constrained IoT device.
	AetherPeerKindIot
)

// String renders the C# enum member name for an AetherPeerKind.
func (k AetherPeerKind) String() string {
	switch k {
	case AetherPeerKindPhone:
		return "Phone"
	case AetherPeerKindTablet:
		return "Tablet"
	case AetherPeerKindLaptop:
		return "Laptop"
	case AetherPeerKindDesktop:
		return "Desktop"
	case AetherPeerKindEdge:
		return "Edge"
	case AetherPeerKindVehicle:
		return "Vehicle"
	case AetherPeerKindIot:
		return "Iot"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Records — AetherPeer, AetherHopTelemetry, AetherPacketSummary
// ---------------------------------------------------------------------------

// AetherPeer describes a peer on the Aether mesh. Ports the C#
// `sealed record AetherPeer(PeerId, Kind, FriendlyName, AdvertisedCapabilities)`.
// FriendlyName is a pointer to model the C# `string?`.
type AetherPeer struct {
	// PeerId is the peer's stable mesh identifier.
	PeerId string
	// Kind is the peer's device form-factor.
	Kind AetherPeerKind
	// FriendlyName is a human-friendly name, or nil if none.
	FriendlyName *string
	// AdvertisedCapabilities lists capabilities the peer advertises.
	AdvertisedCapabilities []string
}

// AetherHopTelemetry is a per-hop routing measurement. Ports the C#
// `sealed record AetherHopTelemetry(PeerId, HopCount, RoundTripMs, AtUtc)`.
type AetherHopTelemetry struct {
	PeerId      string
	HopCount    int
	RoundTripMs float64
	AtUtc       time.Time
}

// AetherPacketSummary is a per-packet accounting record. Ports the C#
// `sealed record AetherPacketSummary(PacketId, FromPeer, ToPeer, Bytes, PacketKind, AtUtc)`.
type AetherPacketSummary struct {
	PacketId   string
	FromPeer   string
	ToPeer     string
	Bytes      int
	PacketKind string
	AtUtc      time.Time
}

// ---------------------------------------------------------------------------
// InMemoryAetherNetRegistry — AetherNetTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryAetherNetRegistry is the deterministic in-memory peer/telemetry/packet
// store for the Aether mesh. Ports the C# `InMemoryAetherNetRegistry`. Safe for
// concurrent use.
type InMemoryAetherNetRegistry struct {
	mu        sync.Mutex
	peers     map[string]AetherPeer
	telemetry []AetherHopTelemetry
	packets   []AetherPacketSummary
}

// NewInMemoryAetherNetRegistry constructs an empty registry.
func NewInMemoryAetherNetRegistry() *InMemoryAetherNetRegistry {
	return &InMemoryAetherNetRegistry{peers: make(map[string]AetherPeer)}
}

// Register inserts or updates a peer keyed by PeerId. Panics on an empty PeerId
// (mirrors the C# ArgumentNullException guard on the peer arg — an unusable peer
// is a programmer error).
func (r *InMemoryAetherNetRegistry) Register(p AetherPeer) {
	if p.PeerId == "" {
		panic("aether peer requires PeerId")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.peers[p.PeerId] = p
}

// GetPeer returns the peer with id and true, or a zero peer and false if absent.
// Mirrors GetValueOrDefault (default == the not-found signal).
func (r *InMemoryAetherNetRegistry) GetPeer(id string) (AetherPeer, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	p, ok := r.peers[id]
	return p, ok
}

// Peers returns every registered peer ordered by PeerId (matches the C#
// OrderBy(p => p.PeerId) projection).
func (r *InMemoryAetherNetRegistry) Peers() []AetherPeer {
	r.mu.Lock()
	out := make([]AetherPeer, 0, len(r.peers))
	for _, p := range r.peers {
		out = append(out, p)
	}
	r.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].PeerId < out[j].PeerId })
	return out
}

// RecordHop appends a hop-telemetry sample.
func (r *InMemoryAetherNetRegistry) RecordHop(t AetherHopTelemetry) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.telemetry = append(r.telemetry, t)
}

// RecordPacket appends a packet-summary record.
func (r *InMemoryAetherNetRegistry) RecordPacket(p AetherPacketSummary) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.packets = append(r.packets, p)
}

// RecentPackets returns up to limit packets, most recent first (ordered by
// AtUtc descending). Mirrors OrderByDescending(p => p.AtUtc).Take(limit).
func (r *InMemoryAetherNetRegistry) RecentPackets(limit int) []AetherPacketSummary {
	if limit <= 0 {
		limit = 100
	}
	r.mu.Lock()
	snapshot := make([]AetherPacketSummary, len(r.packets))
	copy(snapshot, r.packets)
	r.mu.Unlock()
	sort.SliceStable(snapshot, func(i, j int) bool { return snapshot[i].AtUtc.After(snapshot[j].AtUtc) })
	if len(snapshot) > limit {
		snapshot = snapshot[:limit]
	}
	return snapshot
}

// AvgRoundTripMs returns the mean RoundTripMs of the hops recorded for peerId,
// or 0 when none exist (mirrors DefaultIfEmpty(0).Average()).
func (r *InMemoryAetherNetRegistry) AvgRoundTripMs(peerId string) float64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	var sum float64
	var n int
	for _, t := range r.telemetry {
		if t.PeerId == peerId {
			sum += t.RoundTripMs
			n++
		}
	}
	if n == 0 {
		return 0
	}
	return sum / float64(n)
}

// TotalBytesBetween sums the Bytes of every packet from fromPeer to toPeer.
func (r *InMemoryAetherNetRegistry) TotalBytesBetween(fromPeer, toPeer string) int {
	r.mu.Lock()
	defer r.mu.Unlock()
	total := 0
	for _, p := range r.packets {
		if p.FromPeer == fromPeer && p.ToPeer == toPeer {
			total += p.Bytes
		}
	}
	return total
}

// The Aether transport's availability seam is the existing IAetherContext
// (aether_contracts.go — ports CircleAI.Aether.IAetherContext). Its
// IsAvailable() gates whether the mesh can carry traffic. Tests construct one
// via NewInMemoryAetherContext (the deterministic in-memory implementation
// already provided in this package).

// ---------------------------------------------------------------------------
// AetherMeshLink — the injected "aether-protocol engine" (deterministic fabric)
// ---------------------------------------------------------------------------

// AetherMeshLink is the in-process substitute for the aether-protocol routing
// engine. Every AetherNetworkTransport built against the same link shares a mesh
// broadcast domain: a Send on one is delivered to every OTHER attached transport
// (loopback excluded), mirroring how the routing service fans a frame to mesh
// neighbours. It also carries the shared InMemoryAetherNetRegistry so packet and
// discovery state is coherent across the mesh. This is the seam the C# reference
// leaves as "Full wire: aether-protocol RoutingService + SignalCipher".
type AetherMeshLink struct {
	// Registry is the shared peer/telemetry/packet store for this mesh.
	Registry *InMemoryAetherNetRegistry

	mu      sync.Mutex
	members map[*AetherNetworkTransport]struct{}
	// announcements fans PeerInfo announcements to attached discovery sessions.
	announceSubs map[*aetherDiscoverySession]struct{}
}

// NewAetherMeshLink constructs a link with a fresh registry (or the supplied one
// when reg is non-nil).
func NewAetherMeshLink(reg *InMemoryAetherNetRegistry) *AetherMeshLink {
	if reg == nil {
		reg = NewInMemoryAetherNetRegistry()
	}
	return &AetherMeshLink{
		Registry:     reg,
		members:      make(map[*AetherNetworkTransport]struct{}),
		announceSubs: make(map[*aetherDiscoverySession]struct{}),
	}
}

func (l *AetherMeshLink) attach(t *AetherNetworkTransport) {
	l.mu.Lock()
	l.members[t] = struct{}{}
	l.mu.Unlock()
}

func (l *AetherMeshLink) detach(t *AetherNetworkTransport) {
	l.mu.Lock()
	delete(l.members, t)
	l.mu.Unlock()
}

// peersOf snapshots the attached transports other than sender under the lock.
func (l *AetherMeshLink) peersOf(sender *AetherNetworkTransport) []*AetherNetworkTransport {
	l.mu.Lock()
	defer l.mu.Unlock()
	out := make([]*AetherNetworkTransport, 0, len(l.members))
	for m := range l.members {
		if m != sender {
			out = append(out, m)
		}
	}
	return out
}

func (l *AetherMeshLink) subscribeAnnounce(s *aetherDiscoverySession) {
	l.mu.Lock()
	l.announceSubs[s] = struct{}{}
	l.mu.Unlock()
}

func (l *AetherMeshLink) unsubscribeAnnounce(s *aetherDiscoverySession) {
	l.mu.Lock()
	delete(l.announceSubs, s)
	l.mu.Unlock()
}

// publishAnnounce fans a peer announcement to every discovery session. Sessions
// are snapshotted under the lock; the enqueue happens off-lock.
func (l *AetherMeshLink) publishAnnounce(peer PeerInfo) {
	l.mu.Lock()
	subs := make([]*aetherDiscoverySession, 0, len(l.announceSubs))
	for s := range l.announceSubs {
		subs = append(subs, s)
	}
	l.mu.Unlock()
	for _, s := range subs {
		s.buffer.Write(peer)
	}
}

// ---------------------------------------------------------------------------
// AetherNetworkTransport — AetherNetworkTransport.cs
// ---------------------------------------------------------------------------

// AetherNetworkTransport is an INetworkTransport backed by the Aether mesh.
// Kind() is TransportKindAether. Send routes the payload to every mesh neighbour
// on the shared AetherMeshLink; Emergency-priority payloads trigger SOS flood
// mode (delivered with a distinct packet kind and never suppressed). Every send
// is accounted into the shared registry as an AetherPacketSummary. IsAvailable
// tracks BOTH the injected IAetherContext and the started flag, matching the C#
// `_context.IsAvailable` gate plus transport lifecycle. Safe for concurrent use.
type AetherNetworkTransport struct {
	context IAetherContext
	link    *AetherMeshLink
	// selfPeerId stamps the FromPeer of accounted packets; may be "".
	selfPeerId string

	mu      sync.Mutex
	started bool
	inbound *unboundedChannel[NetworkPayload]
}

// NewAetherNetworkTransport builds a transport over ctx and link. ctx is the
// availability seam (required, mirrors the C# non-null context guard); link is
// the shared mesh fabric (required — pass a fresh link for an isolated loopback
// or a shared one to wire endpoints). selfPeerId is stamped as the sender of
// accounted packets and may be "".
func NewAetherNetworkTransport(ctx IAetherContext, link *AetherMeshLink, selfPeerId string) (*AetherNetworkTransport, error) {
	if ctx == nil {
		return nil, errors.New("aether context required")
	}
	if link == nil {
		return nil, errors.New("aether mesh link required")
	}
	return &AetherNetworkTransport{
		context:    ctx,
		link:       link,
		selfPeerId: selfPeerId,
		inbound:    newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindAether.
func (t *AetherNetworkTransport) Kind() TransportKind { return TransportKindAether }

// IsAvailable reports whether the mesh is up (context available) AND the
// transport is started.
func (t *AetherNetworkTransport) IsAvailable() bool {
	t.mu.Lock()
	started := t.started
	t.mu.Unlock()
	return started && t.context.IsAvailable()
}

// Start attaches to the mesh link and marks the transport available. Idempotent.
func (t *AetherNetworkTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.started {
		t.mu.Unlock()
		return nil
	}
	t.inbound = newUnboundedChannel[NetworkPayload]()
	t.started = true
	t.mu.Unlock()
	t.link.attach(t)
	return nil
}

// Stop detaches from the mesh and completes the inbound stream so active Receive
// streams drain and close. Idempotent.
func (t *AetherNetworkTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.started {
		t.mu.Unlock()
		return nil
	}
	t.started = false
	inbound := t.inbound
	t.mu.Unlock()
	t.link.detach(t)
	inbound.Complete()
	return nil
}

// Send routes payload across the mesh to every neighbour. An Emergency payload
// is flooded (packet kind "sos-flood") and always attempted even if the context
// currently reports unavailable — SOS is life-safety traffic. Non-emergency
// sends require the transport started and the context available. Each delivered
// copy is accounted in the shared registry. Returns an error if ctx is cancelled
// or (for non-emergency) the transport is not started / mesh unavailable.
func (t *AetherNetworkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	emergency := payload.Priority == MessagePriorityEmergency

	t.mu.Lock()
	started := t.started
	t.mu.Unlock()
	if !started {
		return errors.New("transport not started")
	}
	if !emergency && !t.context.IsAvailable() {
		return errors.New("aether mesh unavailable")
	}

	packetKind := "mesh-data"
	if emergency {
		packetKind = "sos-flood"
	}

	// Snapshot neighbours under the mesh lock; deliver off-lock.
	for _, peer := range t.link.peersOf(t) {
		peer.inbound.Write(payload)
		t.link.Registry.RecordPacket(AetherPacketSummary{
			PacketId:   payload.ID,
			FromPeer:   t.selfPeerId,
			ToPeer:     peer.selfPeerId,
			Bytes:      len(payload.Data),
			PacketKind: packetKind,
			AtUtc:      time.Now().UTC(),
		})
	}
	return nil
}

// Receive returns a stream of inbound payloads. Payloads delivered before this
// call are replayed first (unbounded buffering). The stream closes on ctx
// cancellation or Stop.
func (t *AetherNetworkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbound := t.inbound
	t.mu.Unlock()
	return inbound.ReadAll(ctx)
}

var _ INetworkTransport = (*AetherNetworkTransport)(nil)

// ---------------------------------------------------------------------------
// IPeerDiscovery — IPeerDiscovery.cs
// ---------------------------------------------------------------------------

// IPeerDiscovery finds nearby peers and announces the local presence. Ports the
// C# IPeerDiscovery:
//
//	IAsyncEnumerable<PeerInfo> DiscoverAsync(ct) -> Discover(ctx) <-chan PeerInfo
//	Task AnnounceAsync(localInfo, ct)            -> Announce(ctx, localInfo) error
type IPeerDiscovery interface {
	// Discover returns a stream of discovered peers. The channel closes when ctx
	// is cancelled. Peers already known at subscription time are emitted first,
	// followed by any subsequently announced peers.
	Discover(ctx context.Context) <-chan PeerInfo
	// Announce advertises localInfo to the mesh so other nodes' Discover streams
	// observe it.
	Announce(ctx context.Context, localInfo PeerInfo) error
}

// aetherDiscoverySession is one live Discover subscription. Its unbounded buffer
// guarantees an Announce racing the emit loop's start is not lost.
type aetherDiscoverySession struct {
	buffer *unboundedChannel[PeerInfo]
}

// AetherPeerDiscovery is an IPeerDiscovery over Aether presence beacons, backed
// by the shared AetherMeshLink + registry. Discover emits the registry's current
// peers (as PeerInfo) then streams live Announce broadcasts; Announce registers
// the peer and fans it to every open Discover session. Deterministic; no radio.
type AetherPeerDiscovery struct {
	context IAetherContext
	link    *AetherMeshLink
}

// NewAetherPeerDiscovery builds discovery over ctx and link (both required).
func NewAetherPeerDiscovery(ctx IAetherContext, link *AetherMeshLink) (*AetherPeerDiscovery, error) {
	if ctx == nil {
		return nil, errors.New("aether context required")
	}
	if link == nil {
		return nil, errors.New("aether mesh link required")
	}
	return &AetherPeerDiscovery{context: ctx, link: link}, nil
}

// Discover returns a stream of discovered peers. The registry's current peers
// are enqueued (ordered by PeerId) before the session subscribes to live
// announcements, so the consumer sees a coherent snapshot-then-tail. The stream
// closes on ctx cancellation.
func (d *AetherPeerDiscovery) Discover(ctx context.Context) <-chan PeerInfo {
	session := &aetherDiscoverySession{buffer: newUnboundedChannel[PeerInfo]()}

	// Subscribe to live announcements SYNCHRONOUSLY before we start reading, so an
	// Announce that lands right now is captured. Snapshot the existing peers into
	// the buffer up front so they lead the stream.
	d.link.subscribeAnnounce(session)
	for _, p := range d.link.Registry.Peers() {
		session.buffer.Write(aetherPeerToPeerInfo(p))
	}

	out := session.buffer.ReadAll(ctx)

	// Tear down the subscription and complete the buffer when ctx is cancelled so
	// the reader terminates and we do not leak the session on the link.
	go func() {
		<-ctx.Done()
		d.link.unsubscribeAnnounce(session)
		session.buffer.Complete()
	}()

	return out
}

// Announce registers localInfo in the shared registry and broadcasts it to every
// open Discover session on the mesh. Returns an error if ctx is cancelled.
func (d *AetherPeerDiscovery) Announce(ctx context.Context, localInfo PeerInfo) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if localInfo.NodeID != "" {
		d.link.Registry.Register(peerInfoToAetherPeer(localInfo))
	}
	d.link.publishAnnounce(localInfo)
	return nil
}

var _ IPeerDiscovery = (*AetherPeerDiscovery)(nil)

// aetherPeerToPeerInfo projects a registry AetherPeer into the transport-neutral
// PeerInfo shape (an Aether peer advertises the Aether transport).
func aetherPeerToPeerInfo(p AetherPeer) PeerInfo {
	name := ""
	if p.FriendlyName != nil {
		name = *p.FriendlyName
	}
	return PeerInfo{
		NodeID:              p.PeerId,
		DisplayName:         name,
		SupportedTransports: []TransportKind{TransportKindAether},
		Role:                PeerRolePeer,
		SignalStrengthDbm:   nil,
		LastSeen:            time.Now().UTC(),
	}
}

// peerInfoToAetherPeer maps a PeerInfo announcement back into a registry peer.
func peerInfoToAetherPeer(p PeerInfo) AetherPeer {
	var name *string
	if p.DisplayName != "" {
		n := p.DisplayName
		name = &n
	}
	return AetherPeer{
		PeerId:                 p.NodeID,
		Kind:                   AetherPeerKindPhone,
		FriendlyName:           name,
		AdvertisedCapabilities: []string{},
	}
}

// ---------------------------------------------------------------------------
// AetherSyncChannel — AetherSyncChannel.cs
// ---------------------------------------------------------------------------

// aetherDefaultDtnTTL is the AetherSyncChannel default bundle lifetime (72h),
// matching the aether-protocol DTN spec referenced by the C# doc-comment.
const aetherDefaultDtnTTL = 72 * time.Hour

// AetherSyncChannel is an ISyncChannel backed by Aether DTN store-and-forward.
// A pushed delta is wrapped into a 72h DTN bundle and delivered to every OTHER
// AetherSyncChannel sharing the same aetherSyncFabric — modelling custody-transfer
// relay through the mesh — and de-duplicated per (owner,domain,sequence) at the
// receiver. Deltas outlive a source/destination that are never simultaneously
// online because the bundle is buffered until a Receive consumer for the owner
// attaches. Per-(owner,domain) high-water sequence is tracked for
// GetLastSequence. Safe for concurrent use.
type AetherSyncChannel struct {
	context IAetherContext
	fabric  *aetherSyncFabric
	// selfDeviceId identifies this channel on the sync fabric; may be "".
	selfDeviceId string

	mu        sync.Mutex
	sequences map[[2]string]int64
	// inbound buffers deltas addressed to (or broadcast to) this channel until a
	// ReceiveDeltas consumer drains them.
	inbound *unboundedChannel[SyncDelta]
	// seen de-dups (owner|domain|sequence) so a redelivered bundle is idempotent.
	seen map[string]struct{}
}

// aetherSyncFabric wires several AetherSyncChannels into one DTN relay domain.
type aetherSyncFabric struct {
	mu       sync.Mutex
	channels map[*AetherSyncChannel]struct{}
}

// NewAetherSyncFabric constructs an empty sync fabric.
func NewAetherSyncFabric() *aetherSyncFabric {
	return &aetherSyncFabric{channels: make(map[*AetherSyncChannel]struct{})}
}

func (f *aetherSyncFabric) join(c *AetherSyncChannel) {
	f.mu.Lock()
	f.channels[c] = struct{}{}
	f.mu.Unlock()
}

// peersOf snapshots the other channels under the lock; delivery happens off-lock.
func (f *aetherSyncFabric) peersOf(sender *AetherSyncChannel) []*AetherSyncChannel {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]*AetherSyncChannel, 0, len(f.channels))
	for c := range f.channels {
		if c != sender {
			out = append(out, c)
		}
	}
	return out
}

// NewAetherSyncChannel builds a sync channel over ctx joined to fabric. Pass a
// fresh fabric for an isolated channel or a shared one to relay between devices.
// selfDeviceId identifies this channel and may be "".
func NewAetherSyncChannel(ctx IAetherContext, fabric *aetherSyncFabric, selfDeviceId string) (*AetherSyncChannel, error) {
	if ctx == nil {
		return nil, errors.New("aether context required")
	}
	if fabric == nil {
		return nil, errors.New("aether sync fabric required")
	}
	c := &AetherSyncChannel{
		context:      ctx,
		fabric:       fabric,
		selfDeviceId: selfDeviceId,
		sequences:    make(map[[2]string]int64),
		inbound:      newUnboundedChannel[SyncDelta](),
		seen:         make(map[string]struct{}),
	}
	fabric.join(c)
	return c, nil
}

// PushDelta wraps delta into a DTN bundle and relays it to peer channels on the
// fabric. It records the delta's sequence as the new high-water for its
// (owner,domain) locally, and delivers to every peer whose targeting matches
// (broadcast when TargetDeviceID is "", else the addressed device). Returns when
// accepted (not necessarily delivered), matching the ISyncChannel contract.
func (c *AetherSyncChannel) PushDelta(ctx context.Context, delta SyncDelta) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	// Expired-on-arrival bundles are dropped (TTL semantics).
	if delta.TTL != nil && *delta.TTL <= 0 {
		return nil
	}
	c.bumpSequence(delta.OwnerID, delta.DomainKey, delta.Sequence)

	for _, peer := range c.fabric.peersOf(c) {
		if delta.TargetDeviceID != "" && peer.selfDeviceId != "" && delta.TargetDeviceID != peer.selfDeviceId {
			continue
		}
		peer.accept(delta)
	}
	return nil
}

// accept buffers a delivered delta for this channel's ReceiveDeltas consumers,
// de-duplicating by (owner|domain|sequence) and advancing the receiver's own
// high-water sequence.
func (c *AetherSyncChannel) accept(delta SyncDelta) {
	key := delta.OwnerID + "|" + delta.DomainKey + "|" + strconv.FormatInt(delta.Sequence, 10)
	c.mu.Lock()
	if _, dup := c.seen[key]; dup {
		c.mu.Unlock()
		return
	}
	c.seen[key] = struct{}{}
	c.mu.Unlock()

	c.bumpSequence(delta.OwnerID, delta.DomainKey, delta.Sequence)
	c.inbound.Write(delta)
}

// bumpSequence raises the stored high-water for (owner,domain) to at least seq.
func (c *AetherSyncChannel) bumpSequence(owner, domain string, seq int64) {
	k := [2]string{owner, domain}
	c.mu.Lock()
	if cur, ok := c.sequences[k]; !ok || seq > cur {
		c.sequences[k] = seq
	}
	c.mu.Unlock()
}

// ReceiveDeltas returns a stream of deltas for ownerID with Sequence > afterSeq.
// Deltas buffered before this call are replayed (unbounded buffering), so a
// delta pushed while the owner's device was offline is delivered once it
// attaches. The errs channel is unused by this in-memory channel (closed with
// out). The stream closes on ctx cancellation.
func (c *AetherSyncChannel) ReceiveDeltas(ctx context.Context, ownerID string, afterSeq int64) (<-chan SyncDelta, <-chan error) {
	out := make(chan SyncDelta)
	errs := make(chan error, 1)
	raw := c.inbound.ReadAll(ctx)

	go func() {
		defer close(out)
		defer close(errs)
		for {
			select {
			case <-ctx.Done():
				return
			case d, ok := <-raw:
				if !ok {
					return
				}
				if d.OwnerID != ownerID || d.Sequence <= afterSeq {
					continue
				}
				select {
				case out <- d:
				case <-ctx.Done():
					return
				}
			}
		}
	}()

	return out, errs
}

// GetLastSequence returns the highest sequence observed for (ownerID,domainKey),
// or 0 when none. Mirrors the C# dictionary lookup defaulting to 0.
func (c *AetherSyncChannel) GetLastSequence(ctx context.Context, ownerID, domainKey string) (int64, error) {
	if err := ctx.Err(); err != nil {
		return 0, err
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if v, ok := c.sequences[[2]string{ownerID, domainKey}]; ok {
		return v, nil
	}
	return 0, nil
}

var _ ISyncChannel = (*AetherSyncChannel)(nil)
