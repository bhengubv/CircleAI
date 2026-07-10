// network_bluetooth_test.go
//
// Verifies network_bluetooth.go:
//   - BluetoothConnectionState ordinals + String
//   - Capability profiles (Le5/Le4/Classic) values
//   - InMemoryBluetoothTransportRegistry: register/get, ordered endpoints,
//     state default + set, throughput avg
//   - InMemoryBleGattAdapter: arm/write fan-out, availability gate, state
//     transitions in the registry
//   - BluetoothNetworkTransport: lifecycle, IsAvailable follows adapter,
//     send-before-start error, fabric fan-out (loopback excluded),
//     buffered-before-subscribe, Stop completes the stream

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestBluetoothConnectionState_Ordinals(t *testing.T) {
	cases := []struct {
		s    circleai.BluetoothConnectionState
		ord  int
		name string
	}{
		{circleai.BluetoothConnectionStateDisconnected, 0, "Disconnected"},
		{circleai.BluetoothConnectionStateDiscovering, 1, "Discovering"},
		{circleai.BluetoothConnectionStateConnecting, 2, "Connecting"},
		{circleai.BluetoothConnectionStateConnected, 3, "Connected"},
		{circleai.BluetoothConnectionStateFailed, 4, "Failed"},
	}
	for _, c := range cases {
		if int(c.s) != c.ord {
			t.Errorf("%s ordinal = %d want %d", c.name, int(c.s), c.ord)
		}
		if c.s.String() != c.name {
			t.Errorf("String = %q want %q", c.s.String(), c.name)
		}
	}
}

func TestBluetoothCapabilityProfiles_Values(t *testing.T) {
	le5 := circleai.BluetoothCapabilityProfileLe5()
	if le5.MaxMtuBytes != 247 || !le5.SupportsSecureConnections || !le5.SupportsHighSpeed {
		t.Errorf("Le5 = %+v", le5)
	}
	if len(le5.CompatibleProfiles) != 2 || le5.CompatibleProfiles[0] != "GATT" || le5.CompatibleProfiles[1] != "L2CAP" {
		t.Errorf("Le5 profiles = %+v", le5.CompatibleProfiles)
	}
	le4 := circleai.BluetoothCapabilityProfileLe4()
	if le4.MaxMtuBytes != 23 || le4.SupportsHighSpeed {
		t.Errorf("Le4 = %+v", le4)
	}
	classic := circleai.BluetoothCapabilityProfileClassic()
	if classic.MaxMtuBytes != 1024 || classic.SupportsHighSpeed {
		t.Errorf("Classic = %+v", classic)
	}
	if len(classic.CompatibleProfiles) != 2 || classic.CompatibleProfiles[0] != "SPP" {
		t.Errorf("Classic profiles = %+v", classic.CompatibleProfiles)
	}
}

func TestBluetoothRegistry_EndpointsAndState(t *testing.T) {
	r := circleai.NewInMemoryBluetoothTransportRegistry()
	r.Register(circleai.BluetoothEndpointDescriptor{DeviceId: "d2", Name: "Zebra", MacAddress: "AA"})
	r.Register(circleai.BluetoothEndpointDescriptor{DeviceId: "d1", Name: "Apple", MacAddress: "BB"})

	all := r.AllEndpoints()
	if len(all) != 2 || all[0].Name != "Apple" || all[1].Name != "Zebra" {
		t.Fatalf("AllEndpoints not ordered by Name: %+v", all)
	}
	if _, ok := r.GetEndpoint("nope"); ok {
		t.Error("GetEndpoint(nope) should be false")
	}
	if s := r.State("d1"); s != circleai.BluetoothConnectionStateDisconnected {
		t.Errorf("default state = %v want Disconnected", s)
	}
	r.SetState("d1", circleai.BluetoothConnectionStateConnected)
	if s := r.State("d1"); s != circleai.BluetoothConnectionStateConnected {
		t.Errorf("state after set = %v", s)
	}

	now := time.Now().UTC()
	r.RecordThroughput(circleai.BluetoothThroughputSample{DeviceId: "d1", KbpsRead: 100, KbpsWrite: 10, AtUtc: now})
	r.RecordThroughput(circleai.BluetoothThroughputSample{DeviceId: "d1", KbpsRead: 200, KbpsWrite: 20, AtUtc: now})
	if avg := r.AvgKbpsRead("d1"); avg != 150 {
		t.Errorf("AvgKbpsRead = %v want 150", avg)
	}
	if avg := r.AvgKbpsRead("none"); avg != 0 {
		t.Errorf("AvgKbpsRead(none) = %v want 0", avg)
	}
}

func TestBleAdapter_WriteFanOutAndAvailability(t *testing.T) {
	fab := circleai.NewBluetoothFabric(nil)
	a, err := circleai.NewInMemoryBleGattAdapter(fab, "dev-a")
	if err != nil {
		t.Fatal(err)
	}
	b, _ := circleai.NewInMemoryBleGattAdapter(fab, "dev-b")

	sinkA := newTestSink()
	sinkB := newTestSink()
	if err := a.Start(context.Background(), sinkA); err != nil {
		t.Fatal(err)
	}
	if err := b.Start(context.Background(), sinkB); err != nil {
		t.Fatal(err)
	}
	// State recorded as Connected on Start.
	if s := fab.Registry.State("dev-a"); s != circleai.BluetoothConnectionStateConnected {
		t.Errorf("dev-a state = %v want Connected", s)
	}

	if err := a.Write(context.Background(), circleai.NewNetworkPayload([]byte("frame"), "")); err != nil {
		t.Fatal(err)
	}
	if got := string(sinkB.wait(t).Data); got != "frame" {
		t.Errorf("b sink got %q", got)
	}
	if sinkA.tryGet() != nil {
		t.Error("sender sink should not receive its own write")
	}

	// Unavailable adapter refuses writes.
	a.SetAvailable(false)
	if err := a.Write(context.Background(), circleai.NewNetworkPayload([]byte("x"), "")); err == nil {
		t.Error("write on unavailable adapter should error")
	}
	a.SetAvailable(true)

	_ = a.Stop(context.Background())
	if s := fab.Registry.State("dev-a"); s != circleai.BluetoothConnectionStateDisconnected {
		t.Errorf("dev-a state after Stop = %v want Disconnected", s)
	}
	// Write after Stop errors (not armed).
	if err := a.Write(context.Background(), circleai.NewNetworkPayload(nil, "")); err == nil {
		t.Error("write after Stop should error")
	}
}

func TestBluetoothTransport_LifecycleAndFanOut(t *testing.T) {
	fab := circleai.NewBluetoothFabric(nil)
	adA, _ := circleai.NewInMemoryBleGattAdapter(fab, "a")
	adB, _ := circleai.NewInMemoryBleGattAdapter(fab, "b")
	ta, err := circleai.NewBluetoothNetworkTransport(adA)
	if err != nil {
		t.Fatal(err)
	}
	tb, _ := circleai.NewBluetoothNetworkTransport(adB)

	if ta.Kind() != circleai.TransportKindBluetooth {
		t.Errorf("Kind = %v", ta.Kind())
	}
	if !ta.IsAvailable() {
		t.Error("adapter available => transport available")
	}
	if err := ta.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := tb.Start(context.Background()); err != nil {
		t.Fatal(err)
	}

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := tb.Receive(rctx)

	if err := ta.Send(context.Background(), circleai.NewNetworkPayload([]byte("hi"), "")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "hi" {
		t.Errorf("b transport got %q", got)
	}

	// IsAvailable follows the adapter.
	adA.SetAvailable(false)
	if ta.IsAvailable() {
		t.Error("transport should follow adapter unavailability")
	}
	adA.SetAvailable(true)
}

func TestBluetoothTransport_NilAdapterRejected(t *testing.T) {
	if _, err := circleai.NewBluetoothNetworkTransport(nil); err == nil {
		t.Error("nil adapter should be rejected")
	}
}

func TestBluetoothTransport_BufferedBeforeSubscribe(t *testing.T) {
	fab := circleai.NewBluetoothFabric(nil)
	adA, _ := circleai.NewInMemoryBleGattAdapter(fab, "a")
	adB, _ := circleai.NewInMemoryBleGattAdapter(fab, "b")
	ta, _ := circleai.NewBluetoothNetworkTransport(adA)
	tb, _ := circleai.NewBluetoothNetworkTransport(adB)
	_ = ta.Start(context.Background())
	_ = tb.Start(context.Background())

	if err := ta.Send(context.Background(), circleai.NewNetworkPayload([]byte("early"), "")); err != nil {
		t.Fatal(err)
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if got := string(recvOne(t, tb.Receive(rctx)).Data); got != "early" {
		t.Errorf("buffered frame lost: %q", got)
	}
}

func TestBluetoothTransport_StopCompletesStream(t *testing.T) {
	fab := circleai.NewBluetoothFabric(nil)
	ad, _ := circleai.NewInMemoryBleGattAdapter(fab, "a")
	tr, _ := circleai.NewBluetoothNetworkTransport(ad)
	_ = tr.Start(context.Background())
	stream := tr.Receive(context.Background())
	_ = tr.Stop(context.Background())
	select {
	case _, ok := <-stream:
		if ok {
			t.Error("stream should close after Stop")
		}
	case <-time.After(2 * time.Second):
		t.Error("stream did not close after Stop")
	}
}

// testSink is a minimal inboundSink for driving the BLE adapter in tests. It is
// backed by a buffered channel so a delivered frame can be observed.
type testSink struct {
	ch chan circleai.NetworkPayload
}

func newTestSink() *testSink { return &testSink{ch: make(chan circleai.NetworkPayload, 16)} }

// Write satisfies the inboundSink contract used by IBleGattAdapter.Start.
func (s *testSink) Write(item circleai.NetworkPayload) bool {
	s.ch <- item
	return true
}

func (s *testSink) wait(t *testing.T) circleai.NetworkPayload {
	t.Helper()
	select {
	case p := <-s.ch:
		return p
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting on sink")
		return circleai.NetworkPayload{}
	}
}

func (s *testSink) tryGet() *circleai.NetworkPayload {
	select {
	case p := <-s.ch:
		return &p
	case <-time.After(120 * time.Millisecond):
		return nil
	}
}
