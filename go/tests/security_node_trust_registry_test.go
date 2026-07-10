// security_node_trust_registry_test.go
//
// Verifies NodeTrustRegistry (ported from NodeTrustRegistry.cs):
//   - GetOrCreate seeds InitialTrustScore; unknown peers read InitialTrustScore.
//   - ApplyDegradation clamps to [0,1], records the event, and publishes a
//     PeerTrustScoreUpdate on the unbounded channel.
//   - ApplyRecovery heals below-1.0 peers and skips full-trust peers.
//   - Event history is windowed and bounded by MaxEventsPerNode.
//   - TrustScoreUpdates streams changes (competing-consumer, unbounded buffer).

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func evAt(node string, kind circleai.PeerSecurityEventKind, level circleai.PeerThreatLevel, at time.Time) circleai.PeerSecurityEvent {
	return circleai.PeerSecurityEvent{
		NodeID: node, Kind: kind, ThreatLevel: level,
		Description: "ev", TransportID: "test", OccurredAt: at,
	}
}

func TestRegistry_InitialTrustScore(t *testing.T) {
	opt := circleai.NewSecurityOptions()
	r := circleai.NewNodeTrustRegistry(opt)
	if got := r.GetTrustScore("unknown"); got != opt.InitialTrustScore {
		t.Errorf("unknown peer score: got %v, want %v", got, opt.InitialTrustScore)
	}
	e := r.GetOrCreate("n1")
	if e.TrustScore != opt.InitialTrustScore {
		t.Errorf("seeded score: got %v", e.TrustScore)
	}
}

func TestRegistry_ApplyDegradationClampsAndRecordsEvent(t *testing.T) {
	r := circleai.NewNodeTrustRegistry(circleai.NewSecurityOptions())
	now := time.Now().UTC()
	prev, cur := r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelMedium, now), 0.3)
	if prev != 1.0 {
		t.Errorf("prev: got %v, want 1.0", prev)
	}
	if cur < 0.699 || cur > 0.701 {
		t.Errorf("cur: got %v, want ~0.7", cur)
	}
	// Over-degrade clamps at 0.
	_, cur2 := r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindIntrusionSignal, circleai.PeerThreatLevelCritical, now), 5.0)
	if cur2 != 0.0 {
		t.Errorf("clamp low: got %v, want 0", cur2)
	}
	if evs := r.GetRecentEvents("n1"); len(evs) != 2 {
		t.Errorf("recent events: got %d, want 2", len(evs))
	}
}

func TestRegistry_ApplyRecoveryHealsAndSkipsFull(t *testing.T) {
	opt := circleai.NewSecurityOptions()
	opt.RecoveryRatePerSecond = 0.1
	r := circleai.NewNodeTrustRegistry(opt)
	now := time.Now().UTC()

	// n1 degraded to 0.5; n2 stays at full trust.
	r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelMedium, now), 0.5)
	r.GetOrCreate("n2") // full trust

	r.ApplyRecovery(2 * time.Second) // +0.2
	if got := r.GetTrustScore("n1"); got < 0.699 || got > 0.701 {
		t.Errorf("n1 after recovery: got %v, want ~0.7", got)
	}
	if got := r.GetTrustScore("n2"); got != 1.0 {
		t.Errorf("n2 should stay 1.0: got %v", got)
	}
}

func TestRegistry_RecoveryCapsAtOne(t *testing.T) {
	opt := circleai.NewSecurityOptions()
	opt.RecoveryRatePerSecond = 1.0
	r := circleai.NewNodeTrustRegistry(opt)
	now := time.Now().UTC()
	r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelMedium, now), 0.5)
	r.ApplyRecovery(10 * time.Second) // would overshoot; must cap at 1.0
	if got := r.GetTrustScore("n1"); got != 1.0 {
		t.Errorf("recovery cap: got %v, want 1.0", got)
	}
}

func TestRegistry_EventHistoryBounded(t *testing.T) {
	opt := circleai.NewSecurityOptions()
	opt.MaxEventsPerNode = 3
	r := circleai.NewNodeTrustRegistry(opt)
	now := time.Now().UTC()
	for i := 0; i < 10; i++ {
		r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, now), 0.01)
	}
	if evs := r.GetRecentEvents("n1"); len(evs) != 3 {
		t.Errorf("history should be bounded to 3, got %d", len(evs))
	}
}

func TestRegistry_GetRecentEventsWindowed(t *testing.T) {
	opt := circleai.NewSecurityOptions()
	opt.EventWindow = 5 * time.Minute
	r := circleai.NewNodeTrustRegistry(opt)
	now := time.Now().UTC()
	old := now.Add(-10 * time.Minute)
	r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, old), 0.01)
	r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, now), 0.01)
	if evs := r.GetRecentEvents("n1"); len(evs) != 1 {
		t.Errorf("windowed recent events: got %d, want 1 (old excluded)", len(evs))
	}
}

func TestRegistry_UnknownNodeRecentEventsEmpty(t *testing.T) {
	r := circleai.NewNodeTrustRegistry(circleai.NewSecurityOptions())
	if evs := r.GetRecentEvents("ghost"); len(evs) != 0 {
		t.Errorf("unknown node events: got %d, want 0", len(evs))
	}
}

func TestRegistry_TrustScoreUpdatesStreamed(t *testing.T) {
	r := circleai.NewNodeTrustRegistry(circleai.NewSecurityOptions())
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Subscribe synchronously BEFORE producing the change.
	updates := r.TrustScoreUpdates(ctx)

	now := time.Now().UTC()
	r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelMedium, now), 0.3)

	select {
	case u, ok := <-updates:
		if !ok {
			t.Fatal("stream closed unexpectedly")
		}
		if u.NodeID != "n1" {
			t.Errorf("update node: got %q", u.NodeID)
		}
		if u.PreviousScore != 1.0 {
			t.Errorf("update prev: got %v", u.PreviousScore)
		}
		if u.NewScore < 0.699 || u.NewScore > 0.701 {
			t.Errorf("update new: got %v", u.NewScore)
		}
		if u.Reason == "" {
			t.Error("update reason empty")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("no trust-score update received")
	}
}

func TestRegistry_NoUpdateWhenScoreUnchanged(t *testing.T) {
	// Degradation of exactly 0 (None level path uses ComputeDegradation, but here
	// we call ApplyDegradation directly with a sub-epsilon amount) → no publish.
	r := circleai.NewNodeTrustRegistry(circleai.NewSecurityOptions())
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	updates := r.TrustScoreUpdates(ctx)
	now := time.Now().UTC()
	// 0.00005 is below the 0.0001 publish epsilon.
	r.ApplyDegradation(evAt("n1", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, now), 0.00005)

	select {
	case <-updates:
		t.Error("no update should be published for a sub-epsilon change")
	case <-time.After(200 * time.Millisecond):
		// expected: nothing arrives
	}
}
