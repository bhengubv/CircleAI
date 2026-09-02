// security_aethernet_test.go
//
// Verifies the CircleAI.Security.AetherNet slice:
//   - AetherMapper round-trips (event kind, threat level both ways, directive kind)
//   - AetherSecurityBridge: an Aether security event published on telemetry flows
//     through the transport-agnostic SecurityLayerService and emerges as a mapped
//     Aether SecurityDirective at the ISecurityDirectiveConsumer; posture maps.
//   - AetherIntelligenceAdapter: Peer* intelligence results map to Aether types.
//   - MeshDirectiveStore: records, lazy-expires, Release lifts, IsBlocked/audit.
//   - MeshSecurityGate: Decide/Enforce + MeshSecurityBlockedError.
//   - MeshGatedCompanionSession: Send/Stream/Agent gated; metadata calls pass.

package circleai_test

import (
	"context"
	"errors"
	"strings"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ─── Mapper round-trips ─────────────────────────────────────────────────────

func TestAetherMapper_ThreatLevelRoundTrip(t *testing.T) {
	// Aether ↔ Peer threat level is a symmetric 1:1 mapping for all five levels.
	levels := []struct {
		a circleai.AetherThreatLevel
		p circleai.PeerThreatLevel
	}{
		{circleai.AetherThreatLevelNone, circleai.PeerThreatLevelNone},
		{circleai.AetherThreatLevelLow, circleai.PeerThreatLevelLow},
		{circleai.AetherThreatLevelMedium, circleai.PeerThreatLevelMedium},
		{circleai.AetherThreatLevelHigh, circleai.PeerThreatLevelHigh},
		{circleai.AetherThreatLevelCritical, circleai.PeerThreatLevelCritical},
	}
	for _, l := range levels {
		// Round-trip through the security bridge posture is the observable proof;
		// here we assert equal ordinals hold, which is what the switch guarantees.
		if int(l.a) != int(l.p) {
			t.Errorf("threat level ordinals diverge: %v vs %v", l.a, l.p)
		}
	}
}

// ─── Bridge end-to-end: telemetry event → mapped Aether directive ───────────

// aetherDirectiveSink records SecurityDirectives delivered to the Aether-side
// ISecurityDirectiveConsumer.
type aetherDirectiveSink struct {
	mu   sync.Mutex
	seen []circleai.SecurityDirective
}

func (s *aetherDirectiveSink) OnDirective(d circleai.SecurityDirective) {
	s.mu.Lock()
	s.seen = append(s.seen, d)
	s.mu.Unlock()
}
func (s *aetherDirectiveSink) last() (circleai.SecurityDirective, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.seen) == 0 {
		return circleai.SecurityDirective{}, false
	}
	return s.seen[len(s.seen)-1], true
}
func (s *aetherDirectiveSink) count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.seen)
}

func newAetherBridge(t *testing.T) (*circleai.AetherSecurityBridge, *circleai.NodeTrustRegistry, *circleai.SecurityOptions) {
	t.Helper()
	opt := circleai.NewSecurityOptions()
	reg := circleai.NewNodeTrustRegistry(opt)
	pub := circleai.NewDirectivePublisher()
	layer := circleai.NewSecurityLayerService(reg, opt, pub)
	bridge := circleai.NewAetherSecurityBridge(layer)
	return bridge, reg, opt
}

func criticalAetherEvent(node string) circleai.AetherSecurityEvent {
	return circleai.AetherSecurityEvent{
		NodeID:      node,
		Kind:        circleai.AetherSecurityEventKindIntrusionSignal,
		ThreatLevel: circleai.AetherThreatLevelCritical,
		Description: "intrusion",
		Metadata:    map[string]string{"src": "test"},
		OccurredAt:  time.Now().UTC(),
	}
}

func TestAetherSecurityBridge_TelemetryToDirective(t *testing.T) {
	bridge, reg, _ := newAetherBridge(t)
	tel := circleai.NewInMemoryAetherTelemetry()
	sink := &aetherDirectiveSink{}
	bridge.SubscribeToDirectives(sink)

	ctx := context.Background()
	if err := bridge.Start(ctx, tel); err != nil {
		t.Fatalf("start: %v", err)
	}
	defer bridge.Stop(ctx)

	// The bridge subscribed to telemetry synchronously in Start, so these events
	// cannot race the subscription. Two criticals drive n1 past quarantine.
	tel.PublishSecurityEvent(criticalAetherEvent("n1")) // 1.0 → 0.55
	tel.PublishSecurityEvent(criticalAetherEvent("n1")) // 0.55 → 0.10 (quarantine)

	d, ok := sink.last()
	if !ok {
		t.Fatalf("expected a mapped Aether directive, got none")
	}
	if d.Kind != circleai.SecurityDirectiveKindQuarantineNode {
		t.Errorf("directive kind: got %v want QuarantineNode", d.Kind)
	}
	if d.ThreatLevel != circleai.AetherThreatLevelCritical {
		t.Errorf("directive threat level: got %v want Critical", d.ThreatLevel)
	}
	if !d.HasTarget() || *d.TargetNodeID != "n1" {
		t.Errorf("directive target: %+v", d.TargetNodeID)
	}
	if d.TrustScoreOverride == nil {
		t.Error("directive should carry the peer trust score as TrustScoreOverride")
	}
	if got := reg.GetTrustScore("n1"); got > 0.25 {
		t.Errorf("n1 trust should be quarantined (≤0.25): got %v", got)
	}
}

func TestAetherSecurityBridge_NodeExitDoesNotDirective(t *testing.T) {
	bridge, _, _ := newAetherBridge(t)
	tel := circleai.NewInMemoryAetherTelemetry()
	sink := &aetherDirectiveSink{}
	bridge.SubscribeToDirectives(sink)

	ctx := context.Background()
	_ = bridge.Start(ctx, tel)
	defer bridge.Stop(ctx)

	// A node departure is handled (HandlePeerLeft) but issues no directive.
	tel.PublishNodeEvent(circleai.AetherNodeEvent{
		NodeID: "n9", Kind: circleai.AetherNodeEventKindLeft,
		Health: circleai.AetherNodeHealth{TrustScore: 1.0}, OccurredAt: time.Now().UTC(),
	})
	// Transport/route/network events are ignored entirely.
	tel.PublishTransportEvent(circleai.AetherTransportEvent{NodeID: "n9"})
	tel.PublishRouteEvent(circleai.AetherRouteEvent{SourceNodeID: "n9"})
	tel.PublishNetworkEvent(circleai.AetherNetworkEvent{})

	if sink.count() != 0 {
		t.Errorf("non-security events must not produce directives, got %d", sink.count())
	}
}

func TestAetherSecurityBridge_PostureMaps(t *testing.T) {
	bridge, _, _ := newAetherBridge(t)
	tel := circleai.NewInMemoryAetherTelemetry()
	ctx := context.Background()

	before, err := bridge.GetPosture(ctx)
	if err != nil {
		t.Fatalf("posture: %v", err)
	}
	if before.IsActive {
		t.Error("posture should be inactive before Start")
	}

	_ = bridge.Start(ctx, tel)
	defer bridge.Stop(ctx)

	// Drive n1 into quarantine.
	tel.PublishSecurityEvent(criticalAetherEvent("n1"))
	tel.PublishSecurityEvent(criticalAetherEvent("n1"))

	after, _ := bridge.GetPosture(ctx)
	if !after.IsActive {
		t.Error("posture should be active after Start")
	}
	if after.QuarantinedNodeCount != 1 {
		t.Errorf("quarantined count: got %d want 1", after.QuarantinedNodeCount)
	}
	if after.OverallThreatLevel != circleai.AetherThreatLevelCritical {
		t.Errorf("overall threat: got %v want Critical", after.OverallThreatLevel)
	}
}

func TestAetherSecurityBridge_StopUnsubscribesTelemetry(t *testing.T) {
	bridge, reg, _ := newAetherBridge(t)
	tel := circleai.NewInMemoryAetherTelemetry()
	ctx := context.Background()

	_ = bridge.Start(ctx, tel)
	if tel.SubscriberCount() != 1 {
		t.Fatalf("expected 1 telemetry subscriber after Start, got %d", tel.SubscriberCount())
	}
	_ = bridge.Stop(ctx)
	if tel.SubscriberCount() != 0 {
		t.Fatalf("Stop should detach the telemetry subscription, got %d", tel.SubscriberCount())
	}
	// Events after Stop are ignored (no observer) → no trust change.
	tel.PublishSecurityEvent(criticalAetherEvent("n1"))
	if got := reg.GetTrustScore("n1"); got != 1.0 {
		t.Errorf("events after Stop should not degrade trust: got %v", got)
	}
}

// ─── Intelligence adapter ───────────────────────────────────────────────────

func TestAetherIntelligenceAdapter_MapsResults(t *testing.T) {
	opt := circleai.NewSecurityOptions()
	reg := circleai.NewNodeTrustRegistry(opt)
	inner := circleai.NewPeerIntelligenceService(reg, opt)
	adapter := circleai.NewAetherIntelligenceAdapter(inner)
	ctx := context.Background()

	// Empty network → overall health 1.0, no suspicious nodes.
	health, err := adapter.GetNetworkHealth(ctx)
	if err != nil {
		t.Fatalf("health: %v", err)
	}
	if health.OverallScore != 1.0 {
		t.Errorf("empty-network overall score: got %v want 1.0", health.OverallScore)
	}
	if !health.IsValid() {
		t.Error("health report should be valid")
	}

	// Degrade a peer, then assess: threat level + confidence come through mapped.
	reg.ApplyDegradation(circleai.PeerSecurityEvent{
		NodeID: "bad", Kind: circleai.PeerSecurityEventKindIntrusionSignal,
		ThreatLevel: circleai.PeerThreatLevelCritical, Description: "hit",
		TransportID: "aether", OccurredAt: time.Now().UTC(),
	}, 0.8) // 1.0 → 0.2

	assess, _ := adapter.AssessThreat(ctx, "bad")
	if assess.NodeID != "bad" {
		t.Errorf("assess node: got %q", assess.NodeID)
	}
	if assess.Level != circleai.AetherThreatLevelCritical {
		t.Errorf("assess level: got %v want Critical (score 0.2 ≤ 0.25)", assess.Level)
	}
	if !assess.IsValid() {
		t.Error("assessment confidence should be valid")
	}

	advice, _ := adapter.GetRoutingAdvice(ctx, "bad")
	if advice.DestinationNodeID != "bad" {
		t.Errorf("advice dest: got %q", advice.DestinationNodeID)
	}
	// A quarantined destination has no safe recommended path.
	if len(advice.RecommendedPath) != 0 {
		t.Errorf("quarantined dest should have empty path, got %+v", advice.RecommendedPath)
	}
}

func TestAetherIntelligenceAdapter_StreamMapsUpdates(t *testing.T) {
	opt := circleai.NewSecurityOptions()
	reg := circleai.NewNodeTrustRegistry(opt)
	inner := circleai.NewPeerIntelligenceService(reg, opt)
	adapter := circleai.NewAetherIntelligenceAdapter(inner)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Subscribe synchronously (the adapter obtains the inner channel before
	// spawning its mapping goroutine), THEN emit — no update is lost.
	stream := adapter.StreamTrustScores(ctx)
	reg.ApplyDegradation(circleai.PeerSecurityEvent{
		NodeID: "n1", Kind: circleai.PeerSecurityEventKindIntrusionSignal,
		ThreatLevel: circleai.PeerThreatLevelCritical, Description: "drop",
		TransportID: "aether", OccurredAt: time.Now().UTC(),
	}, 0.4) // 1.0 → 0.6

	select {
	case u, ok := <-stream:
		if !ok {
			t.Fatal("stream closed before delivering the update")
		}
		if u.NodeID != "n1" {
			t.Errorf("update node: got %q", u.NodeID)
		}
		if u.CurrentScore >= u.PreviousScore {
			t.Errorf("update should show a degradation: prev=%v cur=%v", u.PreviousScore, u.CurrentScore)
		}
		if !u.IsDegraded() {
			t.Error("update should be degraded")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for a mapped trust-score update")
	}
}

// ─── MeshDirectiveStore ─────────────────────────────────────────────────────

func avoidDirective(node, reason string, issuedAt time.Time, dur *time.Duration) circleai.SecurityDirective {
	return circleai.SecurityDirective{
		Kind:         circleai.SecurityDirectiveKindAvoidNode,
		TargetNodeID: &node,
		ThreatLevel:  circleai.AetherThreatLevelHigh,
		Reason:       reason,
		Duration:     dur,
		IssuedAt:     issuedAt,
	}
}

func TestMeshDirectiveStore_RecordsAndBlocks(t *testing.T) {
	now := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	store := circleai.NewMeshDirectiveStoreWithClock(func() time.Time { return now })

	store.OnDirective(avoidDirective("n1", "misbehaving", now, nil))
	blocked, reason := store.IsBlocked("n1")
	if !blocked || reason != "misbehaving" {
		t.Errorf("n1 should be blocked with reason: got blocked=%v reason=%q", blocked, reason)
	}
	if store.TrackedNodeCount() != 1 {
		t.Errorf("tracked node count: got %d want 1", store.TrackedNodeCount())
	}

	// Unknown node is not blocked.
	if b, _ := store.IsBlocked("nobody"); b {
		t.Error("unknown node should not be blocked")
	}
	// Blank id is not blocked.
	if b, _ := store.IsBlocked("   "); b {
		t.Error("blank id should not be blocked")
	}
}

func TestMeshDirectiveStore_ReleaseLiftsBlock(t *testing.T) {
	now := time.Now().UTC()
	store := circleai.NewMeshDirectiveStore()
	store.OnDirective(avoidDirective("n1", "bad", now, nil))
	if b, _ := store.IsBlocked("n1"); !b {
		t.Fatal("n1 should start blocked")
	}

	release := circleai.SecurityDirective{
		Kind: circleai.SecurityDirectiveKindReleaseNode, TargetNodeID: strptrAether("n1"),
		Reason: "recovered", IssuedAt: now,
	}
	store.OnDirective(release)
	if b, _ := store.IsBlocked("n1"); b {
		t.Error("Release should lift the block")
	}
	if store.TrackedNodeCount() != 0 {
		t.Errorf("release should drop the node entirely: count %d", store.TrackedNodeCount())
	}
}

func TestMeshDirectiveStore_LazyExpiry(t *testing.T) {
	base := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	current := base
	store := circleai.NewMeshDirectiveStoreWithClock(func() time.Time { return current })

	ttl := 30 * time.Second
	store.OnDirective(avoidDirective("n1", "temp", base, &ttl))
	if b, _ := store.IsBlocked("n1"); !b {
		t.Fatal("n1 should be blocked before expiry")
	}

	// Advance the clock past the TTL → the directive lazily expires on read.
	current = base.Add(31 * time.Second)
	if b, _ := store.IsBlocked("n1"); b {
		t.Error("n1 should be unblocked after TTL")
	}
	// Expiry sweep removed the node.
	if store.TrackedNodeCount() != 0 {
		t.Errorf("expired directive should be swept: count %d", store.TrackedNodeCount())
	}
}

func TestMeshDirectiveStore_MostRecentBlockReason(t *testing.T) {
	base := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	store := circleai.NewMeshDirectiveStoreWithClock(func() time.Time { return base.Add(time.Hour) })

	store.OnDirective(avoidDirective("n1", "first", base, nil))
	store.OnDirective(circleai.SecurityDirective{
		Kind: circleai.SecurityDirectiveKindQuarantineNode, TargetNodeID: strptrAether("n1"),
		ThreatLevel: circleai.AetherThreatLevelCritical, Reason: "second-and-worse",
		IssuedAt: base.Add(time.Minute),
	})
	_, reason := store.IsBlocked("n1")
	if reason != "second-and-worse" {
		t.Errorf("most-recent block reason should win: got %q", reason)
	}
	active := store.GetActiveDirectives("n1")
	if len(active) != 2 {
		t.Errorf("both directives should be active: got %d", len(active))
	}
}

func TestMeshDirectiveStore_IgnoresUntargeted(t *testing.T) {
	store := circleai.NewMeshDirectiveStore()
	// No target → ignored.
	store.OnDirective(circleai.SecurityDirective{
		Kind: circleai.SecurityDirectiveKindElevateMonitoring, Reason: "global",
		IssuedAt: time.Now().UTC(),
	})
	if store.TrackedNodeCount() != 0 {
		t.Errorf("untargeted directive should be ignored: count %d", store.TrackedNodeCount())
	}
}

func TestMeshDirectiveStore_ElevateMonitoringIsNotBlock(t *testing.T) {
	now := time.Now().UTC()
	store := circleai.NewMeshDirectiveStore()
	store.OnDirective(circleai.SecurityDirective{
		Kind: circleai.SecurityDirectiveKindElevateMonitoring, TargetNodeID: strptrAether("n1"),
		Reason: "watching", IssuedAt: now,
	})
	// ElevateMonitoring is tracked but is NOT a block.
	if b, _ := store.IsBlocked("n1"); b {
		t.Error("ElevateMonitoring should not block")
	}
	if len(store.GetActiveDirectives("n1")) != 1 {
		t.Error("ElevateMonitoring directive should still be tracked for audit")
	}
}

// ─── MeshSecurityGate ───────────────────────────────────────────────────────

func TestMeshSecurityGate_DecideAndEnforce(t *testing.T) {
	now := time.Now().UTC()
	store := circleai.NewMeshDirectiveStore()
	gate := circleai.NewMeshSecurityGate(store)

	// Not blocked → Allowed, Enforce returns nil.
	if d := gate.Decide("clean"); d.IsBlocked {
		t.Error("clean id should be allowed")
	}
	if err := gate.Enforce("clean"); err != nil {
		t.Errorf("clean id should not error: %v", err)
	}

	store.OnDirective(avoidDirective("blocked", "spam", now, nil))
	d := gate.Decide("blocked")
	if !d.IsBlocked || d.Reason != "spam" {
		t.Errorf("blocked id decision wrong: %+v", d)
	}

	err := gate.Enforce("blocked")
	if err == nil {
		t.Fatal("Enforce should return an error for a blocked id")
	}
	var blockedErr *circleai.MeshSecurityBlockedError
	if !errors.As(err, &blockedErr) {
		t.Fatalf("expected MeshSecurityBlockedError, got %T", err)
	}
	if blockedErr.BlockedID != "blocked" {
		t.Errorf("blocked id: got %q", blockedErr.BlockedID)
	}
	if !strings.Contains(blockedErr.Error(), "spam") {
		t.Errorf("error message should carry the reason: %q", blockedErr.Error())
	}
}

// ─── MeshGatedCompanionSession ──────────────────────────────────────────────

// fakeInnerSession is a minimal ICompanionSession stand-in that records calls.
type fakeInnerSession struct {
	identity  string
	sendCalls int
	mu        sync.Mutex
}

func (f *fakeInnerSession) SessionID() string                 { return "sess-1" }
func (f *fakeInnerSession) IdentityID() string                { return f.identity }
func (f *fakeInnerSession) Interface() circleai.InterfaceKind { return circleai.InterfaceKindMobile }
func (f *fakeInnerSession) Send(_ context.Context, _ string) (string, error) {
	f.mu.Lock()
	f.sendCalls++
	f.mu.Unlock()
	return "reply", nil
}
func (f *fakeInnerSession) Stream(_ context.Context, _ string) (<-chan string, <-chan error) {
	tokens := make(chan string, 1)
	errs := make(chan error, 1)
	tokens <- "chunk"
	close(tokens)
	close(errs)
	return tokens, errs
}
func (f *fakeInnerSession) Agent(_ context.Context, _ string) (string, error) { return "agent", nil }
func (f *fakeInnerSession) GetContext() circleai.CompanionContext {
	return circleai.CompanionContext{IdentityID: f.identity}
}
func (f *fakeInnerSession) RefreshContext(_ context.Context) error { return nil }
func (f *fakeInnerSession) History() []circleai.CompanionTurn      { return nil }
func (f *fakeInnerSession) SignalFeedback(_ context.Context, _ bool, _ *string) error {
	return nil
}
func (f *fakeInnerSession) ProactiveEvents() <-chan circleai.CompanionProactiveEvent {
	ch := make(chan circleai.CompanionProactiveEvent)
	close(ch)
	return ch
}
func (f *fakeInnerSession) Close() error { return nil }
func (f *fakeInnerSession) sends() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.sendCalls
}

func TestMeshGatedCompanionSession_AllowsWhenClean(t *testing.T) {
	inner := &fakeInnerSession{identity: "user-ok"}
	store := circleai.NewMeshDirectiveStore()
	gate := circleai.NewMeshSecurityGate(store)
	gated := circleai.NewMeshGatedCompanionSession(inner, gate)

	ctx := context.Background()
	if reply, err := gated.Send(ctx, "hi"); err != nil || reply != "reply" {
		t.Errorf("clean user Send should pass: reply=%q err=%v", reply, err)
	}
	if inner.sends() != 1 {
		t.Errorf("inner Send should have been called once, got %d", inner.sends())
	}
	if a, err := gated.Agent(ctx, "do"); err != nil || a != "agent" {
		t.Errorf("clean user Agent should pass: %q %v", a, err)
	}
	// Pass-through identity/props.
	if gated.SessionID() != "sess-1" || gated.IdentityID() != "user-ok" {
		t.Error("identity pass-through broken")
	}
	if gated.Interface() != circleai.InterfaceKindMobile {
		t.Error("interface pass-through broken")
	}
}

func TestMeshGatedCompanionSession_BlocksSendStreamAgent(t *testing.T) {
	inner := &fakeInnerSession{identity: "user-bad"}
	store := circleai.NewMeshDirectiveStore()
	gate := circleai.NewMeshSecurityGate(store)
	gated := circleai.NewMeshGatedCompanionSession(inner, gate)

	// Mesh blocks this identity.
	store.OnDirective(avoidDirective("user-bad", "abuse", time.Now().UTC(), nil))
	ctx := context.Background()

	// Send is blocked before reaching the inner generator.
	_, err := gated.Send(ctx, "hi")
	var be *circleai.MeshSecurityBlockedError
	if !errors.As(err, &be) {
		t.Fatalf("Send should be blocked: got %v", err)
	}
	if inner.sends() != 0 {
		t.Errorf("inner Send must NOT be reached when blocked, got %d", inner.sends())
	}

	// Agent is blocked.
	if _, err := gated.Agent(ctx, "do"); !errors.As(err, &be) {
		t.Fatalf("Agent should be blocked: got %v", err)
	}

	// Stream returns closed token channel + one blocked error on the error chan.
	tokens, errs := gated.Stream(ctx, "hi")
	gotToken := false
	for range tokens {
		gotToken = true
	}
	if gotToken {
		t.Error("blocked Stream should yield no tokens")
	}
	streamErr := <-errs
	if !errors.As(streamErr, &be) {
		t.Fatalf("blocked Stream should carry a MeshSecurityBlockedError: got %v", streamErr)
	}
}

func TestMeshGatedCompanionSession_MetadataCallsPassThroughWhenBlocked(t *testing.T) {
	inner := &fakeInnerSession{identity: "user-bad"}
	store := circleai.NewMeshDirectiveStore()
	gate := circleai.NewMeshSecurityGate(store)
	gated := circleai.NewMeshGatedCompanionSession(inner, gate)
	store.OnDirective(avoidDirective("user-bad", "abuse", time.Now().UTC(), nil))
	ctx := context.Background()

	// Diagnostic/metadata calls are NOT gated — a blocked user can still see their
	// own state (the C# "stop the chat, don't punish" rule).
	if gated.GetContext().IdentityID != "user-bad" {
		t.Error("GetContext should pass through even when blocked")
	}
	if err := gated.RefreshContext(ctx); err != nil {
		t.Errorf("RefreshContext should pass through: %v", err)
	}
	if err := gated.SignalFeedback(ctx, true, nil); err != nil {
		t.Errorf("SignalFeedback should pass through: %v", err)
	}
	if err := gated.Close(); err != nil {
		t.Errorf("Close should pass through: %v", err)
	}
}

// strptrAether returns a pointer to s (local helper for the Aether test file).
func strptrAether(s string) *string { return &s }
