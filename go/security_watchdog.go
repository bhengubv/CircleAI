// security_watchdog.go
//
// Ports CircleAI.Security.ISecurityWatchdog + DefaultSecurityWatchdog
// (ISecurityWatchdog.cs).
//
// The central contract for the CircleAI local runtime immune system. Detection
// sites (companion pipeline, biometric verifier, agent patch gate) call
// OnAnomalyDetected when they observe something suspicious. The watchdog decides
// the response: key rotation, session revocation, mesh isolation, or state
// rollback.
//
// DefaultSecurityWatchdog is the out-of-box implementation. It broadcasts every
// signal on an UNBOUNDED channel (single-process correct; not multi-replica
// safe — matching the C# WireProven note) and applies a graduated response.
//
// The C# CircleAIComponentBase telemetry wrapper (RunOperationAsync /
// RunStreamAsync, metric counters) is host-side plumbing outside this package's
// portable contract; the port preserves the observable behaviour — signal
// broadcast + the exact graduated-response policy.

package circleai

import (
	"context"
	"errors"
	"fmt"
)

// ISecurityWatchdog is the central contract for the CircleAI local runtime
// immune system. Ports ISecurityWatchdog.
type ISecurityWatchdog interface {
	// OnAnomalyDetected is called by any detection site when a local runtime
	// anomaly is observed. The watchdog evaluates signal and applies the
	// appropriate protective response. checkpoint may be nil.
	OnAnomalyDetected(ctx context.Context, signal *AnomalySignal, checkpoint *SecurityCheckpoint) (SecurityResponse, error)
	// StreamSignals returns a live stream of every AnomalySignal observed since
	// the watchdog started. The channel closes when ctx is cancelled. Mirrors
	// the C# IAsyncEnumerable<AnomalySignal>.
	StreamSignals(ctx context.Context) <-chan AnomalySignal
}

const (
	watchdogRotationThreshold  = 0.30
	watchdogCompositeThreshold = 0.60
)

// DefaultSecurityWatchdog is the default in-process watchdog. It applies
// graduated responses based on ThreatVector and confidence level:
//   - confidence < 0.30                                → NoAction
//   - confidence 0.30–0.60                             → KeyRotation
//   - confidence > 0.60                                → Composite (rotation +
//     mesh signal); + StateRollback when a verifying checkpoint is available for
//     a high-severity vector.
//
// Ports DefaultSecurityWatchdog.
type DefaultSecurityWatchdog struct {
	signals *unboundedChannel[AnomalySignal]
}

// NewDefaultSecurityWatchdog constructs the default watchdog.
func NewDefaultSecurityWatchdog() *DefaultSecurityWatchdog {
	return &DefaultSecurityWatchdog{
		signals: newUnboundedChannel[AnomalySignal](),
	}
}

// ComponentName returns the component identifier, mirroring the C# property.
func (w *DefaultSecurityWatchdog) ComponentName() string { return "DefaultSecurityWatchdog" }

// OnAnomalyDetected evaluates signal and applies the graduated response policy.
// It first broadcasts the signal to any StreamSignals subscribers, then returns
// the response. Ports DefaultSecurityWatchdog.OnAnomalyDetectedAsync.
func (w *DefaultSecurityWatchdog) OnAnomalyDetected(ctx context.Context, signal *AnomalySignal, checkpoint *SecurityCheckpoint) (SecurityResponse, error) {
	if signal == nil {
		return SecurityResponse{}, errors.New("signal must not be nil")
	}
	if err := ctx.Err(); err != nil {
		return SecurityResponse{}, err
	}

	// Broadcast to any stream subscribers.
	w.signals.Write(*signal)

	confidence := float64(signal.Confidence)

	// ── Graduated response policy ──────────────────────────────────────────
	if confidence < watchdogRotationThreshold {
		return NewNoActionResponse(signal.ID,
			fmt.Sprintf("Confidence %s below rotation threshold — monitoring only.", formatPercent0(confidence))), nil
	}

	// High-severity vectors always warrant rollback if we have a checkpoint.
	isHighSeverity := signal.Vector == ThreatVectorControlFlowDrift ||
		signal.Vector == ThreatVectorPrivilegeEscalation ||
		signal.Vector == ThreatVectorNetworkPivot ||
		signal.Vector == ThreatVectorStateCorruption

	if confidence > watchdogCompositeThreshold {
		actions := []SecurityResponseKind{
			SecurityResponseKindKeyRotation,
			SecurityResponseKindMeshIsolationSignal,
		}

		var restored *SecurityCheckpoint
		if checkpoint != nil && isHighSeverity && checkpoint.Verify() {
			actions = append(actions, SecurityResponseKindStateRollback)
			restored = checkpoint
		}

		return NewCompositeResponse(signal.ID, actions,
			fmt.Sprintf("Composite response for %s (confidence %s) in %s.",
				threatVectorName(signal.Vector), formatPercent0(confidence), signal.AffectedModule),
			restored), nil
	}

	// Mid-range confidence: rotate keys only.
	return NewKeyRotationResponse(signal.ID,
		fmt.Sprintf("Key rotation triggered for %s (confidence %s) in %s.",
			threatVectorName(signal.Vector), formatPercent0(confidence), signal.AffectedModule)), nil
}

// StreamSignals returns the live signal stream. Ports
// DefaultSecurityWatchdog.StreamSignalsAsync.
func (w *DefaultSecurityWatchdog) StreamSignals(ctx context.Context) <-chan AnomalySignal {
	return w.signals.ReadAll(ctx)
}

// formatPercent0 renders a [0,1] ratio as a whole-number percentage, matching
// .NET's ":P0" format (e.g. 0.92 → "92%"). .NET rounds half away from zero.
func formatPercent0(ratio float64) string {
	pct := ratio * 100
	// Round half away from zero, as .NET's default MidpointRounding for P0.
	if pct >= 0 {
		pct = float64(int64(pct + 0.5))
	} else {
		pct = float64(int64(pct - 0.5))
	}
	return fmt.Sprintf("%d%%", int64(pct))
}

// threatVectorName returns the C# enum member name for a ThreatVector, used in
// human-readable response descriptions (mirrors signal.Vector.ToString()).
func threatVectorName(v ThreatVector) string {
	switch v {
	case ThreatVectorMemoryAnomaly:
		return "MemoryAnomaly"
	case ThreatVectorControlFlowDrift:
		return "ControlFlowDrift"
	case ThreatVectorPrivilegeEscalation:
		return "PrivilegeEscalation"
	case ThreatVectorBiometricSpoofAttempt:
		return "BiometricSpoofAttempt"
	case ThreatVectorNetworkPivot:
		return "NetworkPivot"
	case ThreatVectorStateCorruption:
		return "StateCorruption"
	case ThreatVectorAgentPatchRejected:
		return "AgentPatchRejected"
	default:
		return "Unknown"
	}
}

var _ ISecurityWatchdog = (*DefaultSecurityWatchdog)(nil)
