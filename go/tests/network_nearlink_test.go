// network_nearlink_test.go
//
// Verifies network_nearlink.go:
//   - NearLinkPairingState / NearLinkPowerProfile ordinals + String
//   - InMemoryNearLinkRegistry: register/get device, Devices ordering, pairing
//     state default+set, session open/get/close + ActiveSessions, AvgRssi
//     (default -127 + averaged)
//   - InMemoryNearLinkAdapter + NearLinkFabric: fan-out to peers, loopback
//     excluded, availability/armed gating, pairing-state transitions on
//     Start/Stop, throughput recorded
//   - NearLinkTransport: lifecycle, Send delegates, buffered-before-subscribe,
//     Stop completes stream, constructor guard

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestNearLinkPairingState_Ordinals(t *testing.T) {
	cases := []struct {
		s    circleai.NearLinkPairingState
		ord  int
		name string
	}{
		{circleai.NearLinkPairingStateUnpaired, 0, "Unpaired"},
		{circleai.NearLinkPairingStatePairing, 1, "Pairing"},
		{circleai.NearLinkPairingStatePaired, 2, "Paired"},
		{circleai.NearLinkPairingStatePairingFailed, 3, "PairingFailed"},
	}
	for _, c := range cases {
		if int(c.s) != c.ord || c.s.String() != c.name {
			t.Errorf("%s: ord=%d str=%q", c.name, int(c.s), c.s.String())
		}
	}
}

func TestNearLinkPowerProfile_Ordinals(t *testing.T) {
	cases := []struct {
		p    circleai.NearLinkPowerProfile
		ord  int
		name string
	}{
		{circleai.NearLinkPowerProfileLowEnergy, 0, "LowEnergy"},
		{circleai.NearLinkPowerProfileBalanced, 1, "Balanced"},
		{circleai.NearLinkPowerProfileHighThroughput, 2, "HighThroughput"},
	}
	for _, c := range cases {
		if int(c.p) != c.ord || c.p.String() != c.name {
			t.Errorf("%s: ord=%d str=%q", c.name, int(c.p), c.p.String())
		}
	}
}

func TestNearLinkRegistry_Devices(t *testing.T) {
	r := circleai.NewInMemoryNearLinkRegistry()
	r.Register(circleai.NearLinkDevice{DeviceId: "d2", FriendlyName: "Zeta"})
	r.Register(circleai.NearLinkDevice{DeviceId: "d1", FriendlyName: "Alpha"})
	got, ok := r.GetDevice("d1")
	if !ok || got.FriendlyName != "Alpha" {
		t.Errorf("GetDevice = %+v ok=%v", got, ok)
	}
	devs := r.Devices()
	if len(devs) != 2 || devs[0].FriendlyName != "Alpha" || devs[1].FriendlyName != "Zeta" {
		t.Errorf("Devices not ordered by FriendlyName: %+v", devs)
	}
}

func TestNearLinkRegistry_PairingStateAndSessions(t *testing.T) {
	r := circleai.NewInMemoryNearLinkRegistry()
	if r.PairingState("d") != circleai.NearLinkPairingStateUnpaired {
		t.Error("default pairing state should be Unpaired")
	}
	r.SetPairingState("d", circleai.NearLinkPairingStatePaired)
	if r.PairingState("d") != circleai.NearLinkPairingStatePaired {
		t.Error("pairing state after set")
	}

	r.OpenSession(circleai.NearLinkSession{SessionId: "s1", DeviceId: "d", PowerProfile: circleai.NearLinkPowerProfileBalanced})
	if got, ok := r.GetSession("s1"); !ok || got.DeviceId != "d" {
		t.Errorf("GetSession = %+v ok=%v", got, ok)
	}
	if len(r.ActiveSessions()) != 1 {
		t.Errorf("ActiveSessions = %d want 1", len(r.ActiveSessions()))
	}
	r.CloseSession("s1")
	if len(r.ActiveSessions()) != 0 {
		t.Errorf("ActiveSessions after close = %d want 0", len(r.ActiveSessions()))
	}
}

func TestNearLinkRegistry_AvgRssi(t *testing.T) {
	r := circleai.NewInMemoryNearLinkRegistry()
	if got := r.AvgRssi("d"); got != -127 {
		t.Errorf("AvgRssi with no samples = %v want -127", got)
	}
	r.RecordThroughput(circleai.NearLinkThroughputSample{DeviceId: "d", RssiDbm: -40})
	r.RecordThroughput(circleai.NearLinkThroughputSample{DeviceId: "d", RssiDbm: -60})
	if got := r.AvgRssi("d"); got != -50 {
		t.Errorf("AvgRssi = %v want -50", got)
	}
}

func TestNearLinkAdapter_NotArmed(t *testing.T) {
	fab := circleai.NewNearLinkFabric(nil)
	a, _ := circleai.NewInMemoryNearLinkAdapter(fab, "dev-a")
	// Not armed -> Send errors.
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("x"), "")); err == nil {
		t.Error("Send before Start should error")
	}
}

func TestNearLinkTransport_EndToEnd(t *testing.T) {
	fab := circleai.NewNearLinkFabric(nil)
	adA, _ := circleai.NewInMemoryNearLinkAdapter(fab, "dev-a")
	adB, _ := circleai.NewInMemoryNearLinkAdapter(fab, "dev-b")
	a, _ := circleai.NewNearLinkTransport(adA)
	b, _ := circleai.NewNearLinkTransport(adB)

	if a.Kind() != circleai.TransportKindNearLink {
		t.Errorf("Kind = %v", a.Kind())
	}
	if !a.IsAvailable() {
		t.Error("adapter available by default so transport is available")
	}

	_ = a.Start(context.Background())
	_ = b.Start(context.Background())
	// Start should have paired both devices in the shared registry.
	if fab.Registry.PairingState("dev-a") != circleai.NearLinkPairingStatePaired {
		t.Error("dev-a should be Paired after Start")
	}

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)
	aStream := a.Receive(rctx)

	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("hello"), "")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "hello" {
		t.Errorf("peer got %q want hello", got)
	}
	expectNoPayload(t, aStream) // loopback excluded

	// Throughput sample recorded for the sender device.
	if got := fab.Registry.AvgRssi("dev-a"); got != -50 {
		t.Errorf("expected default -50 dBm sample for dev-a, got %v", got)
	}

	_ = a.Stop(context.Background())
	if fab.Registry.PairingState("dev-a") != circleai.NearLinkPairingStateUnpaired {
		t.Error("dev-a should be Unpaired after Stop")
	}
}

func TestNearLinkTransport_BufferedBeforeSubscribe(t *testing.T) {
	fab := circleai.NewNearLinkFabric(nil)
	adA, _ := circleai.NewInMemoryNearLinkAdapter(fab, "a")
	adB, _ := circleai.NewInMemoryNearLinkAdapter(fab, "b")
	a, _ := circleai.NewNearLinkTransport(adA)
	b, _ := circleai.NewNearLinkTransport(adB)
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

func TestNearLinkTransport_Guards(t *testing.T) {
	if _, err := circleai.NewNearLinkTransport(nil); err == nil {
		t.Error("nil adapter should be rejected")
	}
	if _, err := circleai.NewInMemoryNearLinkAdapter(nil, "d"); err == nil {
		t.Error("nil fabric should be rejected")
	}
}

func TestNearLinkAdapter_Unavailable(t *testing.T) {
	fab := circleai.NewNearLinkFabric(nil)
	ad, _ := circleai.NewInMemoryNearLinkAdapter(fab, "d")
	tr, _ := circleai.NewNearLinkTransport(ad)
	_ = tr.Start(context.Background())
	ad.SetAvailable(false)
	if tr.IsAvailable() {
		t.Error("transport should report unavailable when adapter radio is off")
	}
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload([]byte("x"), "")); err == nil {
		t.Error("Send on unavailable adapter should error")
	}
}
