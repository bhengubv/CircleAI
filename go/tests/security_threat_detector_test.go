// security_threat_detector_test.go
//
// Verifies ThreatDetector (ported from ThreatDetector.cs):
//   - ComputeDegradation = BaseWeight(kind) × ThreatMultiplier(level), 0 on None.
//   - DetectIndicators emits the exact tag set and ORDER the C# reference does,
//     honours the sliding window, and returns empty when no patterns match.

package circleai_test

import (
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

const secEpsilon = 1e-9

func secEvent(kind circleai.PeerSecurityEventKind, level circleai.PeerThreatLevel, at time.Time) circleai.PeerSecurityEvent {
	return circleai.PeerSecurityEvent{
		NodeID:      "node-1",
		Kind:        kind,
		ThreatLevel: level,
		Description: "test event",
		TransportID: "test",
		OccurredAt:  at,
	}
}

func TestComputeDegradation_WeightsAndMultipliers(t *testing.T) {
	now := time.Now().UTC()
	cases := []struct {
		name  string
		kind  circleai.PeerSecurityEventKind
		level circleai.PeerThreatLevel
		want  float64
	}{
		{"auth-medium", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelMedium, 0.05 * 1.0},
		{"auth-low", circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, 0.05 * 0.5},
		{"intrusion-critical", circleai.PeerSecurityEventKindIntrusionSignal, circleai.PeerThreatLevelCritical, 0.15 * 3.0},
		{"exfil-high", circleai.PeerSecurityEventKindDataExfiltration, circleai.PeerThreatLevelHigh, 0.14 * 2.0},
		{"dos-medium", circleai.PeerSecurityEventKindDenialOfService, circleai.PeerThreatLevelMedium, 0.13},
		{"privilege-high", circleai.PeerSecurityEventKindPrivilegeAttempt, circleai.PeerThreatLevelHigh, 0.12 * 2.0},
		{"unknown-medium", circleai.PeerSecurityEventKindUnknown, circleai.PeerThreatLevelMedium, 0.05},
		{"none-level-zero", circleai.PeerSecurityEventKindIntrusionSignal, circleai.PeerThreatLevelNone, 0.0},
	}
	for _, c := range cases {
		c := c
		t.Run(c.name, func(t *testing.T) {
			got := circleai.ComputeDegradation(secEvent(c.kind, c.level, now))
			if math.Abs(got-c.want) > secEpsilon {
				t.Errorf("ComputeDegradation=%v want %v", got, c.want)
			}
		})
	}
}

func TestComputeDegradation_NoneIsAlwaysZero(t *testing.T) {
	now := time.Now().UTC()
	for k := circleai.PeerSecurityEventKindAuthAttempt; k <= circleai.PeerSecurityEventKindUnknown; k++ {
		if got := circleai.ComputeDegradation(secEvent(k, circleai.PeerThreatLevelNone, now)); got != 0 {
			t.Errorf("kind %d with None level: got %v, want 0", k, got)
		}
	}
}

func TestDetectIndicators_EmptyWhenNoEvents(t *testing.T) {
	got := circleai.DetectIndicators(nil, 5*time.Minute)
	if len(got) != 0 {
		t.Errorf("expected empty, got %v", got)
	}
}

func TestDetectIndicators_AllPatternsInOrder(t *testing.T) {
	now := time.Now().UTC()
	// Build a window that trips every indicator:
	//   3 auth attempts, an intrusion signal, a critical event, ≥3 distinct
	//   kinds, a privilege attempt, and a data-exfiltration event.
	events := []circleai.PeerSecurityEvent{
		secEvent(circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, now),
		secEvent(circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, now),
		secEvent(circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, now),
		secEvent(circleai.PeerSecurityEventKindIntrusionSignal, circleai.PeerThreatLevelCritical, now),
		secEvent(circleai.PeerSecurityEventKindPrivilegeAttempt, circleai.PeerThreatLevelHigh, now),
		secEvent(circleai.PeerSecurityEventKindDataExfiltration, circleai.PeerThreatLevelMedium, now),
	}
	got := circleai.DetectIndicators(events, 5*time.Minute)
	want := []string{
		"repeated-auth-attempts",
		"intrusion-signal-detected",
		"high-severity-event",
		"multi-vector-activity",
		"privilege-escalation-attempt",
		"data-exfiltration-signal",
	}
	if len(got) != len(want) {
		t.Fatalf("indicator count: got %d %v, want %d %v", len(got), got, len(want), want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("indicator[%d]: got %q, want %q (full: %v)", i, got[i], want[i], got)
		}
	}
}

func TestDetectIndicators_WindowExcludesOldEvents(t *testing.T) {
	now := time.Now().UTC()
	old := now.Add(-10 * time.Minute) // outside a 5-minute window
	events := []circleai.PeerSecurityEvent{
		secEvent(circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, old),
		secEvent(circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, old),
		secEvent(circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, old),
	}
	got := circleai.DetectIndicators(events, 5*time.Minute)
	if len(got) != 0 {
		t.Errorf("old events should be excluded by window, got %v", got)
	}
}

func TestDetectIndicators_TwoAuthAttemptsBelowThreshold(t *testing.T) {
	now := time.Now().UTC()
	events := []circleai.PeerSecurityEvent{
		secEvent(circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, now),
		secEvent(circleai.PeerSecurityEventKindAuthAttempt, circleai.PeerThreatLevelLow, now),
	}
	got := circleai.DetectIndicators(events, 5*time.Minute)
	for _, ind := range got {
		if ind == "repeated-auth-attempts" {
			t.Error("2 auth attempts should NOT trip repeated-auth-attempts (threshold is 3)")
		}
	}
}
