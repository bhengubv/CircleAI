// security_peer_types_test.go
//
// Verifies the Peer* enum ordinals (ported from PeerSecurityTypes.cs and the
// dispatcher / watchdog enums). Ordinals are wire-relevant and must stay stable
// across language ports — they mirror the C# declaration order (PeerThreatLevel
// is explicitly numbered None=0..Critical=4).

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestEnum_PeerSecurityEventKindOrdinals(t *testing.T) {
	cases := []struct {
		got  circleai.PeerSecurityEventKind
		want int
	}{
		{circleai.PeerSecurityEventKindAuthAttempt, 0},
		{circleai.PeerSecurityEventKindRoutingAnomaly, 1},
		{circleai.PeerSecurityEventKindBehaviourChange, 2},
		{circleai.PeerSecurityEventKindEncryptionEvent, 3},
		{circleai.PeerSecurityEventKindIntrusionSignal, 4},
		{circleai.PeerSecurityEventKindPrivilegeAttempt, 5},
		{circleai.PeerSecurityEventKindConnectionAnomaly, 6},
		{circleai.PeerSecurityEventKindDataExfiltration, 7},
		{circleai.PeerSecurityEventKindDenialOfService, 8},
		{circleai.PeerSecurityEventKindUnknown, 9},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("PeerSecurityEventKind %d: want ordinal %d", int(c.got), c.want)
		}
	}
}

func TestEnum_PeerThreatLevelOrdinals(t *testing.T) {
	cases := []struct {
		got  circleai.PeerThreatLevel
		want int
	}{
		{circleai.PeerThreatLevelNone, 0},
		{circleai.PeerThreatLevelLow, 1},
		{circleai.PeerThreatLevelMedium, 2},
		{circleai.PeerThreatLevelHigh, 3},
		{circleai.PeerThreatLevelCritical, 4},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("PeerThreatLevel %d: want ordinal %d", int(c.got), c.want)
		}
	}
}

func TestEnum_PeerDirectiveKindOrdinals(t *testing.T) {
	cases := []struct {
		got  circleai.PeerDirectiveKind
		want int
	}{
		{circleai.PeerDirectiveKindElevateMonitoring, 0},
		{circleai.PeerDirectiveKindAvoidNode, 1},
		{circleai.PeerDirectiveKindQuarantineNode, 2},
		{circleai.PeerDirectiveKindReleaseNode, 3},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("PeerDirectiveKind %d: want ordinal %d", int(c.got), c.want)
		}
	}
}

func TestEnum_AnomalyDispatchOutcomeOrdinals(t *testing.T) {
	cases := []struct {
		got  circleai.AnomalyDispatchOutcome
		want int
	}{
		{circleai.AnomalyDispatchOutcomeDispatched, 0},
		{circleai.AnomalyDispatchOutcomeDuplicate, 1},
		{circleai.AnomalyDispatchOutcomeBelowThreshold, 2},
		{circleai.AnomalyDispatchOutcomeUnverified, 3},
		{circleai.AnomalyDispatchOutcomeCancelled, 4},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("AnomalyDispatchOutcome %d: want ordinal %d", int(c.got), c.want)
		}
	}
}

func TestEnum_SecurityResponseKindOrdinals(t *testing.T) {
	cases := []struct {
		got  circleai.SecurityResponseKind
		want int
	}{
		{circleai.SecurityResponseKindNoAction, 0},
		{circleai.SecurityResponseKindKeyRotation, 1},
		{circleai.SecurityResponseKindSessionRevocation, 2},
		{circleai.SecurityResponseKindMeshIsolationSignal, 3},
		{circleai.SecurityResponseKindStateRollback, 4},
		{circleai.SecurityResponseKindComposite, 5},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("SecurityResponseKind %d: want ordinal %d", int(c.got), c.want)
		}
	}
}
