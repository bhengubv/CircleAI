// security_layer_service.go
//
// Ports CircleAI.Security.SecurityLayerService (AISecurityLayerService.cs) —
// the transport-agnostic AI Security Layer, a full implementation of
// IPeerSecurityLayer.
//
// Lifecycle:
//   Start  → launches the background trust-recovery loop.
//   (run)  → security events arrive via HandlePeerEvent(PeerSecurityEvent). Each
//            event degrades the peer's trust score; threshold evaluation decides
//            which PeerDirective (if any) to issue.
//   Stop   → cancels the recovery loop and waits for it to exit.
//
// Directives issued (most-severe wins per event):
//   QuarantineNode     trust ≤ QuarantineThreshold
//   AvoidNode          trust ≤ AvoidNodeThreshold
//   ElevateMonitoring  trust ≤ ElevateMonitoringThreshold
//   ReleaseNode        not issued automatically — requires operator action
//
// Concurrency: Start/Stop are guarded by a mutex; the recovery loop runs on a
// goroutine cancelled via context and joined through a done channel (mirrors
// the C# CancellationTokenSource + Task.WaitAsync join).

package circleai

import (
	"context"
	"sync"
	"time"
)

// SecurityLayerService degrades per-peer trust scores via ComputeDegradation and
// issues PeerDirective recommendations to all registered IPeerDirectiveConsumer
// subscribers. Ports SecurityLayerService.
type SecurityLayerService struct {
	registry  *NodeTrustRegistry
	options   *SecurityOptions
	publisher *DirectivePublisher

	// recoveryInterval is the passive-recovery cadence. Defaults to 30s to match
	// the C# reference; overridable for tests via WithRecoveryInterval.
	recoveryInterval time.Duration

	mu       sync.Mutex
	cancel   context.CancelFunc
	loopDone chan struct{}
	active   bool
}

// NewSecurityLayerService constructs the layer over its collaborators.
func NewSecurityLayerService(registry *NodeTrustRegistry, options *SecurityOptions, publisher *DirectivePublisher) *SecurityLayerService {
	return &SecurityLayerService{
		registry:         registry,
		options:          options,
		publisher:        publisher,
		recoveryInterval: 30 * time.Second,
	}
}

// WithRecoveryInterval overrides the passive-recovery cadence (default 30s).
// Must be called before Start. Returns the receiver for chaining. This is a Go
// affordance so tests can exercise the recovery loop without a 30s wait; the
// C# reference hard-codes 30s.
func (s *SecurityLayerService) WithRecoveryInterval(d time.Duration) *SecurityLayerService {
	if d > 0 {
		s.recoveryInterval = d
	}
	return s
}

// Start launches the background trust-recovery loop. Idempotent when already
// active. Ports SecurityLayerService.StartAsync.
func (s *SecurityLayerService) Start(ctx context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.active {
		return nil
	}
	loopCtx, cancel := context.WithCancel(ctx)
	s.cancel = cancel
	s.loopDone = make(chan struct{})
	s.active = true

	interval := s.recoveryInterval
	done := s.loopDone
	go s.runRecoveryLoop(loopCtx, interval, done)
	return nil
}

// Stop cancels the recovery loop and waits for it to exit (or ctx to cancel).
// Ports SecurityLayerService.StopAsync.
func (s *SecurityLayerService) Stop(ctx context.Context) error {
	s.mu.Lock()
	s.active = false
	cancel := s.cancel
	done := s.loopDone
	s.cancel = nil
	s.loopDone = nil
	s.mu.Unlock()

	if cancel != nil {
		cancel()
	}
	if done != nil {
		select {
		case <-done:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	return nil
}

// HandlePeerEvent feeds a security event from any transport into the layer.
// Call from a transport adapter after translating its native event type to
// PeerSecurityEvent. Thread-safe. Ports SecurityLayerService.HandlePeerEvent.
func (s *SecurityLayerService) HandlePeerEvent(e PeerSecurityEvent) {
	degradation := ComputeDegradation(e)
	if degradation <= 0 {
		return // PeerThreatLevelNone — no trust impact
	}
	previous, current := s.registry.ApplyDegradation(e, degradation)
	s.evaluateThresholds(e.NodeID, previous, current, e.Description)
}

// HandlePeerLeft notifies the layer that a peer has left. The trust entry is
// preserved for historical queries; no directive is issued. Ports
// SecurityLayerService.HandlePeerLeft.
func (s *SecurityLayerService) HandlePeerLeft(nodeID string) {
	// Trust entry retained for forensic queries; no action required on departure.
	_ = nodeID
}

// SubscribeToDirectives subscribes consumer to directive notifications and
// returns an unsubscribe func. Ports SecurityLayerService.SubscribeToDirectives.
func (s *SecurityLayerService) SubscribeToDirectives(consumer IPeerDirectiveConsumer) (unsubscribe func()) {
	return s.publisher.Subscribe(consumer)
}

// GetPosture returns a snapshot of the current security posture. Ports
// SecurityLayerService.GetPostureAsync.
func (s *SecurityLayerService) GetPosture(ctx context.Context) (PeerSecurityPosture, error) {
	nodeIDs := s.registry.AllNodeIDs()

	quarantined := 0
	monitored := 0
	worstScore := 1.0
	for i, id := range nodeIDs {
		score := s.registry.GetTrustScore(id)
		if score <= s.options.QuarantineThreshold {
			quarantined++
		}
		if score <= s.options.ElevateMonitoringThreshold && score > s.options.QuarantineThreshold {
			monitored++
		}
		if i == 0 || score < worstScore {
			worstScore = score
		}
	}
	if len(nodeIDs) == 0 {
		worstScore = 1.0
	}

	s.mu.Lock()
	active := s.active
	s.mu.Unlock()

	return PeerSecurityPosture{
		OverallThreatLevel:   scoreToThreatLevel(worstScore),
		QuarantinedPeerCount: quarantined,
		MonitoredPeerCount:   monitored,
		IsActive:             active,
		GeneratedAt:          time.Now().UTC(),
	}, nil
}

// ─── Threshold evaluation ──────────────────────────────────────────────────

// evaluateThresholds issues at most one directive per event, from most-severe
// to least. Ports SecurityLayerService.EvaluateThresholds.
func (s *SecurityLayerService) evaluateThresholds(nodeID string, previous, current float64, reason string) {
	if previous > s.options.QuarantineThreshold && current <= s.options.QuarantineThreshold {
		s.issueDirective(PeerDirectiveKindQuarantineNode, nodeID, current, reason, PeerThreatLevelCritical)
		return
	}
	if previous > s.options.AvoidNodeThreshold && current <= s.options.AvoidNodeThreshold {
		s.issueDirective(PeerDirectiveKindAvoidNode, nodeID, current, reason, PeerThreatLevelHigh)
		return
	}
	if previous > s.options.ElevateMonitoringThreshold && current <= s.options.ElevateMonitoringThreshold {
		s.issueDirective(PeerDirectiveKindElevateMonitoring, nodeID, current, reason, PeerThreatLevelMedium)
	}
}

func (s *SecurityLayerService) issueDirective(kind PeerDirectiveKind, nodeID string, trustScore float64, reason string, threatLevel PeerThreatLevel) {
	s.publisher.Publish(PeerDirective{
		Kind:         kind,
		TargetNodeID: nodeID,
		TrustScore:   trustScore,
		ThreatLevel:  threatLevel,
		Reason:       reason,
		Duration:     nil, // permanent until ReleaseNode
		IssuedAt:     time.Now().UTC(),
	})
}

// ─── Background recovery loop ──────────────────────────────────────────────

func (s *SecurityLayerService) runRecoveryLoop(ctx context.Context, interval time.Duration, done chan struct{}) {
	defer close(done)
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			s.registry.ApplyRecovery(interval)
		}
	}
}

// ─── Helpers ────────────────────────────────────────────────────────────────

// scoreToThreatLevel maps a trust score to a PeerThreatLevel. Ports
// SecurityLayerService.ScoreToThreatLevel (same boundaries as the C# switch).
func scoreToThreatLevel(score float64) PeerThreatLevel {
	switch {
	case score <= 0.25:
		return PeerThreatLevelCritical
	case score <= 0.50:
		return PeerThreatLevelHigh
	case score <= 0.75:
		return PeerThreatLevelMedium
	case score <= 0.90:
		return PeerThreatLevelLow
	default:
		return PeerThreatLevelNone
	}
}

var _ IPeerSecurityLayer = (*SecurityLayerService)(nil)
