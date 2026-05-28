// security.go
//
// ThreatVector + AnomalySignal — the portable schema half of the Circle AI
// security pipeline. The watchdog implementation stays C# host-side; every
// language port carries identical detection types so signals serialise
// 1:1 across the network.
//
// Ordinals on ThreatVector are stable across language ports — never reorder.
// New values must be appended at the end (before ThreatVectorUnknown is
// preserved at ordinal 7 as the explicit "fall through" sentinel).

package circleai

import (
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// ThreatVector
// ---------------------------------------------------------------------------

// ThreatVector classifies the kind of locally-detected runtime anomaly.
// Ordinals are stable across language ports — see fixtures/anomaly_signal_schema.json.
type ThreatVector int

const (
	// ThreatVectorMemoryAnomaly indicates suspect memory / store mutation
	// (e.g. unexpected episodic-memory rewrite, persona corruption).
	ThreatVectorMemoryAnomaly ThreatVector = 0

	// ThreatVectorControlFlowDrift indicates the companion pipeline diverged
	// from its expected control-flow graph (e.g. tool-use ordering anomaly).
	ThreatVectorControlFlowDrift ThreatVector = 1

	// ThreatVectorPrivilegeEscalation indicates an attempt to acquire a
	// capability above the caller's tier (e.g. agent invoking a tool
	// reserved for verified identities).
	ThreatVectorPrivilegeEscalation ThreatVector = 2

	// ThreatVectorBiometricSpoofAttempt indicates a presentation-attack
	// signal from the biometric matcher (e.g. liveness failure, replay).
	ThreatVectorBiometricSpoofAttempt ThreatVector = 3

	// ThreatVectorNetworkPivot indicates the mesh layer observed an
	// unexpected lateral connection attempt (e.g. peer requesting a
	// store namespace outside its assigned scope).
	ThreatVectorNetworkPivot ThreatVector = 4

	// ThreatVectorStateCorruption indicates a checksum / invariant
	// violation on a persisted store (affect, persona, goal, episodic).
	ThreatVectorStateCorruption ThreatVector = 5

	// ThreatVectorAgentPatchRejected indicates an agent-proposed self-patch
	// was rejected by the gate (signature, capability, or policy mismatch).
	ThreatVectorAgentPatchRejected ThreatVector = 6

	// ThreatVectorUnknown is the explicit fall-through sentinel for signals
	// that do not match a known taxonomy entry. Always last.
	ThreatVectorUnknown ThreatVector = 7
)

// ---------------------------------------------------------------------------
// AnomalySignal
// ---------------------------------------------------------------------------

// AnomalySignal carries the details of a locally-detected runtime anomaly
// from the detection site to the watchdog handler. Signals are immutable
// once created — detection sites construct them and hand them off; the
// watchdog (or any ops-security agent) reads them and decides the response.
type AnomalySignal struct {
	// ID is a unique identifier for this signal instance (UUID v4 string).
	ID string

	// Vector is the classification of the detected threat.
	Vector ThreatVector

	// Confidence is the likelihood this is a genuine threat, in [0.0, 1.0].
	// 1.0 = definitive; 0.0 = speculative. NewAnomalySignal clamps the input.
	Confidence float32

	// AffectedModule names the module or subsystem where the anomaly was
	// detected (e.g. "Circle.AI.Companion", "Circle.AI.Identity").
	AffectedModule string

	// Description is a human-readable description of the anomaly.
	Description string

	// Evidence holds optional structured evidence attached by the detection
	// site. Keys are evidence labels; values are serialised data or hashes.
	// NewAnomalySignal substitutes an empty map when nil is passed.
	Evidence map[string]string

	// DetectedAt is the UTC timestamp of detection.
	DetectedAt time.Time
}

// NewAnomalySignal constructs an AnomalySignal, stamping a fresh UUID v4
// and the current UTC time. Confidence is clamped to [0.0, 1.0]; a nil
// evidence map is replaced with an empty map.
func NewAnomalySignal(
	vector ThreatVector,
	confidence float32,
	affectedModule string,
	description string,
	evidence map[string]string,
) *AnomalySignal {
	if evidence == nil {
		evidence = map[string]string{}
	}
	if confidence < 0 {
		confidence = 0
	}
	if confidence > 1 {
		confidence = 1
	}
	return &AnomalySignal{
		ID:             uuid.NewString(),
		Vector:         vector,
		Confidence:     confidence,
		AffectedModule: affectedModule,
		Description:    description,
		Evidence:       evidence,
		DetectedAt:     time.Now().UTC(),
	}
}
