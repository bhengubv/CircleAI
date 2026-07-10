// security_response.go
//
// Ports CircleAI.Security.SecurityResponse + SecurityResponseKind
// (SecurityResponse.cs).
//
// Describes the protective action taken by ISecurityWatchdog in response to an
// AnomalySignal. Returned from OnAnomalyDetected so calling code (ops-security
// agent, host application) knows what was done.

package circleai

import (
	"fmt"
	"time"
)

// SecurityResponseKind is the type of protective action taken in response to an
// AnomalySignal. Ports SecurityResponseKind. Ordinals follow the C# declaration
// order.
type SecurityResponseKind int

const (
	// SecurityResponseKindNoAction — no action; confidence below threshold or
	// vector is informational.
	SecurityResponseKindNoAction SecurityResponseKind = 0
	// SecurityResponseKindKeyRotation — the session's ephemeral UHID key ring
	// was regenerated; prior session keys are revoked and all in-flight requests
	// using old keys will fail.
	SecurityResponseKindKeyRotation SecurityResponseKind = 1
	// SecurityResponseKindSessionRevocation — the affected session or execution
	// sandbox was marked untrusted and isolated from the rest of the runtime.
	SecurityResponseKindSessionRevocation SecurityResponseKind = 2
	// SecurityResponseKindMeshIsolationSignal — a PeerDirective was issued to
	// surrounding mesh nodes to isolate the suspected attack origin.
	SecurityResponseKindMeshIsolationSignal SecurityResponseKind = 3
	// SecurityResponseKindStateRollback — state was rolled back to the most
	// recent verified SecurityCheckpoint.
	SecurityResponseKindStateRollback SecurityResponseKind = 4
	// SecurityResponseKindComposite — a combination of responses was applied
	// (e.g. key rotation + mesh isolation). See AppliedActions for the full list.
	SecurityResponseKindComposite SecurityResponseKind = 5
)

// SecurityResponse describes the protective action taken by ISecurityWatchdog in
// response to an AnomalySignal. Ports the SecurityResponse record.
type SecurityResponse struct {
	// SignalID identifies the AnomalySignal that triggered this response.
	SignalID string
	// Kind is the primary response kind.
	Kind SecurityResponseKind
	// AppliedActions lists each individual action applied when Kind is
	// Composite. Empty for single-action responses.
	AppliedActions []SecurityResponseKind
	// Description is a human-readable description of what was done and why.
	Description string
	// RestoredCheckpoint is the SecurityCheckpoint that was restored, if any.
	// nil when Kind is not StateRollback.
	RestoredCheckpoint *SecurityCheckpoint
	// RespondedAt is the UTC timestamp of the response.
	RespondedAt time.Time
}

// NewNoActionResponse creates a no-action response for low-confidence or
// informational signals. Ports SecurityResponse.NoAction.
func NewNoActionResponse(signalID, reason string) SecurityResponse {
	return SecurityResponse{
		SignalID:           signalID,
		Kind:               SecurityResponseKindNoAction,
		AppliedActions:     []SecurityResponseKind{},
		Description:        reason,
		RestoredCheckpoint: nil,
		RespondedAt:        time.Now().UTC(),
	}
}

// NewKeyRotationResponse creates a key-rotation response. Ports
// SecurityResponse.ForKeyRotation.
func NewKeyRotationResponse(signalID, description string) SecurityResponse {
	return SecurityResponse{
		SignalID:           signalID,
		Kind:               SecurityResponseKindKeyRotation,
		AppliedActions:     []SecurityResponseKind{},
		Description:        description,
		RestoredCheckpoint: nil,
		RespondedAt:        time.Now().UTC(),
	}
}

// NewRollbackResponse creates a state-rollback response, recording the restored
// checkpoint. Ports SecurityResponse.ForRollback.
func NewRollbackResponse(signalID string, restored *SecurityCheckpoint) SecurityResponse {
	return SecurityResponse{
		SignalID:           signalID,
		Kind:               SecurityResponseKindStateRollback,
		AppliedActions:     []SecurityResponseKind{},
		Description:        fmt.Sprintf("State rolled back to checkpoint %s (%s).", restored.ID, restored.ModuleLabel),
		RestoredCheckpoint: restored,
		RespondedAt:        time.Now().UTC(),
	}
}

// NewCompositeResponse creates a composite response from multiple individual
// actions. Ports SecurityResponse.Composite. restoredCheckpoint may be nil.
func NewCompositeResponse(signalID string, actions []SecurityResponseKind, description string, restoredCheckpoint *SecurityCheckpoint) SecurityResponse {
	return SecurityResponse{
		SignalID:           signalID,
		Kind:               SecurityResponseKindComposite,
		AppliedActions:     actions,
		Description:        description,
		RestoredCheckpoint: restoredCheckpoint,
		RespondedAt:        time.Now().UTC(),
	}
}
