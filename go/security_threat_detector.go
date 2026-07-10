// security_threat_detector.go
//
// Ports CircleAI.Security.ThreatDetector (ThreatDetector.cs).
//
// Pure, stateless threat logic — no state, no DI, fully testable in isolation.
// Two responsibilities:
//   1. ComputeDegradation: how much trust a single security event should cost.
//   2. DetectIndicators:   which behavioural patterns are visible in a window.
//
// Transport-agnostic: operates on PeerSecurityEvent / PeerSecurityEventKind /
// PeerThreatLevel only. Implemented as package-level funcs (the C# type is a
// static class).

package circleai

import "time"

// threatDetectorBaseWeight returns the degradation weight for a security event
// kind. Mirrors ThreatDetector.BaseWeight.
func threatDetectorBaseWeight(kind PeerSecurityEventKind) float64 {
	switch kind {
	case PeerSecurityEventKindAuthAttempt:
		return 0.05
	case PeerSecurityEventKindRoutingAnomaly:
		return 0.10
	case PeerSecurityEventKindBehaviourChange:
		return 0.08
	case PeerSecurityEventKindEncryptionEvent:
		return 0.06
	case PeerSecurityEventKindIntrusionSignal:
		return 0.15
	case PeerSecurityEventKindPrivilegeAttempt:
		return 0.12
	case PeerSecurityEventKindConnectionAnomaly:
		return 0.07
	case PeerSecurityEventKindDataExfiltration:
		return 0.14
	case PeerSecurityEventKindDenialOfService:
		return 0.13
	default:
		return 0.05
	}
}

// threatDetectorThreatMultiplier returns the degradation multiplier for a threat
// level. Mirrors ThreatDetector.ThreatMultiplier.
func threatDetectorThreatMultiplier(level PeerThreatLevel) float64 {
	switch level {
	case PeerThreatLevelNone:
		return 0.0
	case PeerThreatLevelLow:
		return 0.5
	case PeerThreatLevelMedium:
		return 1.0
	case PeerThreatLevelHigh:
		return 2.0
	case PeerThreatLevelCritical:
		return 3.0
	default:
		return 1.0
	}
}

// ComputeDegradation returns the trust-score degradation amount for a security
// event, calculated as BaseWeight(kind) × ThreatMultiplier(level). Returns 0
// when PeerThreatLevelNone. Ports ThreatDetector.ComputeDegradation.
func ComputeDegradation(e PeerSecurityEvent) float64 {
	return threatDetectorBaseWeight(e.Kind) * threatDetectorThreatMultiplier(e.ThreatLevel)
}

// DetectIndicators derives human-readable threat indicator tags from a set of
// recent events within the given window. Returns an empty slice when no
// patterns are detected. Ports ThreatDetector.DetectIndicators.
//
// The order of appended indicators matches the C# reference exactly so any
// downstream ordinal comparison stays byte-stable.
func DetectIndicators(recentEvents []PeerSecurityEvent, window time.Duration) []string {
	cutoff := time.Now().UTC().Add(-window)

	windowed := make([]PeerSecurityEvent, 0, len(recentEvents))
	for _, e := range recentEvents {
		// C#: e.OccurredAt >= cutoff
		if !e.OccurredAt.Before(cutoff) {
			windowed = append(windowed, e)
		}
	}

	if len(windowed) == 0 {
		return []string{}
	}

	indicators := make([]string, 0, 6)

	// ≥ 3 auth attempts within the window → brute-force signal.
	authCount := 0
	for _, e := range windowed {
		if e.Kind == PeerSecurityEventKindAuthAttempt {
			authCount++
		}
	}
	if authCount >= 3 {
		indicators = append(indicators, "repeated-auth-attempts")
	}

	// Any intrusion signal → explicit probe or exploit.
	if anyEventKind(windowed, PeerSecurityEventKindIntrusionSignal) {
		indicators = append(indicators, "intrusion-signal-detected")
	}

	// High or Critical event → severity flag.
	hasHighSeverity := false
	for _, e := range windowed {
		if e.ThreatLevel == PeerThreatLevelHigh || e.ThreatLevel == PeerThreatLevelCritical {
			hasHighSeverity = true
			break
		}
	}
	if hasHighSeverity {
		indicators = append(indicators, "high-severity-event")
	}

	// ≥ 3 distinct event kinds → multi-vector activity.
	distinct := make(map[PeerSecurityEventKind]struct{}, len(windowed))
	for _, e := range windowed {
		distinct[e.Kind] = struct{}{}
	}
	if len(distinct) >= 3 {
		indicators = append(indicators, "multi-vector-activity")
	}

	// Privilege escalation attempt.
	if anyEventKind(windowed, PeerSecurityEventKindPrivilegeAttempt) {
		indicators = append(indicators, "privilege-escalation-attempt")
	}

	// Data exfiltration signal.
	if anyEventKind(windowed, PeerSecurityEventKindDataExfiltration) {
		indicators = append(indicators, "data-exfiltration-signal")
	}

	return indicators
}

func anyEventKind(events []PeerSecurityEvent, kind PeerSecurityEventKind) bool {
	for _, e := range events {
		if e.Kind == kind {
			return true
		}
	}
	return false
}
