// security_watchdog_test.go
//
// Verifies DefaultSecurityWatchdog (ported from ISecurityWatchdog.cs):
//   - Graduated response policy across the confidence bands.
//   - High-severity vectors add StateRollback only when a VERIFYING checkpoint
//     is supplied.
//   - StreamSignals delivers every dispatched signal, buffers signals emitted
//     BEFORE a reader attaches (unbounded semantics), and completes on cancel.

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func sig(vec circleai.ThreatVector, confidence float32) *circleai.AnomalySignal {
	return circleai.NewAnomalySignal(vec, confidence, "CircleAI.Test", "unit signal", nil)
}

func TestWatchdog_LowConfidenceNoAction(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	resp, err := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.10), nil)
	if err != nil {
		t.Fatalf("err: %v", err)
	}
	if resp.Kind != circleai.SecurityResponseKindNoAction {
		t.Errorf("kind: got %v, want NoAction", resp.Kind)
	}
	if !strings.Contains(resp.Description, "10%") {
		t.Errorf("description should show 10%%: %q", resp.Description)
	}
}

func TestWatchdog_MidConfidenceKeyRotation(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	resp, err := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.45), nil)
	if err != nil {
		t.Fatalf("err: %v", err)
	}
	if resp.Kind != circleai.SecurityResponseKindKeyRotation {
		t.Errorf("kind: got %v, want KeyRotation", resp.Kind)
	}
	if !strings.Contains(resp.Description, "MemoryAnomaly") || !strings.Contains(resp.Description, "45%") {
		t.Errorf("description mismatch: %q", resp.Description)
	}
}

func TestWatchdog_BoundaryAtRotationThreshold(t *testing.T) {
	// Exactly 0.30 is NOT < 0.30, so it should rotate, not no-action.
	w := circleai.NewDefaultSecurityWatchdog()
	resp, _ := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.30), nil)
	if resp.Kind != circleai.SecurityResponseKindKeyRotation {
		t.Errorf("0.30 should be KeyRotation (not < threshold), got %v", resp.Kind)
	}
}

func TestWatchdog_BoundaryBelowCompositeThreshold(t *testing.T) {
	// The composite gate is strict-greater (C#: confidence > 0.60). A value
	// clearly at or below the threshold stays KeyRotation. (Exactly 0.60 is a
	// float32 knife-edge: the Go port stores Confidence as float32 — matching the
	// existing schema — and float32(0.60) ≈ 0.60000002 which IS > 0.60, so the
	// exact-boundary result is representation-defined and not asserted here.)
	w := circleai.NewDefaultSecurityWatchdog()
	resp, _ := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.59), nil)
	if resp.Kind != circleai.SecurityResponseKindKeyRotation {
		t.Errorf("0.59 should stay KeyRotation (≤ composite threshold), got %v", resp.Kind)
	}
}

func TestWatchdog_JustAboveCompositeThreshold(t *testing.T) {
	// Just above 0.60 escalates to Composite.
	w := circleai.NewDefaultSecurityWatchdog()
	resp, _ := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.61), nil)
	if resp.Kind != circleai.SecurityResponseKindComposite {
		t.Errorf("0.61 should escalate to Composite (> threshold), got %v", resp.Kind)
	}
}

func TestWatchdog_HighConfidenceComposite(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	resp, _ := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.95), nil)
	if resp.Kind != circleai.SecurityResponseKindComposite {
		t.Fatalf("kind: got %v, want Composite", resp.Kind)
	}
	// Non-high-severity vector + no checkpoint → rotation + mesh only.
	if len(resp.AppliedActions) != 2 {
		t.Fatalf("expected 2 actions, got %v", resp.AppliedActions)
	}
	if resp.RestoredCheckpoint != nil {
		t.Error("no checkpoint supplied → RestoredCheckpoint must be nil")
	}
}

func TestWatchdog_HighSeverityWithVerifyingCheckpointAddsRollback(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	cp, _ := circleai.NewSecurityCheckpoint("u", "CircleAI.Memory", []byte("intact"))
	resp, _ := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorStateCorruption, 0.99), cp)
	if resp.Kind != circleai.SecurityResponseKindComposite {
		t.Fatalf("kind: got %v", resp.Kind)
	}
	if len(resp.AppliedActions) != 3 {
		t.Fatalf("expected 3 actions (rotation+mesh+rollback), got %v", resp.AppliedActions)
	}
	if resp.AppliedActions[2] != circleai.SecurityResponseKindStateRollback {
		t.Errorf("third action should be StateRollback, got %v", resp.AppliedActions[2])
	}
	if resp.RestoredCheckpoint != cp {
		t.Error("RestoredCheckpoint should be the supplied checkpoint")
	}
}

func TestWatchdog_HighSeverityWithTamperedCheckpointSkipsRollback(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	cp, _ := circleai.NewSecurityCheckpoint("u", "CircleAI.Memory", []byte("intact"))
	cp.Payload[0] ^= 0xFF // fails Verify()
	resp, _ := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorNetworkPivot, 0.99), cp)
	if len(resp.AppliedActions) != 2 {
		t.Fatalf("tampered checkpoint must NOT add rollback, got %v", resp.AppliedActions)
	}
	if resp.RestoredCheckpoint != nil {
		t.Error("tampered checkpoint → RestoredCheckpoint nil")
	}
}

func TestWatchdog_NonHighSeverityWithCheckpointSkipsRollback(t *testing.T) {
	// MemoryAnomaly is NOT in the high-severity set, so even a valid checkpoint
	// is not restored.
	w := circleai.NewDefaultSecurityWatchdog()
	cp, _ := circleai.NewSecurityCheckpoint("u", "CircleAI.Memory", []byte("intact"))
	resp, _ := w.OnAnomalyDetected(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.99), cp)
	if len(resp.AppliedActions) != 2 {
		t.Fatalf("non-high-severity must NOT add rollback, got %v", resp.AppliedActions)
	}
}

func TestWatchdog_NilSignalErrors(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	if _, err := w.OnAnomalyDetected(context.Background(), nil, nil); err == nil {
		t.Error("nil signal should error")
	}
}

func TestWatchdog_StreamDeliversSignals(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Subscribe SYNCHRONOUSLY before emitting, then read on a goroutine.
	stream := w.StreamSignals(ctx)

	const n = 5
	go func() {
		for i := 0; i < n; i++ {
			_, _ = w.OnAnomalyDetected(ctx, sig(circleai.ThreatVectorMemoryAnomaly, 0.5), nil)
		}
	}()

	got := 0
	deadline := time.After(2 * time.Second)
	for got < n {
		select {
		case _, ok := <-stream:
			if !ok {
				t.Fatalf("stream closed early after %d signals", got)
			}
			got++
		case <-deadline:
			t.Fatalf("timed out after %d/%d signals", got, n)
		}
	}
}

func TestWatchdog_StreamBuffersPreSubscriptionSignals(t *testing.T) {
	// Unbounded semantics: signals emitted BEFORE ReadAll must still be seen.
	w := circleai.NewDefaultSecurityWatchdog()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Emit two signals with NO active reader.
	_, _ = w.OnAnomalyDetected(ctx, sig(circleai.ThreatVectorMemoryAnomaly, 0.5), nil)
	_, _ = w.OnAnomalyDetected(ctx, sig(circleai.ThreatVectorControlFlowDrift, 0.5), nil)

	// Now attach a reader; both buffered signals must arrive.
	stream := w.StreamSignals(ctx)
	got := 0
	deadline := time.After(2 * time.Second)
	for got < 2 {
		select {
		case _, ok := <-stream:
			if !ok {
				t.Fatalf("stream closed after %d buffered signals", got)
			}
			got++
		case <-deadline:
			t.Fatalf("buffered signals lost: got %d/2", got)
		}
	}
}

func TestWatchdog_StreamCompletesOnCancel(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	ctx, cancel := context.WithCancel(context.Background())
	stream := w.StreamSignals(ctx)
	cancel()

	select {
	case _, ok := <-stream:
		if ok {
			// A late value is acceptable only if the channel then closes; drain.
			select {
			case _, ok2 := <-stream:
				if ok2 {
					t.Error("stream should close after cancel")
				}
			case <-time.After(time.Second):
				t.Error("stream did not close after cancel")
			}
		}
	case <-time.After(2 * time.Second):
		t.Error("stream did not close after cancel")
	}
}
