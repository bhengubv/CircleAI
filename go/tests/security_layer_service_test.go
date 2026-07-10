// security_layer_service_test.go
//
// Verifies SecurityLayerService (ported from AISecurityLayerService.cs):
//   - HandlePeerEvent degrades trust and issues at most one directive per event,
//     most-severe threshold first.
//   - None-level events cause no degradation and no directive.
//   - GetPosture reports quarantined/monitored counts and overall threat level.
//   - The background recovery loop heals scores over time.
//   - Start/Stop are idempotent / clean.

package circleai_test

import (
	"context"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

type directiveSink struct {
	mu   sync.Mutex
	seen []circleai.PeerDirective
}

func (s *directiveSink) OnDirective(d circleai.PeerDirective) {
	s.mu.Lock()
	s.seen = append(s.seen, d)
	s.mu.Unlock()
}

func (s *directiveSink) last() (circleai.PeerDirective, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.seen) == 0 {
		return circleai.PeerDirective{}, false
	}
	return s.seen[len(s.seen)-1], true
}

func (s *directiveSink) count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.seen)
}

func newLayer() (*circleai.SecurityLayerService, *circleai.NodeTrustRegistry, *directiveSink) {
	opt := circleai.NewSecurityOptions()
	reg := circleai.NewNodeTrustRegistry(opt)
	pub := circleai.NewDirectivePublisher()
	svc := circleai.NewSecurityLayerService(reg, opt, pub)
	sink := &directiveSink{}
	svc.SubscribeToDirectives(sink)
	return svc, reg, sink
}

func critical(node string) circleai.PeerSecurityEvent {
	return circleai.PeerSecurityEvent{
		NodeID: node, Kind: circleai.PeerSecurityEventKindIntrusionSignal,
		ThreatLevel: circleai.PeerThreatLevelCritical, Description: "attack",
		TransportID: "test", OccurredAt: time.Now().UTC(),
	}
}

func TestLayer_ElevateMonitoringDirective(t *testing.T) {
	svc, _, sink := newLayer()
	// One medium routing anomaly: 0.10 × 1.0 = 0.10 drop → 0.90 > 0.75, no dir.
	// Push enough to cross only the 0.75 monitoring threshold (not 0.50).
	// behaviour-change medium = 0.08; three of them = 0.24 → 0.76 (still >0.75),
	// four → 0.68 (≤0.75, >0.50) → ElevateMonitoring.
	ev := circleai.PeerSecurityEvent{
		NodeID: "n1", Kind: circleai.PeerSecurityEventKindBehaviourChange,
		ThreatLevel: circleai.PeerThreatLevelMedium, Description: "drift",
		TransportID: "test", OccurredAt: time.Now().UTC(),
	}
	for i := 0; i < 4; i++ {
		svc.HandlePeerEvent(ev)
	}
	d, ok := sink.last()
	if !ok {
		t.Fatal("expected a directive")
	}
	if d.Kind != circleai.PeerDirectiveKindElevateMonitoring {
		t.Errorf("directive kind: got %v, want ElevateMonitoring", d.Kind)
	}
}

func TestLayer_QuarantineOnCriticalCross(t *testing.T) {
	svc, reg, sink := newLayer()
	// Critical intrusion = 0.15 × 3.0 = 0.45 per event. Two events → 0.10 ≤ 0.25
	// crossing the quarantine threshold in one step.
	svc.HandlePeerEvent(critical("n1")) // 1.0 → 0.55
	svc.HandlePeerEvent(critical("n1")) // 0.55 → 0.10 (crosses 0.25)
	d, ok := sink.last()
	if !ok {
		t.Fatal("expected a directive")
	}
	if d.Kind != circleai.PeerDirectiveKindQuarantineNode {
		t.Errorf("directive kind: got %v, want QuarantineNode", d.Kind)
	}
	if d.ThreatLevel != circleai.PeerThreatLevelCritical {
		t.Errorf("threat level: got %v", d.ThreatLevel)
	}
	if got := reg.GetTrustScore("n1"); got > 0.25 {
		t.Errorf("score should be ≤ 0.25: got %v", got)
	}
}

func TestLayer_NoneLevelNoDirectiveNoDegradation(t *testing.T) {
	svc, reg, sink := newLayer()
	ev := circleai.PeerSecurityEvent{
		NodeID: "n1", Kind: circleai.PeerSecurityEventKindIntrusionSignal,
		ThreatLevel: circleai.PeerThreatLevelNone, Description: "benign",
		TransportID: "test", OccurredAt: time.Now().UTC(),
	}
	svc.HandlePeerEvent(ev)
	if sink.count() != 0 {
		t.Errorf("None-level event should issue no directive, got %d", sink.count())
	}
	// Score must be unchanged; peer may not even be tracked. GetTrustScore of an
	// untracked peer returns InitialTrustScore (1.0).
	if got := reg.GetTrustScore("n1"); got != 1.0 {
		t.Errorf("None-level event should not degrade: got %v", got)
	}
}

func TestLayer_SingleDirectivePerEvent(t *testing.T) {
	svc, _, sink := newLayer()
	// A single event that plunges straight past all three thresholds should still
	// emit exactly ONE (the most-severe) directive.
	big := circleai.PeerSecurityEvent{
		NodeID: "n1", Kind: circleai.PeerSecurityEventKindIntrusionSignal,
		ThreatLevel: circleai.PeerThreatLevelCritical, Description: "nuke",
		TransportID: "test", OccurredAt: time.Now().UTC(),
	}
	// 0.45 drop only reaches 0.55 — not past quarantine. Degrade manually via
	// repeated events is covered elsewhere; here force a big single drop by
	// stacking three criticals but checking the count delta of the LAST event.
	svc.HandlePeerEvent(big) // 1.0 → 0.55 (crosses 0.75 → ElevateMonitoring)
	before := sink.count()
	svc.HandlePeerEvent(big) // 0.55 → 0.10 (crosses 0.50 and 0.25 in one step)
	after := sink.count()
	if after-before != 1 {
		t.Errorf("second event should emit exactly one directive, got %d", after-before)
	}
	d, _ := sink.last()
	if d.Kind != circleai.PeerDirectiveKindQuarantineNode {
		t.Errorf("most-severe directive expected: got %v", d.Kind)
	}
}

func TestLayer_PostureCounts(t *testing.T) {
	svc, _, _ := newLayer()
	// Drive n1 into quarantine and n2 into monitoring.
	svc.HandlePeerEvent(critical("n1")) // 0.55
	svc.HandlePeerEvent(critical("n1")) // 0.10 quarantined
	monitor := circleai.PeerSecurityEvent{
		NodeID: "n2", Kind: circleai.PeerSecurityEventKindBehaviourChange,
		ThreatLevel: circleai.PeerThreatLevelMedium, Description: "drift",
		TransportID: "test", OccurredAt: time.Now().UTC(),
	}
	for i := 0; i < 4; i++ {
		svc.HandlePeerEvent(monitor) // → ~0.68 monitored
	}

	p, err := svc.GetPosture(context.Background())
	if err != nil {
		t.Fatalf("posture: %v", err)
	}
	if p.QuarantinedPeerCount != 1 {
		t.Errorf("quarantined: got %d, want 1", p.QuarantinedPeerCount)
	}
	if p.MonitoredPeerCount != 1 {
		t.Errorf("monitored: got %d, want 1", p.MonitoredPeerCount)
	}
	if p.OverallThreatLevel != circleai.PeerThreatLevelCritical {
		t.Errorf("overall threat (worst = n1 quarantined): got %v, want Critical", p.OverallThreatLevel)
	}
}

func TestLayer_PostureEmptyNetwork(t *testing.T) {
	svc, _, _ := newLayer()
	p, _ := svc.GetPosture(context.Background())
	if p.OverallThreatLevel != circleai.PeerThreatLevelNone {
		t.Errorf("empty network threat: got %v, want None", p.OverallThreatLevel)
	}
	if p.QuarantinedPeerCount != 0 || p.MonitoredPeerCount != 0 {
		t.Errorf("empty counts nonzero: %+v", p)
	}
}

func TestLayer_StartStopAndRecoveryLoop(t *testing.T) {
	opt := circleai.NewSecurityOptions()
	opt.RecoveryRatePerSecond = 0.5
	reg := circleai.NewNodeTrustRegistry(opt)
	pub := circleai.NewDirectivePublisher()
	svc := circleai.NewSecurityLayerService(reg, opt, pub).WithRecoveryInterval(20 * time.Millisecond)

	// Degrade a peer, then let the loop heal it.
	svc.HandlePeerEvent(circleai.PeerSecurityEvent{
		NodeID: "n1", Kind: circleai.PeerSecurityEventKindIntrusionSignal,
		ThreatLevel: circleai.PeerThreatLevelCritical, Description: "hit",
		TransportID: "test", OccurredAt: time.Now().UTC(),
	})
	start := reg.GetTrustScore("n1")

	ctx := context.Background()
	if err := svc.Start(ctx); err != nil {
		t.Fatalf("start: %v", err)
	}
	// Idempotent start.
	if err := svc.Start(ctx); err != nil {
		t.Fatalf("second start: %v", err)
	}

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if reg.GetTrustScore("n1") > start {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}
	if reg.GetTrustScore("n1") <= start {
		t.Errorf("recovery loop did not heal: start=%v now=%v", start, reg.GetTrustScore("n1"))
	}

	if err := svc.Stop(ctx); err != nil {
		t.Fatalf("stop: %v", err)
	}
	// Stop is safe to call again.
	if err := svc.Stop(ctx); err != nil {
		t.Fatalf("second stop: %v", err)
	}
}

func TestLayer_PostureReflectsActive(t *testing.T) {
	svc, _, _ := newLayer()
	before, _ := svc.GetPosture(context.Background())
	if before.IsActive {
		t.Error("should be inactive before Start")
	}
	_ = svc.Start(context.Background())
	defer svc.Stop(context.Background())
	after, _ := svc.GetPosture(context.Background())
	if !after.IsActive {
		t.Error("should be active after Start")
	}
}
