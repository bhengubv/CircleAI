// security_anomaly_dispatcher_test.go
//
// Verifies DefaultAnomalyEventDispatcher (ported from IAnomalyEventDispatcher.cs):
//   - Dispatched on first accept; Duplicate on a repeated id.
//   - BelowThreshold when confidence < minimum; Cancelled when ctx is done.
//   - nil watchdog rejected at construction.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestDispatcher_NilWatchdogRejected(t *testing.T) {
	if _, err := circleai.NewDefaultAnomalyEventDispatcher(nil, 0.3); err == nil {
		t.Error("nil watchdog should error")
	}
}

func TestDispatcher_DispatchesAndInvokesWatchdog(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	d, err := circleai.NewDefaultAnomalyEventDispatcherDefault(w)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	res, err := d.VerifyAndDispatch(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.9), nil)
	if err != nil {
		t.Fatalf("dispatch: %v", err)
	}
	if res.Outcome != circleai.AnomalyDispatchOutcomeDispatched {
		t.Fatalf("outcome: got %v, want Dispatched", res.Outcome)
	}
	if res.Response == nil {
		t.Fatal("Response should be non-nil on dispatch")
	}
	if res.Response.Kind != circleai.SecurityResponseKindComposite {
		t.Errorf("response kind: got %v", res.Response.Kind)
	}
}

func TestDispatcher_DeduplicatesById(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	d, _ := circleai.NewDefaultAnomalyEventDispatcherDefault(w)
	s := sig(circleai.ThreatVectorMemoryAnomaly, 0.9)

	first, _ := d.VerifyAndDispatch(context.Background(), s, nil)
	if first.Outcome != circleai.AnomalyDispatchOutcomeDispatched {
		t.Fatalf("first: got %v", first.Outcome)
	}
	second, _ := d.VerifyAndDispatch(context.Background(), s, nil)
	if second.Outcome != circleai.AnomalyDispatchOutcomeDuplicate {
		t.Errorf("second (same id): got %v, want Duplicate", second.Outcome)
	}
	if second.Response != nil {
		t.Error("duplicate should carry nil Response")
	}
}

func TestDispatcher_BelowThreshold(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	d, _ := circleai.NewDefaultAnomalyEventDispatcher(w, 0.5)
	res, _ := d.VerifyAndDispatch(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.4), nil)
	if res.Outcome != circleai.AnomalyDispatchOutcomeBelowThreshold {
		t.Errorf("outcome: got %v, want BelowThreshold", res.Outcome)
	}
}

func TestDispatcher_CancelledContext(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	d, _ := circleai.NewDefaultAnomalyEventDispatcherDefault(w)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	res, _ := d.VerifyAndDispatch(ctx, sig(circleai.ThreatVectorMemoryAnomaly, 0.9), nil)
	if res.Outcome != circleai.AnomalyDispatchOutcomeCancelled {
		t.Errorf("outcome: got %v, want Cancelled", res.Outcome)
	}
}

func TestDispatcher_ThresholdClampedIntoRange(t *testing.T) {
	// A minimum above 1.0 clamps to 1.0 — every ordinary signal is below it.
	w := circleai.NewDefaultSecurityWatchdog()
	d, _ := circleai.NewDefaultAnomalyEventDispatcher(w, 5.0)
	res, _ := d.VerifyAndDispatch(context.Background(), sig(circleai.ThreatVectorMemoryAnomaly, 0.99), nil)
	if res.Outcome != circleai.AnomalyDispatchOutcomeBelowThreshold {
		t.Errorf("clamped-to-1.0 threshold: got %v, want BelowThreshold", res.Outcome)
	}
}

func TestDispatcher_NilSignalErrors(t *testing.T) {
	w := circleai.NewDefaultSecurityWatchdog()
	d, _ := circleai.NewDefaultAnomalyEventDispatcherDefault(w)
	if _, err := d.VerifyAndDispatch(context.Background(), nil, nil); err == nil {
		t.Error("nil signal should error")
	}
}
