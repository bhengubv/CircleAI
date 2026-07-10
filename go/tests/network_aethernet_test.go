// network_aethernet_test.go
//
// Verifies network_aethernet.go:
//   - InMemoryAetherNetRegistry: register/get/ordered peers, hop-avg,
//     packet accounting, recent-packets ordering, total-bytes-between
//   - AetherPeerKind ordinals + String
//   - AetherNetworkTransport: lifecycle, availability gated on context + started,
//     mesh fan-out (loopback excluded), buffered-before-subscribe, SOS flood
//     bypasses an unavailable context, packet accounting into the registry
//   - AetherPeerDiscovery: snapshot-then-announce streaming
//   - AetherSyncChannel: 72h relay, per-(owner,domain) sequence, de-dup,
//     targeted vs broadcast, ReceiveDeltas afterSeq filtering

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func availableAetherContext() circleai.IAetherContext {
	return circleai.NewInMemoryAetherContext(circleai.InMemoryAetherContextOptions{
		InstallLevel: circleai.AetherInstallLevelOS,
		Enabled:      true,
	})
}

func TestAetherPeerKind_Ordinals(t *testing.T) {
	cases := []struct {
		k    circleai.AetherPeerKind
		ord  int
		name string
	}{
		{circleai.AetherPeerKindPhone, 0, "Phone"},
		{circleai.AetherPeerKindTablet, 1, "Tablet"},
		{circleai.AetherPeerKindLaptop, 2, "Laptop"},
		{circleai.AetherPeerKindDesktop, 3, "Desktop"},
		{circleai.AetherPeerKindEdge, 4, "Edge"},
		{circleai.AetherPeerKindVehicle, 5, "Vehicle"},
		{circleai.AetherPeerKindIot, 6, "Iot"},
	}
	for _, c := range cases {
		if int(c.k) != c.ord {
			t.Errorf("%s ordinal = %d want %d", c.name, int(c.k), c.ord)
		}
		if c.k.String() != c.name {
			t.Errorf("String = %q want %q", c.k.String(), c.name)
		}
	}
}

func TestAetherRegistry_PeersAndAccounting(t *testing.T) {
	r := circleai.NewInMemoryAetherNetRegistry()
	r.Register(circleai.AetherPeer{PeerId: "zeta", Kind: circleai.AetherPeerKindPhone, FriendlyName: strptr("Z")})
	r.Register(circleai.AetherPeer{PeerId: "alpha", Kind: circleai.AetherPeerKindEdge, AdvertisedCapabilities: []string{"relay"}})

	peers := r.Peers()
	if len(peers) != 2 || peers[0].PeerId != "alpha" || peers[1].PeerId != "zeta" {
		t.Fatalf("Peers not ordered by PeerId: %+v", peers)
	}
	if _, ok := r.GetPeer("missing"); ok {
		t.Error("GetPeer(missing) should be false")
	}
	got, ok := r.GetPeer("alpha")
	if !ok || got.Kind != circleai.AetherPeerKindEdge {
		t.Errorf("GetPeer(alpha) = %+v ok=%v", got, ok)
	}

	now := time.Now().UTC()
	r.RecordHop(circleai.AetherHopTelemetry{PeerId: "alpha", HopCount: 2, RoundTripMs: 10, AtUtc: now})
	r.RecordHop(circleai.AetherHopTelemetry{PeerId: "alpha", HopCount: 3, RoundTripMs: 20, AtUtc: now})
	if avg := r.AvgRoundTripMs("alpha"); avg != 15 {
		t.Errorf("AvgRoundTripMs = %v want 15", avg)
	}
	if avg := r.AvgRoundTripMs("nobody"); avg != 0 {
		t.Errorf("AvgRoundTripMs(nobody) = %v want 0", avg)
	}

	r.RecordPacket(circleai.AetherPacketSummary{PacketId: "p1", FromPeer: "alpha", ToPeer: "zeta", Bytes: 100, PacketKind: "data", AtUtc: now.Add(1 * time.Second)})
	r.RecordPacket(circleai.AetherPacketSummary{PacketId: "p2", FromPeer: "alpha", ToPeer: "zeta", Bytes: 50, PacketKind: "data", AtUtc: now.Add(2 * time.Second)})
	if tot := r.TotalBytesBetween("alpha", "zeta"); tot != 150 {
		t.Errorf("TotalBytesBetween = %d want 150", tot)
	}
	recent := r.RecentPackets(1)
	if len(recent) != 1 || recent[0].PacketId != "p2" {
		t.Errorf("RecentPackets(1) = %+v want [p2] (most recent)", recent)
	}
}

func TestAetherTransport_LifecycleAndAvailability(t *testing.T) {
	ctx := availableAetherContext()
	link := circleai.NewAetherMeshLink(nil)
	tr, err := circleai.NewAetherNetworkTransport(ctx, link, "self")
	if err != nil {
		t.Fatal(err)
	}
	if tr.Kind() != circleai.TransportKindAether {
		t.Errorf("Kind = %v", tr.Kind())
	}
	if tr.IsAvailable() {
		t.Error("not available before Start")
	}
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload(nil, "")); err == nil {
		t.Error("Send before Start should error")
	}
	if err := tr.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	if !tr.IsAvailable() {
		t.Error("available after Start with available context")
	}
	_ = tr.Stop(context.Background())
	if tr.IsAvailable() {
		t.Error("not available after Stop")
	}
}

func TestAetherTransport_NilArgsRejected(t *testing.T) {
	if _, err := circleai.NewAetherNetworkTransport(nil, circleai.NewAetherMeshLink(nil), ""); err == nil {
		t.Error("nil context should be rejected")
	}
	if _, err := circleai.NewAetherNetworkTransport(availableAetherContext(), nil, ""); err == nil {
		t.Error("nil link should be rejected")
	}
}

func TestAetherTransport_MeshFanOutAndAccounting(t *testing.T) {
	ctx := availableAetherContext()
	link := circleai.NewAetherMeshLink(nil)
	a, _ := circleai.NewAetherNetworkTransport(ctx, link, "a")
	b, _ := circleai.NewAetherNetworkTransport(ctx, link, "b")
	c, _ := circleai.NewAetherNetworkTransport(ctx, link, "c")
	for _, tr := range []*circleai.AetherNetworkTransport{a, b, c} {
		if err := tr.Start(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)
	cStream := c.Receive(rctx)
	aStream := a.Receive(rctx)

	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("mesh"), "")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "mesh" {
		t.Errorf("b got %q", got)
	}
	if got := string(recvOne(t, cStream).Data); got != "mesh" {
		t.Errorf("c got %q", got)
	}
	expectNoPayload(t, aStream) // loopback excluded

	// Two packets accounted (a->b, a->c).
	if tot := link.Registry.TotalBytesBetween("a", "b"); tot != 4 {
		t.Errorf("bytes a->b = %d want 4", tot)
	}
	if tot := link.Registry.TotalBytesBetween("a", "c"); tot != 4 {
		t.Errorf("bytes a->c = %d want 4", tot)
	}
}

func TestAetherTransport_BufferedBeforeSubscribe(t *testing.T) {
	ctx := availableAetherContext()
	link := circleai.NewAetherMeshLink(nil)
	a, _ := circleai.NewAetherNetworkTransport(ctx, link, "a")
	b, _ := circleai.NewAetherNetworkTransport(ctx, link, "b")
	_ = a.Start(context.Background())
	_ = b.Start(context.Background())

	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("early"), "")); err != nil {
		t.Fatal(err)
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if got := string(recvOne(t, b.Receive(rctx)).Data); got != "early" {
		t.Errorf("buffered payload lost: %q", got)
	}
}

func TestAetherTransport_SOSFloodBypassesUnavailable(t *testing.T) {
	// A non-emergency send fails when the mesh is unavailable, but an Emergency
	// (SOS) payload is still flooded to neighbours.
	ctx := circleai.NewInMemoryAetherContext(circleai.InMemoryAetherContextOptions{
		InstallLevel: circleai.AetherInstallLevelOS,
		Enabled:      false, // mesh DOWN
	})
	link := circleai.NewAetherMeshLink(nil)
	a, _ := circleai.NewAetherNetworkTransport(ctx, link, "a")
	b, _ := circleai.NewAetherNetworkTransport(ctx, link, "b")
	_ = a.Start(context.Background())
	_ = b.Start(context.Background())

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)

	// Normal priority fails while unavailable.
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("x"), "")); err == nil {
		t.Error("normal send should fail when mesh unavailable")
	}
	// Emergency floods regardless.
	sos := circleai.NewNetworkPayloadWith([]byte("HELP"), "", circleai.MessagePriorityEmergency, "", nil)
	if err := a.Send(context.Background(), sos); err != nil {
		t.Fatalf("SOS send should succeed: %v", err)
	}
	if got := string(recvOne(t, bStream).Data); got != "HELP" {
		t.Errorf("SOS payload not delivered: %q", got)
	}
	// Accounted as an sos-flood packet.
	recent := link.Registry.RecentPackets(10)
	if len(recent) == 0 || recent[0].PacketKind != "sos-flood" {
		t.Errorf("expected sos-flood packet, got %+v", recent)
	}
}

func TestAetherPeerDiscovery_SnapshotThenAnnounce(t *testing.T) {
	ctx := availableAetherContext()
	link := circleai.NewAetherMeshLink(nil)
	// Pre-seed one peer in the registry.
	link.Registry.Register(circleai.AetherPeer{PeerId: "known", Kind: circleai.AetherPeerKindLaptop, FriendlyName: strptr("Known")})

	disc, err := circleai.NewAetherPeerDiscovery(ctx, link)
	if err != nil {
		t.Fatal(err)
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	stream := disc.Discover(rctx)

	// First emission: the snapshot peer.
	first := recvPeer(t, stream)
	if first.NodeID != "known" {
		t.Errorf("snapshot peer = %q want known", first.NodeID)
	}
	if len(first.SupportedTransports) != 1 || first.SupportedTransports[0] != circleai.TransportKindAether {
		t.Errorf("snapshot peer should advertise Aether: %+v", first.SupportedTransports)
	}

	// Now announce a new peer; the stream should surface it.
	if err := disc.Announce(context.Background(), circleai.PeerInfo{NodeID: "fresh", DisplayName: "Fresh"}); err != nil {
		t.Fatal(err)
	}
	second := recvPeer(t, stream)
	if second.NodeID != "fresh" {
		t.Errorf("announced peer = %q want fresh", second.NodeID)
	}
}

func TestAetherSyncChannel_RelayAndSequence(t *testing.T) {
	ctx := availableAetherContext()
	fab := circleai.NewAetherSyncFabric()
	src, _ := circleai.NewAetherSyncChannel(ctx, fab, "deviceA")
	dst, _ := circleai.NewAetherSyncChannel(ctx, fab, "deviceB")

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	deltas, errs := dst.ReceiveDeltas(rctx, "owner1", 0)

	push := circleai.SyncDelta{
		OwnerID:        "owner1",
		SourceDeviceID: "deviceA",
		TargetDeviceID: "", // broadcast
		DomainKey:      "memory.episodic",
		Payload:        []byte("state"),
		Sequence:       5,
		DeliveryMode:   circleai.SyncDeliveryModeGuaranteed,
		CreatedAt:      time.Now().UTC(),
	}
	if err := src.PushDelta(context.Background(), push); err != nil {
		t.Fatal(err)
	}

	got := recvDelta(t, deltas, errs)
	if got.Sequence != 5 || string(got.Payload) != "state" {
		t.Errorf("delivered delta = %+v", got)
	}

	// Source tracks its own high-water; destination tracks the received one.
	if seq, _ := src.GetLastSequence(context.Background(), "owner1", "memory.episodic"); seq != 5 {
		t.Errorf("src last seq = %d want 5", seq)
	}
	if seq, _ := dst.GetLastSequence(context.Background(), "owner1", "memory.episodic"); seq != 5 {
		t.Errorf("dst last seq = %d want 5", seq)
	}
	if seq, _ := dst.GetLastSequence(context.Background(), "owner1", "other.domain"); seq != 0 {
		t.Errorf("unknown domain seq = %d want 0", seq)
	}
}

func TestAetherSyncChannel_TargetedDelivery(t *testing.T) {
	ctx := availableAetherContext()
	fab := circleai.NewAetherSyncFabric()
	src, _ := circleai.NewAetherSyncChannel(ctx, fab, "deviceA")
	b, _ := circleai.NewAetherSyncChannel(ctx, fab, "deviceB")
	c, _ := circleai.NewAetherSyncChannel(ctx, fab, "deviceC")

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bDeltas, _ := b.ReceiveDeltas(rctx, "o", 0)
	cDeltas, cErrs := c.ReceiveDeltas(rctx, "o", 0)

	// Targeted at deviceB only.
	if err := src.PushDelta(context.Background(), circleai.SyncDelta{
		OwnerID: "o", SourceDeviceID: "deviceA", TargetDeviceID: "deviceB",
		DomainKey: "d", Payload: []byte("for-b"), Sequence: 1,
	}); err != nil {
		t.Fatal(err)
	}
	if got := recvDelta(t, bDeltas, nil); string(got.Payload) != "for-b" {
		t.Errorf("b delta = %q", string(got.Payload))
	}
	// c must NOT receive it.
	select {
	case d, ok := <-cDeltas:
		if ok {
			t.Errorf("c should not receive targeted delta, got %q", string(d.Payload))
		}
	case <-cErrs:
	case <-time.After(150 * time.Millisecond):
	}
}

func TestAetherSyncChannel_ReceiveAfterSeqFilter(t *testing.T) {
	ctx := availableAetherContext()
	fab := circleai.NewAetherSyncFabric()
	src, _ := circleai.NewAetherSyncChannel(ctx, fab, "A")
	dst, _ := circleai.NewAetherSyncChannel(ctx, fab, "B")

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	// Only want deltas after seq 3.
	deltas, _ := dst.ReceiveDeltas(rctx, "o", 3)

	for _, seq := range []int64{2, 3, 4, 5} {
		_ = src.PushDelta(context.Background(), circleai.SyncDelta{
			OwnerID: "o", SourceDeviceID: "A", DomainKey: "d",
			Payload: []byte("v"), Sequence: seq,
		})
	}
	// First surfaced delta must be seq 4 (2 and 3 filtered out).
	first := recvDelta(t, deltas, nil)
	if first.Sequence != 4 {
		t.Errorf("first delivered seq = %d want 4", first.Sequence)
	}
}

// recvPeer reads one PeerInfo or fails after a timeout.
func recvPeer(t *testing.T, ch <-chan circleai.PeerInfo) circleai.PeerInfo {
	t.Helper()
	select {
	case p, ok := <-ch:
		if !ok {
			t.Fatal("peer channel closed early")
		}
		return p
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for a peer")
		return circleai.PeerInfo{}
	}
}

// recvDelta reads one SyncDelta or fails after a timeout (also draining errs).
func recvDelta(t *testing.T, ch <-chan circleai.SyncDelta, errs <-chan error) circleai.SyncDelta {
	t.Helper()
	select {
	case d, ok := <-ch:
		if !ok {
			t.Fatal("delta channel closed early")
		}
		return d
	case err := <-errs:
		t.Fatalf("unexpected error: %v", err)
		return circleai.SyncDelta{}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for a delta")
		return circleai.SyncDelta{}
	}
}
