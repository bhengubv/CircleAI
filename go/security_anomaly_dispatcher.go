// security_anomaly_dispatcher.go
//
// Ports CircleAI.Security.IAnomalyEventDispatcher, AnomalyDispatchOutcome,
// AnomalyDispatchResult, and DefaultAnomalyEventDispatcher
// (IAnomalyEventDispatcher.cs).
//
// Safe-by-default composer around ISecurityWatchdog. The bare
// OnAnomalyDetected path requires the caller to verify (origin trust, schema,
// threshold gate) and dedupe (by id) themselves. The dispatcher folds
// verify -> dedup -> invoke into one call so a production consumer cannot
// accidentally accept an unverified or replayed signal. No exception is thrown
// on rejection — the caller branches on the outcome.

package circleai

import (
	"context"
	"errors"
	"sync"
)

// AnomalyDispatchOutcome is the outcome of a VerifyAndDispatch call. Ports
// AnomalyDispatchOutcome (ordinals match the C# enum).
type AnomalyDispatchOutcome int

const (
	// AnomalyDispatchOutcomeDispatched — signal accepted; watchdog was invoked.
	AnomalyDispatchOutcomeDispatched AnomalyDispatchOutcome = 0
	// AnomalyDispatchOutcomeDuplicate — signal id was already seen; deduped
	// silently.
	AnomalyDispatchOutcomeDuplicate AnomalyDispatchOutcome = 1
	// AnomalyDispatchOutcomeBelowThreshold — confidence was below the configured
	// threshold; ignored.
	AnomalyDispatchOutcomeBelowThreshold AnomalyDispatchOutcome = 2
	// AnomalyDispatchOutcomeUnverified — signal failed the origin/signature
	// verification step.
	AnomalyDispatchOutcomeUnverified AnomalyDispatchOutcome = 3
	// AnomalyDispatchOutcomeCancelled — cancellation tripped before dispatch.
	AnomalyDispatchOutcomeCancelled AnomalyDispatchOutcome = 4
)

// AnomalyDispatchResult is the result of a dispatch attempt. Ports the
// AnomalyDispatchResult record. Response is non-nil only when Outcome is
// Dispatched.
type AnomalyDispatchResult struct {
	// Outcome is what the dispatcher did with the signal.
	Outcome AnomalyDispatchOutcome
	// Response is the watchdog response when Outcome is Dispatched; nil
	// otherwise.
	Response *SecurityResponse
}

// IAnomalyEventDispatcher verifies, dedups, and dispatches an AnomalySignal in a
// single call. Ports IAnomalyEventDispatcher.
type IAnomalyEventDispatcher interface {
	// VerifyAndDispatch runs the verification pipeline (origin trust, optional
	// signature check, confidence threshold) and, when all gates pass, hands the
	// signal to the wrapped ISecurityWatchdog. checkpoint may be nil.
	VerifyAndDispatch(ctx context.Context, signal *AnomalySignal, checkpoint *SecurityCheckpoint) (AnomalyDispatchResult, error)
}

// DefaultAnomalyEventDispatcher is the default in-process dispatcher:
// threshold-gated, id-deduped, no signature verification (compose your own for
// untrusted transports). Ports DefaultAnomalyEventDispatcher.
type DefaultAnomalyEventDispatcher struct {
	watchdog          ISecurityWatchdog
	minimumConfidence float64

	mu   sync.Mutex
	seen map[string]struct{}
}

// NewDefaultAnomalyEventDispatcher creates the dispatcher over watchdog. Signals
// whose confidence is below minimumConfidence are dropped; the value is clamped
// to [0, 1]. Ports the C# constructor (default threshold 0.30 — use
// NewDefaultAnomalyEventDispatcherDefault for that default). Returns an error
// when watchdog is nil, mirroring the C# ArgumentNullException.
func NewDefaultAnomalyEventDispatcher(watchdog ISecurityWatchdog, minimumConfidence float64) (*DefaultAnomalyEventDispatcher, error) {
	if watchdog == nil {
		return nil, errors.New("watchdog must not be nil")
	}
	return &DefaultAnomalyEventDispatcher{
		watchdog:          watchdog,
		minimumConfidence: clampFloat(minimumConfidence, 0.0, 1.0),
		seen:              make(map[string]struct{}),
	}, nil
}

// NewDefaultAnomalyEventDispatcherDefault creates the dispatcher with the C#
// default minimum confidence of 0.30, matching the default watchdog rotation
// threshold so signals that would have been no-ops aren't even dispatched.
func NewDefaultAnomalyEventDispatcherDefault(watchdog ISecurityWatchdog) (*DefaultAnomalyEventDispatcher, error) {
	return NewDefaultAnomalyEventDispatcher(watchdog, 0.30)
}

// VerifyAndDispatch runs the gates then dispatches. Ports
// DefaultAnomalyEventDispatcher.VerifyAndDispatchAsync.
func (d *DefaultAnomalyEventDispatcher) VerifyAndDispatch(ctx context.Context, signal *AnomalySignal, checkpoint *SecurityCheckpoint) (AnomalyDispatchResult, error) {
	if signal == nil {
		return AnomalyDispatchResult{}, errors.New("signal must not be nil")
	}

	if ctx.Err() != nil {
		return AnomalyDispatchResult{Outcome: AnomalyDispatchOutcomeCancelled}, nil
	}

	if float64(signal.Confidence) < d.minimumConfidence {
		return AnomalyDispatchResult{Outcome: AnomalyDispatchOutcomeBelowThreshold}, nil
	}

	if !d.tryAddSeen(signal.ID) {
		return AnomalyDispatchResult{Outcome: AnomalyDispatchOutcomeDuplicate}, nil
	}

	response, err := d.watchdog.OnAnomalyDetected(ctx, signal, checkpoint)
	if err != nil {
		return AnomalyDispatchResult{}, err
	}
	return AnomalyDispatchResult{
		Outcome:  AnomalyDispatchOutcomeDispatched,
		Response: &response,
	}, nil
}

// tryAddSeen returns true if id was newly recorded, false if already present —
// mirrors ConcurrentDictionary.TryAdd.
func (d *DefaultAnomalyEventDispatcher) tryAddSeen(id string) bool {
	d.mu.Lock()
	defer d.mu.Unlock()
	if _, ok := d.seen[id]; ok {
		return false
	}
	d.seen[id] = struct{}{}
	return true
}

var _ IAnomalyEventDispatcher = (*DefaultAnomalyEventDispatcher)(nil)
