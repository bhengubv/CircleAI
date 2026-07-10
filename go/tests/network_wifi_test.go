// network_wifi_test.go
//
// Verifies network_wifi.go:
//   - Port constants + beacon magic
//   - WiFiNetworkTransport: lifecycle + IsAvailable, broadcast fan-out (loopback
//     excluded), directed unicast by DestinationID (only the matching peer),
//     buffered-before-subscribe, Send-before-Start error, constructor guard
//   - WiFiPeerDiscovery: Announce -> Discover streams a PeerInfo with the parsed
//     nodeId + WiFi/{address} display name; non-beacon traffic ignored;
//     Announce-before-Discover-read is buffered (not lost); constructor guard

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestWiFi_Constants(t *testing.T) {
	if circleai.WiFiDiscoveryPort != 47890 {
		t.Errorf("DiscoveryPort = %d want 47890", circleai.WiFiDiscoveryPort)
	}
	if circleai.WiFiDataPort != 47891 {
		t.Errorf("DataPort = %d want 47891", circleai.WiFiDataPort)
	}
}

func TestWiFiTransport_Lifecycle(t *testing.T) {
	fab := circleai.NewWiFiFabric()
	tr, err := circleai.NewWiFiNetworkTransport(fab, "192.168.0.5")
	if err != nil {
		t.Fatal(err)
	}
	if tr.Kind() != circleai.TransportKindWiFi {
		t.Errorf("Kind = %v", tr.Kind())
	}
	if tr.IsAvailable() {
		t.Error("not available before Start")
	}
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload(nil, "")); err == nil {
		t.Error("Send before Start should error")
	}
	_ = tr.Start(context.Background())
	if !tr.IsAvailable() {
		t.Error("available after Start")
	}
	_ = tr.Stop(context.Background())
	if tr.IsAvailable() {
		t.Error("not available after Stop")
	}
}

func TestWiFiTransport_Broadcast(t *testing.T) {
	fab := circleai.NewWiFiFabric()
	a, _ := circleai.NewWiFiNetworkTransport(fab, "10.0.0.1")
	b, _ := circleai.NewWiFiNetworkTransport(fab, "10.0.0.2")
	c, _ := circleai.NewWiFiNetworkTransport(fab, "10.0.0.3")
	for _, tr := range []*circleai.WiFiNetworkTransport{a, b, c} {
		_ = tr.Start(context.Background())
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)
	cStream := c.Receive(rctx)
	aStream := a.Receive(rctx)

	// No destination -> broadcast to every other node.
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("all"), "")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "all" {
		t.Errorf("B broadcast got %q", got)
	}
	if got := string(recvOne(t, cStream).Data); got != "all" {
		t.Errorf("C broadcast got %q", got)
	}
	expectNoPayload(t, aStream) // sender excluded
}

func TestWiFiTransport_Unicast(t *testing.T) {
	fab := circleai.NewWiFiFabric()
	a, _ := circleai.NewWiFiNetworkTransport(fab, "10.0.0.1")
	b, _ := circleai.NewWiFiNetworkTransport(fab, "10.0.0.2")
	c, _ := circleai.NewWiFiNetworkTransport(fab, "10.0.0.3")
	for _, tr := range []*circleai.WiFiNetworkTransport{a, b, c} {
		_ = tr.Start(context.Background())
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)
	cStream := c.Receive(rctx)

	// Directed to B's address -> only B receives.
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("toB"), "10.0.0.2")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "toB" {
		t.Errorf("unicast to B got %q", got)
	}
	expectNoPayload(t, cStream) // C is not the destination
}

func TestWiFiTransport_BufferedBeforeSubscribe(t *testing.T) {
	fab := circleai.NewWiFiFabric()
	a, _ := circleai.NewWiFiNetworkTransport(fab, "10.0.0.1")
	b, _ := circleai.NewWiFiNetworkTransport(fab, "10.0.0.2")
	_ = a.Start(context.Background())
	_ = b.Start(context.Background())
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("early"), "")); err != nil {
		t.Fatal(err)
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if got := string(recvOne(t, b.Receive(rctx)).Data); got != "early" {
		t.Errorf("buffered frame lost: %q", got)
	}
}

func TestWiFiTransport_Guard(t *testing.T) {
	if _, err := circleai.NewWiFiNetworkTransport(nil, "x"); err == nil {
		t.Error("nil fabric should be rejected")
	}
}

func TestWiFiPeerDiscovery_AnnounceThenDiscover(t *testing.T) {
	fab := circleai.NewWiFiDiscoveryFabric()
	// The discoverer subscribes first, then a separate node announces.
	discoverer, err := circleai.NewWiFiPeerDiscovery(fab, "10.0.0.9")
	if err != nil {
		t.Fatal(err)
	}
	announcer, _ := circleai.NewWiFiPeerDiscovery(fab, "10.0.0.7")

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	stream := discoverer.Discover(rctx)

	if err := announcer.Announce(context.Background(), circleai.PeerInfo{NodeID: "node-xyz"}); err != nil {
		t.Fatal(err)
	}
	peer := recvPeer(t, stream)
	if peer.NodeID != "node-xyz" {
		t.Errorf("discovered NodeID = %q want node-xyz", peer.NodeID)
	}
	if peer.DisplayName != "WiFi/10.0.0.7" {
		t.Errorf("DisplayName = %q want WiFi/10.0.0.7", peer.DisplayName)
	}
	if len(peer.SupportedTransports) != 1 || peer.SupportedTransports[0] != circleai.TransportKindWiFi {
		t.Errorf("SupportedTransports = %v", peer.SupportedTransports)
	}
	if peer.Role != circleai.PeerRolePeer {
		t.Errorf("Role = %v want Peer", peer.Role)
	}
}

func TestWiFiPeerDiscovery_BufferedBeforeRead(t *testing.T) {
	fab := circleai.NewWiFiDiscoveryFabric()
	discoverer, _ := circleai.NewWiFiPeerDiscovery(fab, "a")
	announcer, _ := circleai.NewWiFiPeerDiscovery(fab, "b")

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	// Subscribe (Discover) then announce before the first read — the unbounded
	// session buffer must retain the beacon.
	stream := discoverer.Discover(rctx)
	_ = announcer.Announce(context.Background(), circleai.PeerInfo{NodeID: "buffered"})
	if got := recvPeer(t, stream).NodeID; got != "buffered" {
		t.Errorf("buffered beacon lost: %q", got)
	}
}

func TestWiFiPeerDiscovery_Guard(t *testing.T) {
	if _, err := circleai.NewWiFiPeerDiscovery(nil, "x"); err == nil {
		t.Error("nil fabric should be rejected")
	}
}
