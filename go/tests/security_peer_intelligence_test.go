// security_peer_intelligence_test.go
//
// Verifies PeerIntelligenceService (ported from AetherIntelligenceService.cs):
//   - GetNetworkHealth: empty-network default, aggregate score + counts + summary.
//   - AssessThreat: level bands, confidence = deficit + 0.1×indicators (capped).
//   - GetRoutingAdvice: direct path only above avoid-threshold, avoid-list, F2
//     reasoning string.
//   - StreamTrustScores relays registry updates.

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func newIntel() (*circleai.PeerIntelligenceService, *circleai.NodeTrustRegistry) {
	opt := circleai.NewSecurityOptions()
	reg := circleai.NewNodeTrustRegistry(opt)
	return circleai.NewPeerIntelligenceService(reg, opt), reg
}

func degrade(reg *circleai.NodeTrustRegistry, node string, amount float64) {
	reg.ApplyDegradation(circleai.PeerSecurityEvent{
		NodeID: node, Kind: circleai.PeerSecurityEventKindAuthAttempt,
		ThreatLevel: circleai.PeerThreatLevelMedium, Description: "d",
		TransportID: "test", OccurredAt: time.Now().UTC(),
	}, amount)
}

func TestIntel_NetworkHealthEmpty(t *testing.T) {
	intel, _ := newIntel()
	h, _ := intel.GetNetworkHealth(context.Background())
	if h.OverallScore != 1.0 {
		t.Errorf("empty overall: got %v, want 1.0", h.OverallScore)
	}
	if h.Summary != "No peers observed." {
		t.Errorf("empty summary: got %q", h.Summary)
	}
}

func TestIntel_NetworkHealthAggregate(t *testing.T) {
	intel, reg := newIntel()
	// n1 = 1.0 (trusted), n2 = 0.4 (suspicious & below avoid).
	reg.GetOrCreate("n1")
	degrade(reg, "n2", 0.6) // 1.0 → 0.4
	h, _ := intel.GetNetworkHealth(context.Background())
	// overall = (1.0 + 0.4)/2 = 0.7 → "degraded" summary (>0.50).
	if h.OverallScore < 0.699 || h.OverallScore > 0.701 {
		t.Errorf("overall: got %v, want ~0.7", h.OverallScore)
	}
	if h.TrustedPeerCount != 1 { // only n1 > 0.50
		t.Errorf("trusted: got %d, want 1", h.TrustedPeerCount)
	}
	if h.SuspiciousPeerCount != 1 { // n2 ≤ 0.75
		t.Errorf("suspicious: got %d, want 1", h.SuspiciousPeerCount)
	}
	if !strings.Contains(h.Summary, "degraded") {
		t.Errorf("summary: got %q, want 'degraded'", h.Summary)
	}
}

func TestIntel_AssessThreatLevelsAndConfidence(t *testing.T) {
	intel, reg := newIntel()
	// Trusted peer: score 1.0 → None, confidence 0.
	reg.GetOrCreate("trusted")
	a, _ := intel.AssessThreat(context.Background(), "trusted")
	if a.ThreatLevel != circleai.PeerThreatLevelNone {
		t.Errorf("trusted level: got %v", a.ThreatLevel)
	}
	if a.Confidence != 0.0 {
		t.Errorf("trusted confidence: got %v, want 0", a.Confidence)
	}

	// Quarantined peer: drive to 0.10 → Critical, deficit 0.90.
	degrade(reg, "bad", 0.9) // 1.0 → 0.1
	b, _ := intel.AssessThreat(context.Background(), "bad")
	if b.ThreatLevel != circleai.PeerThreatLevelCritical {
		t.Errorf("bad level: got %v, want Critical", b.ThreatLevel)
	}
	if b.Confidence < 0.89 || b.Confidence > 0.91 {
		t.Errorf("bad confidence: got %v, want ~0.90", b.Confidence)
	}
}

func TestIntel_AssessThreatConfidenceCappedAtOne(t *testing.T) {
	intel, reg := newIntel()
	now := time.Now().UTC()
	// Drive score to 0 (deficit 1.0) and add indicators (each +0.1) — the sum
	// exceeds 1.0 and must cap.
	for i := 0; i < 3; i++ {
		reg.ApplyDegradation(circleai.PeerSecurityEvent{
			NodeID: "x", Kind: circleai.PeerSecurityEventKindAuthAttempt,
			ThreatLevel: circleai.PeerThreatLevelCritical, Description: "auth",
			TransportID: "test", OccurredAt: now,
		}, 1.0)
	}
	a, _ := intel.AssessThreat(context.Background(), "x")
	if a.Confidence != 1.0 {
		t.Errorf("confidence should cap at 1.0: got %v", a.Confidence)
	}
	// 3 auth attempts within window → at least the brute-force indicator.
	if len(a.Indicators) == 0 {
		t.Error("expected indicators for repeated auth attempts")
	}
}

func TestIntel_RoutingAdviceDirectWhenTrusted(t *testing.T) {
	intel, reg := newIntel()
	reg.GetOrCreate("dest") // score 1.0
	adv, _ := intel.GetRoutingAdvice(context.Background(), "dest")
	if len(adv.RecommendedPath) != 1 || adv.RecommendedPath[0] != "dest" {
		t.Errorf("recommended path: got %v, want [dest]", adv.RecommendedPath)
	}
	if adv.Confidence != 1.0 {
		t.Errorf("confidence: got %v", adv.Confidence)
	}
	if !strings.Contains(adv.Reasoning, "1.00") {
		t.Errorf("reasoning should carry F2 score 1.00: got %q", adv.Reasoning)
	}
}

func TestIntel_RoutingAdviceAvoidsCompromised(t *testing.T) {
	intel, reg := newIntel()
	degrade(reg, "dest", 0.8) // 1.0 → 0.2 (≤ avoid threshold 0.50)
	adv, _ := intel.GetRoutingAdvice(context.Background(), "dest")
	if len(adv.RecommendedPath) != 0 {
		t.Errorf("no direct path when below avoid threshold: got %v", adv.RecommendedPath)
	}
	found := false
	for _, id := range adv.AvoidNodeIDs {
		if id == "dest" {
			found = true
		}
	}
	if !found {
		t.Errorf("dest should be in avoid list: got %v", adv.AvoidNodeIDs)
	}
	if !strings.Contains(adv.Reasoning, "quarantined") {
		t.Errorf("reasoning for 0.2 score should be 'quarantined': got %q", adv.Reasoning)
	}
}

func TestIntel_StreamTrustScoresRelays(t *testing.T) {
	intel, reg := newIntel()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	stream := intel.StreamTrustScores(ctx)
	degrade(reg, "n1", 0.3)
	select {
	case u, ok := <-stream:
		if !ok {
			t.Fatal("stream closed unexpectedly")
		}
		if u.NodeID != "n1" {
			t.Errorf("node: got %q", u.NodeID)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("no update relayed")
	}
}
