// network_wifi.go
//
// Ports CircleAI.Networking.WiFi:
//   WiFiNetworkTransport.cs -> WiFiNetworkTransport (INetworkTransport)
//   WiFiPeerDiscovery.cs    -> WiFiPeerDiscovery (IPeerDiscovery)
//
// The C# WiFiNetworkTransport uses LAN UDP: a UdpClient bound to DataPort
// (47891) receives, a sender UdpClient unicasts to a parsed destination IP
// (DataPort) or broadcasts to IPAddress.Broadcast (DataPort). WiFiPeerDiscovery
// listens on DiscoveryPort (47890) for "CIRCLEAI:BEACON:{nodeId}" beacons and
// emits a PeerInfo per beacon; AnnounceAsync broadcasts one beacon. Per the
// porting rules (NO stubs — every contract gets a working deterministic
// implementation), the Go port replaces the OS UDP sockets with two shared
// in-memory media: a WiFiFabric for the data plane (unicast by DestinationID /
// broadcast to peers) and a WiFiDiscoveryFabric for the beacon plane. The port
// keeps the port constants and the beacon magic string byte-for-byte.
//
// Concurrency (Wave-1 lessons):
//   - The inbound stream and every discovery session use an unbounded channel —
//     a datagram/beacon delivered before a consumer attaches is BUFFERED, never
//     lost.
//   - Discovery subscribes to the beacon fabric SYNCHRONOUSLY before the reader
//     starts, so an Announce racing Discover() is captured, not dropped.
//   - Fabric membership is snapshotted under the lock; enqueue happens off-lock.

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
	"time"
)

// WiFiDiscoveryPort is the UDP port WiFi beacons are exchanged on (C#
// WiFiNetworkTransport.DiscoveryPort).
const WiFiDiscoveryPort = 47890

// WiFiDataPort is the UDP port WiFi data datagrams are exchanged on (C#
// WiFiNetworkTransport.DataPort).
const WiFiDataPort = 47891

// wiFiBeaconMagic is the beacon prefix WiFiPeerDiscovery emits/parses (C#
// WiFiPeerDiscovery.BeaconMagic). Kept byte-for-byte.
const wiFiBeaconMagic = "CIRCLEAI:BEACON:"

// ---------------------------------------------------------------------------
// WiFiFabric — the injected in-memory LAN data plane
// ---------------------------------------------------------------------------

// WiFiFabric is the in-process substitute for the LAN UDP data plane. Every
// WiFiNetworkTransport built against the same fabric shares a broadcast domain on
// WiFiDataPort. A Send with no DestinationId (or an unparseable one) is delivered
// to every OTHER started transport (broadcast); a Send with a DestinationId that
// matches a peer's localAddress is unicast to that peer only. Sender is always
// excluded (loopback off), matching a node not receiving its own broadcast here.
type WiFiFabric struct {
	mu      sync.Mutex
	members map[*WiFiNetworkTransport]struct{}
}

// NewWiFiFabric constructs an empty data-plane fabric.
func NewWiFiFabric() *WiFiFabric {
	return &WiFiFabric{members: make(map[*WiFiNetworkTransport]struct{})}
}

func (f *WiFiFabric) join(t *WiFiNetworkTransport) {
	f.mu.Lock()
	f.members[t] = struct{}{}
	f.mu.Unlock()
}

func (f *WiFiFabric) leave(t *WiFiNetworkTransport) {
	f.mu.Lock()
	delete(f.members, t)
	f.mu.Unlock()
}

// peersOf snapshots the other started transports under the lock; delivery
// off-lock.
func (f *WiFiFabric) peersOf(sender *WiFiNetworkTransport) []*WiFiNetworkTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]*WiFiNetworkTransport, 0, len(f.members))
	for m := range f.members {
		if m != sender {
			out = append(out, m)
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// WiFiNetworkTransport — WiFiNetworkTransport.cs
// ---------------------------------------------------------------------------

// WiFiNetworkTransport is an INetworkTransport using LAN UDP broadcast/unicast,
// backed by a shared WiFiFabric. Kind() is TransportKindWiFi; IsAvailable()
// reflects the receiver being armed (the C# `_receiver is not null` gate). Start
// arms the receiver and joins the fabric; Send unicasts to a matching peer when
// DestinationId parses to a peer address, else broadcasts to all peers; Stop
// closes and completes the inbound stream. Where the C# drives OS UdpClients,
// the Go port drives the in-memory fabric the rules require. Safe for concurrent
// use.
type WiFiNetworkTransport struct {
	fabric *WiFiFabric
	// localAddress is this node's LAN address; a peer's Send with a matching
	// DestinationId unicasts here. May be "" (then only broadcasts reach it).
	localAddress string

	mu      sync.Mutex
	armed   bool
	inbound *unboundedChannel[NetworkPayload]
}

// NewWiFiNetworkTransport builds a transport on fabric with the given local LAN
// address (used as the unicast match key; may be ""). fabric is required.
func NewWiFiNetworkTransport(fabric *WiFiFabric, localAddress string) (*WiFiNetworkTransport, error) {
	if fabric == nil {
		return nil, errors.New("wifi fabric required")
	}
	return &WiFiNetworkTransport{
		fabric:       fabric,
		localAddress: localAddress,
		inbound:      newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindWiFi.
func (t *WiFiNetworkTransport) Kind() TransportKind { return TransportKindWiFi }

// IsAvailable reports whether the receiver is armed (matches the C# `_receiver
// is not null`).
func (t *WiFiNetworkTransport) IsAvailable() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.armed
}

// LocalAddress is this node's unicast match key — exposed for assertions/tooling.
func (t *WiFiNetworkTransport) LocalAddress() string { return t.localAddress }

// Start arms the receiver and joins the fabric. Idempotent. Mirrors the C#
// StartAsync (creating the sender + receiver UdpClients + starting the pump).
func (t *WiFiNetworkTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.armed {
		t.mu.Unlock()
		return nil
	}
	t.inbound = newUnboundedChannel[NetworkPayload]()
	t.armed = true
	t.mu.Unlock()
	t.fabric.join(t)
	return nil
}

// Stop closes the sockets, leaves the fabric, and completes the inbound stream
// so active Receive streams drain and close. Idempotent. Mirrors the C#
// StopAsync (Close + TryComplete).
func (t *WiFiNetworkTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.armed {
		t.mu.Unlock()
		return nil
	}
	t.armed = false
	inbound := t.inbound
	t.mu.Unlock()
	t.fabric.leave(t)
	inbound.Complete()
	return nil
}

// Send unicasts payload to the peer whose LocalAddress equals payload's
// DestinationId (when set and matching a peer), otherwise broadcasts it to every
// other started transport. Returns an error if the sender is not started or ctx
// is cancelled. Mirrors the C# SendAsync (unicast on a parseable destination IP,
// else broadcast). The C# requires the sender to be non-null (started).
func (t *WiFiNetworkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	armed := t.armed
	t.mu.Unlock()
	if !armed {
		return errors.New("wifi transport not started")
	}

	dest := payload.DestinationID
	peers := t.fabric.peersOf(t)
	if dest != "" {
		// Attempt a unicast to a peer whose local address matches the destination.
		delivered := false
		for _, peer := range peers {
			if peer.localAddress != "" && peer.localAddress == dest {
				peer.inbound.Write(payload)
				delivered = true
			}
		}
		if delivered {
			return nil
		}
		// No peer matched the destination: on a real LAN the unicast datagram is
		// simply not received by anyone here. Do NOT fall back to broadcast — that
		// would diverge from the C# unicast branch.
		return nil
	}

	// Broadcast branch: deliver to every other started transport.
	for _, peer := range peers {
		peer.inbound.Write(payload)
	}
	return nil
}

// Receive returns a stream of inbound payloads. Datagrams delivered before this
// call are replayed first (unbounded buffering). The stream closes on ctx
// cancellation or Stop.
func (t *WiFiNetworkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbound := t.inbound
	t.mu.Unlock()
	return inbound.ReadAll(ctx)
}

var _ INetworkTransport = (*WiFiNetworkTransport)(nil)

// ---------------------------------------------------------------------------
// WiFiDiscoveryFabric — the injected in-memory beacon plane
// ---------------------------------------------------------------------------

// WiFiDiscoveryFabric is the in-process substitute for the LAN UDP beacon plane
// on WiFiDiscoveryPort. An announced beacon is fanned to every open Discover
// session. Sessions buffer beacons on an unbounded channel so an Announce racing
// a Discover start is not lost.
type WiFiDiscoveryFabric struct {
	mu   sync.Mutex
	subs map[*wiFiDiscoverySession]struct{}
}

// NewWiFiDiscoveryFabric constructs an empty beacon-plane fabric.
func NewWiFiDiscoveryFabric() *WiFiDiscoveryFabric {
	return &WiFiDiscoveryFabric{subs: make(map[*wiFiDiscoverySession]struct{})}
}

func (f *WiFiDiscoveryFabric) subscribe(s *wiFiDiscoverySession) {
	f.mu.Lock()
	f.subs[s] = struct{}{}
	f.mu.Unlock()
}

func (f *WiFiDiscoveryFabric) unsubscribe(s *wiFiDiscoverySession) {
	f.mu.Lock()
	delete(f.subs, s)
	f.mu.Unlock()
}

// broadcastBeacon fans a raw beacon datagram (from senderAddress) to every open
// session. Sessions are snapshotted under the lock; enqueue happens off-lock.
func (f *WiFiDiscoveryFabric) broadcastBeacon(senderAddress string, beacon []byte) {
	f.mu.Lock()
	subs := make([]*wiFiDiscoverySession, 0, len(f.subs))
	for s := range f.subs {
		subs = append(subs, s)
	}
	f.mu.Unlock()
	for _, s := range subs {
		s.buffer.Write(wiFiBeacon{fromAddress: senderAddress, data: beacon})
	}
}

// wiFiBeacon is one raw beacon datagram on the discovery fabric.
type wiFiBeacon struct {
	fromAddress string
	data        []byte
}

// wiFiDiscoverySession is one live Discover subscription.
type wiFiDiscoverySession struct {
	buffer *unboundedChannel[wiFiBeacon]
}

// ---------------------------------------------------------------------------
// WiFiPeerDiscovery — WiFiPeerDiscovery.cs
// ---------------------------------------------------------------------------

// WiFiPeerDiscovery discovers nearby Circle AI devices on the same LAN via UDP
// broadcast beacons, backed by a shared WiFiDiscoveryFabric. Ports the C#
// `WiFiPeerDiscovery : IPeerDiscovery`. Discover streams a PeerInfo for every
// received beacon whose payload starts with the beacon magic (the nodeId is the
// suffix); Announce broadcasts one "CIRCLEAI:BEACON:{nodeId}" beacon. No radio,
// no cloud, no infrastructure. Safe for concurrent use.
type WiFiPeerDiscovery struct {
	fabric *WiFiDiscoveryFabric
	// localAddress is stamped as the sender address of announced beacons and used
	// to build the discovered peer's DisplayName ("WiFi/{address}"). May be "".
	localAddress string
}

// NewWiFiPeerDiscovery builds discovery over fabric with the given local LAN
// address (may be ""). fabric is required.
func NewWiFiPeerDiscovery(fabric *WiFiDiscoveryFabric, localAddress string) (*WiFiPeerDiscovery, error) {
	if fabric == nil {
		return nil, errors.New("wifi discovery fabric required")
	}
	return &WiFiPeerDiscovery{fabric: fabric, localAddress: localAddress}, nil
}

// Discover returns a stream of discovered peers. It subscribes to the beacon
// fabric SYNCHRONOUSLY before the reader starts (so an Announce that lands right
// now is captured), then translates each received beacon whose payload begins
// with the magic into a PeerInfo (nodeId = suffix, DisplayName = "WiFi/{sender
// address}", SupportedTransports = [WiFi], Role = Peer). The stream closes on
// ctx cancellation. Mirrors the C# DiscoverAsync receive-loop + beacon parse.
func (d *WiFiPeerDiscovery) Discover(ctx context.Context) <-chan PeerInfo {
	session := &wiFiDiscoverySession{buffer: newUnboundedChannel[wiFiBeacon]()}
	d.fabric.subscribe(session)

	raw := session.buffer.ReadAll(ctx)
	out := make(chan PeerInfo)

	// Tear down the subscription and complete the buffer when ctx is cancelled so
	// the reader terminates and we do not leak the session on the fabric.
	go func() {
		<-ctx.Done()
		d.fabric.unsubscribe(session)
		session.buffer.Complete()
	}()

	go func() {
		defer close(out)
		for b := range raw {
			msg := string(b.data)
			if !strings.HasPrefix(msg, wiFiBeaconMagic) {
				continue
			}
			nodeId := msg[len(wiFiBeaconMagic):]
			peer := PeerInfo{
				NodeID:              nodeId,
				DisplayName:         "WiFi/" + b.fromAddress,
				SupportedTransports: []TransportKind{TransportKindWiFi},
				Role:                PeerRolePeer,
				SignalStrengthDbm:   nil,
				LastSeen:            time.Now().UTC(),
			}
			select {
			case out <- peer:
			case <-ctx.Done():
				return
			}
		}
	}()

	return out
}

// Announce broadcasts a single "CIRCLEAI:BEACON:{nodeId}" beacon to the fabric,
// so other nodes' Discover streams observe localInfo. Returns an error if ctx is
// cancelled. Mirrors the C# AnnounceAsync (UTF-8 beacon broadcast to
// DiscoveryPort). The nodeId is taken from localInfo.NodeID.
func (d *WiFiPeerDiscovery) Announce(ctx context.Context, localInfo PeerInfo) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	beacon := []byte(wiFiBeaconMagic + localInfo.NodeID)
	d.fabric.broadcastBeacon(d.localAddress, beacon)
	return nil
}

var _ IPeerDiscovery = (*WiFiPeerDiscovery)(nil)
