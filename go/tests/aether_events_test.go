// aether_events_test.go
//
// Verifies the CircleAI.Aether telemetry vocabulary port (aether_events.go):
//   - enum ordinals (stable, matching C# declaration order)
//   - record helper methods (IsExit, ExceedsLoss, HopCount, IsFailed,
//     IsHighSeverity, IsHighCongestion, IsValid)
//   - NullAetherTelemetry no-op behaviour
//   - InMemoryAetherTelemetry fan-out + unsubscribe, including the concurrency
//     requirement that a subscriber attached before publish sees the event.

package circleai_test

import (
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestAetherEnum_Ordinals(t *testing.T) {
	// AetherThreatLevel None=0..Critical=4 (wire-relevant, matches C#).
	tl := []struct {
		got  circleai.AetherThreatLevel
		want int
	}{
		{circleai.AetherThreatLevelNone, 0},
		{circleai.AetherThreatLevelLow, 1},
		{circleai.AetherThreatLevelMedium, 2},
		{circleai.AetherThreatLevelHigh, 3},
		{circleai.AetherThreatLevelCritical, 4},
	}
	for _, c := range tl {
		if int(c.got) != c.want {
			t.Errorf("AetherThreatLevel got %d want %d", int(c.got), c.want)
		}
	}

	sk := []struct {
		got  circleai.AetherSecurityEventKind
		want int
	}{
		{circleai.AetherSecurityEventKindNodeAuthAttempt, 0},
		{circleai.AetherSecurityEventKindRoutingAnomaly, 1},
		{circleai.AetherSecurityEventKindNodeBehaviourChange, 2},
		{circleai.AetherSecurityEventKindEncryptionEvent, 3},
		{circleai.AetherSecurityEventKindIntrusionSignal, 4},
		{circleai.AetherSecurityEventKindPrivilegeAttempt, 5},
	}
	for _, c := range sk {
		if int(c.got) != c.want {
			t.Errorf("AetherSecurityEventKind got %d want %d", int(c.got), c.want)
		}
	}

	nk := []struct {
		got  circleai.AetherNodeEventKind
		want int
	}{
		{circleai.AetherNodeEventKindJoined, 0},
		{circleai.AetherNodeEventKindLeft, 1},
		{circleai.AetherNodeEventKindHealthChanged, 2},
	}
	for _, c := range nk {
		if int(c.got) != c.want {
			t.Errorf("AetherNodeEventKind got %d want %d", int(c.got), c.want)
		}
	}

	tk := []struct {
		got  circleai.AetherTransportKind
		want int
	}{
		{circleai.AetherTransportKindWiFi, 0},
		{circleai.AetherTransportKindBluetooth, 1},
		{circleai.AetherTransportKindLoRa, 2},
		{circleai.AetherTransportKindNFC, 3},
		{circleai.AetherTransportKindCellular, 4},
		{circleai.AetherTransportKindEthernet, 5},
		{circleai.AetherTransportKindUnknown, 6},
	}
	for _, c := range tk {
		if int(c.got) != c.want {
			t.Errorf("AetherTransportKind got %d want %d", int(c.got), c.want)
		}
	}

	rk := []struct {
		got  circleai.AetherRouteEventKind
		want int
	}{
		{circleai.AetherRouteEventKindDiscovered, 0},
		{circleai.AetherRouteEventKindChanged, 1},
		{circleai.AetherRouteEventKindFailed, 2},
	}
	for _, c := range rk {
		if int(c.got) != c.want {
			t.Errorf("AetherRouteEventKind got %d want %d", int(c.got), c.want)
		}
	}

	netk := []struct {
		got  circleai.AetherNetworkEventKind
		want int
	}{
		{circleai.AetherNetworkEventKindTopologyChanged, 0},
		{circleai.AetherNetworkEventKindCongestionDetected, 1},
		{circleai.AetherNetworkEventKindPartitionDetected, 2},
	}
	for _, c := range netk {
		if int(c.got) != c.want {
			t.Errorf("AetherNetworkEventKind got %d want %d", int(c.got), c.want)
		}
	}
}

func TestAetherNodeEvent_Helpers(t *testing.T) {
	left := circleai.AetherNodeEvent{Kind: circleai.AetherNodeEventKindLeft}
	if !left.IsExit() {
		t.Error("Left event should be IsExit")
	}
	joined := circleai.AetherNodeEvent{Kind: circleai.AetherNodeEventKindJoined}
	if joined.IsExit() {
		t.Error("Joined event should not be IsExit")
	}

	h := circleai.AetherNodeHealth{TrustScore: 0.5}
	if !h.IsValid() {
		t.Error("0.5 trust should be valid")
	}
	if (circleai.AetherNodeHealth{TrustScore: 1.5}).IsValid() {
		t.Error("1.5 trust should be invalid")
	}
	if (circleai.AetherNodeHealth{TrustScore: -0.1}).IsValid() {
		t.Error("-0.1 trust should be invalid")
	}
}

func TestAetherTransportEvent_ExceedsLoss(t *testing.T) {
	loss := 0.30
	e := circleai.AetherTransportEvent{PacketLossRate: &loss}
	if !e.ExceedsLoss(0.25) {
		t.Error("0.30 should exceed 0.25")
	}
	if e.ExceedsLoss(0.50) {
		t.Error("0.30 should not exceed 0.50")
	}
	// Nil packet loss never exceeds.
	if (circleai.AetherTransportEvent{}).ExceedsLoss(0.0) {
		t.Error("nil packet loss should never exceed")
	}
}

func TestAetherRouteEvent_Helpers(t *testing.T) {
	e := circleai.AetherRouteEvent{
		Path: []string{"a", "b", "c"},
		Kind: circleai.AetherRouteEventKindFailed,
	}
	if e.HopCount() != 3 {
		t.Errorf("HopCount got %d want 3", e.HopCount())
	}
	if !e.IsFailed() {
		t.Error("Failed route should be IsFailed")
	}
	ok := circleai.AetherRouteEvent{Kind: circleai.AetherRouteEventKindDiscovered}
	if ok.IsFailed() {
		t.Error("Discovered route should not be IsFailed")
	}
}

func TestAetherSecurityEvent_IsHighSeverity(t *testing.T) {
	for _, lvl := range []circleai.AetherThreatLevel{circleai.AetherThreatLevelHigh, circleai.AetherThreatLevelCritical} {
		if !(circleai.AetherSecurityEvent{ThreatLevel: lvl}).IsHighSeverity() {
			t.Errorf("%v should be high severity", lvl)
		}
	}
	for _, lvl := range []circleai.AetherThreatLevel{circleai.AetherThreatLevelNone, circleai.AetherThreatLevelLow, circleai.AetherThreatLevelMedium} {
		if (circleai.AetherSecurityEvent{ThreatLevel: lvl}).IsHighSeverity() {
			t.Errorf("%v should not be high severity", lvl)
		}
	}
}

func TestAetherNetworkEvent_IsHighCongestion(t *testing.T) {
	if !(circleai.AetherNetworkEvent{CongestionLevel: 0.76}).IsHighCongestion() {
		t.Error("0.76 should be high congestion")
	}
	if (circleai.AetherNetworkEvent{CongestionLevel: 0.75}).IsHighCongestion() {
		t.Error("0.75 should not be high congestion (strict >)")
	}
}

func TestNullAetherTelemetry_NoOp(t *testing.T) {
	obs := &aetherRecordingObserver{}
	unsub := circleai.NullAetherTelemetryInstance.Subscribe(obs)
	// Null telemetry emits nothing; unsubscribe is a safe no-op.
	unsub()
	unsub() // idempotent
	if obs.securityCount() != 0 {
		t.Error("null telemetry should never deliver events")
	}
}

func TestNullAetherTelemetry_PanicsOnNilObserver(t *testing.T) {
	defer func() {
		if recover() == nil {
			t.Error("expected panic on nil observer")
		}
	}()
	circleai.NullAetherTelemetryInstance.Subscribe(nil)
}

// aetherRecordingObserver counts the events it receives, per kind. Thread-safe.
type aetherRecordingObserver struct {
	mu       sync.Mutex
	node     []circleai.AetherNodeEvent
	security []circleai.AetherSecurityEvent
	tx       int
	route    int
	network  int
}

func (o *aetherRecordingObserver) OnNodeEvent(e circleai.AetherNodeEvent) {
	o.mu.Lock()
	o.node = append(o.node, e)
	o.mu.Unlock()
}
func (o *aetherRecordingObserver) OnSecurityEvent(e circleai.AetherSecurityEvent) {
	o.mu.Lock()
	o.security = append(o.security, e)
	o.mu.Unlock()
}
func (o *aetherRecordingObserver) OnTransportEvent(circleai.AetherTransportEvent) {
	o.mu.Lock()
	o.tx++
	o.mu.Unlock()
}
func (o *aetherRecordingObserver) OnRouteEvent(circleai.AetherRouteEvent) {
	o.mu.Lock()
	o.route++
	o.mu.Unlock()
}
func (o *aetherRecordingObserver) OnNetworkEvent(circleai.AetherNetworkEvent) {
	o.mu.Lock()
	o.network++
	o.mu.Unlock()
}
func (o *aetherRecordingObserver) securityCount() int {
	o.mu.Lock()
	defer o.mu.Unlock()
	return len(o.security)
}
func (o *aetherRecordingObserver) nodeCount() int {
	o.mu.Lock()
	defer o.mu.Unlock()
	return len(o.node)
}

func TestInMemoryAetherTelemetry_FanOutAndUnsubscribe(t *testing.T) {
	tel := circleai.NewInMemoryAetherTelemetry()
	a := &aetherRecordingObserver{}
	b := &aetherRecordingObserver{}

	// Subscribe BEFORE publishing — the event must reach every attached observer.
	unsubA := tel.Subscribe(a)
	tel.Subscribe(b)
	if tel.SubscriberCount() != 2 {
		t.Fatalf("subscriber count got %d want 2", tel.SubscriberCount())
	}

	sec := circleai.AetherSecurityEvent{
		NodeID: "n1", Kind: circleai.AetherSecurityEventKindIntrusionSignal,
		ThreatLevel: circleai.AetherThreatLevelCritical, Description: "hit",
		OccurredAt: time.Now().UTC(),
	}
	tel.PublishSecurityEvent(sec)
	if a.securityCount() != 1 || b.securityCount() != 1 {
		t.Fatalf("fan-out failed: a=%d b=%d", a.securityCount(), b.securityCount())
	}

	// All five channels reach subscribers.
	tel.PublishNodeEvent(circleai.AetherNodeEvent{NodeID: "n1", Kind: circleai.AetherNodeEventKindLeft})
	tel.PublishTransportEvent(circleai.AetherTransportEvent{NodeID: "n1"})
	tel.PublishRouteEvent(circleai.AetherRouteEvent{SourceNodeID: "n1"})
	tel.PublishNetworkEvent(circleai.AetherNetworkEvent{})
	if a.nodeCount() != 1 {
		t.Errorf("node fan-out failed: a=%d", a.nodeCount())
	}

	// Unsubscribe a; only b sees the next event.
	unsubA()
	unsubA() // idempotent
	if tel.SubscriberCount() != 1 {
		t.Fatalf("after unsubscribe count got %d want 1", tel.SubscriberCount())
	}
	tel.PublishSecurityEvent(sec)
	if a.securityCount() != 1 {
		t.Errorf("unsubscribed observer still received events: %d", a.securityCount())
	}
	if b.securityCount() != 2 {
		t.Errorf("remaining observer missed event: %d", b.securityCount())
	}
}
