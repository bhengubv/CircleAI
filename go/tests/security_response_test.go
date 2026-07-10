// security_response_test.go
//
// Verifies SecurityResponse factories (ported from SecurityResponse.cs):
//   NoAction / ForKeyRotation / ForRollback / Composite set the correct Kind,
//   AppliedActions, and RestoredCheckpoint.

package circleai_test

import (
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestSecurityResponse_NoAction(t *testing.T) {
	r := circleai.NewNoActionResponse("sig-1", "below threshold")
	if r.Kind != circleai.SecurityResponseKindNoAction {
		t.Errorf("kind: got %v", r.Kind)
	}
	if len(r.AppliedActions) != 0 {
		t.Errorf("AppliedActions should be empty, got %v", r.AppliedActions)
	}
	if r.RestoredCheckpoint != nil {
		t.Error("RestoredCheckpoint should be nil")
	}
	if r.SignalID != "sig-1" || r.Description != "below threshold" {
		t.Errorf("fields mismatch: %+v", r)
	}
}

func TestSecurityResponse_ForKeyRotation(t *testing.T) {
	r := circleai.NewKeyRotationResponse("sig-2", "rotate now")
	if r.Kind != circleai.SecurityResponseKindKeyRotation {
		t.Errorf("kind: got %v", r.Kind)
	}
	if r.RestoredCheckpoint != nil {
		t.Error("RestoredCheckpoint should be nil")
	}
}

func TestSecurityResponse_ForRollback(t *testing.T) {
	cp, _ := circleai.NewSecurityCheckpoint("u", "CircleAI.Memory", []byte("state"))
	r := circleai.NewRollbackResponse("sig-3", cp)
	if r.Kind != circleai.SecurityResponseKindStateRollback {
		t.Errorf("kind: got %v", r.Kind)
	}
	if r.RestoredCheckpoint != cp {
		t.Error("RestoredCheckpoint should be the passed checkpoint")
	}
	if !strings.Contains(r.Description, cp.ID) || !strings.Contains(r.Description, "CircleAI.Memory") {
		t.Errorf("description should mention checkpoint id + module: %q", r.Description)
	}
}

func TestSecurityResponse_Composite(t *testing.T) {
	actions := []circleai.SecurityResponseKind{
		circleai.SecurityResponseKindKeyRotation,
		circleai.SecurityResponseKindMeshIsolationSignal,
	}
	r := circleai.NewCompositeResponse("sig-4", actions, "composite", nil)
	if r.Kind != circleai.SecurityResponseKindComposite {
		t.Errorf("kind: got %v", r.Kind)
	}
	if len(r.AppliedActions) != 2 {
		t.Fatalf("AppliedActions len: got %d", len(r.AppliedActions))
	}
	if r.AppliedActions[0] != circleai.SecurityResponseKindKeyRotation ||
		r.AppliedActions[1] != circleai.SecurityResponseKindMeshIsolationSignal {
		t.Errorf("AppliedActions content: %v", r.AppliedActions)
	}
}
