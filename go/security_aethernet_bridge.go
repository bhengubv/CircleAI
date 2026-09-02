// security_aethernet_bridge.go
//
// Ports two CircleAI.Security.AetherNet adapters that connect the Aether
// contracts to the transport-agnostic CircleAI.Security layer:
//
//   AetherSecurityBridge.cs      -> AetherSecurityBridge (implements IAISecurityLayer,
//                                   wraps *SecurityLayerService)
//   AetherIntelligenceAdapter.cs -> AetherIntelligenceAdapter (implements
//                                   IAetherIntelligence, wraps *PeerIntelligenceService)
//
// Both classes are PURE TRANSLATION — the SecurityLayerService /
// PeerIntelligenceService do all the reasoning. These adapters map Aether
// vocabulary ↔ Peer vocabulary at the boundary.

package circleai

import (
	"context"
	"sync"
)

// ─────────────────────────────────────────────────────────────────────────────
// AetherSecurityBridge (AetherSecurityBridge.cs)
// ─────────────────────────────────────────────────────────────────────────────

// AetherSecurityBridge connects an Aether mesh telemetry feed to the
// transport-agnostic SecurityLayerService. Implements IAISecurityLayer so it can
// be used as a drop-in replacement for the old Aether-coupled layer. Ports
// AetherSecurityBridge.
type AetherSecurityBridge struct {
	layer *SecurityLayerService

	mu                   sync.Mutex
	telemetryUnsubscribe func()
}

// NewAetherSecurityBridge initialises the bridge over an existing
// transport-agnostic security layer. The layer must be constructed but need not
// be started. Panics if layer is nil (mirrors ArgumentNullException).
func NewAetherSecurityBridge(layer *SecurityLayerService) *AetherSecurityBridge {
	if layer == nil {
		panic("layer must not be nil")
	}
	return &AetherSecurityBridge{layer: layer}
}

// Start subscribes to telemetry and starts the underlying SecurityLayerService
// recovery loop. Ports StartAsync.
//
// Concurrency: the telemetry subscription is registered SYNCHRONOUSLY here,
// before the layer's background loop is launched, so no security event published
// immediately after Start can race ahead of the observer being attached.
func (b *AetherSecurityBridge) Start(ctx context.Context, telemetry IAetherTelemetry) error {
	if telemetry == nil {
		panic("telemetry must not be nil")
	}
	unsub := telemetry.Subscribe(&aetherBridgeObserver{bridge: b})

	b.mu.Lock()
	// If Start is called twice, detach the prior subscription first.
	if b.telemetryUnsubscribe != nil {
		prev := b.telemetryUnsubscribe
		b.telemetryUnsubscribe = unsub
		b.mu.Unlock()
		prev()
	} else {
		b.telemetryUnsubscribe = unsub
		b.mu.Unlock()
	}

	return b.layer.Start(ctx)
}

// Stop detaches the telemetry subscription and stops the underlying layer. Ports
// StopAsync.
func (b *AetherSecurityBridge) Stop(ctx context.Context) error {
	b.mu.Lock()
	unsub := b.telemetryUnsubscribe
	b.telemetryUnsubscribe = nil
	b.mu.Unlock()

	if unsub != nil {
		unsub()
	}
	return b.layer.Stop(ctx)
}

// SubscribeToDirectives wraps consumer in an adapter that translates PeerDirective
// → SecurityDirective before forwarding to the Aether consumer, then subscribes
// it to the underlying layer. Ports SubscribeToDirectives.
func (b *AetherSecurityBridge) SubscribeToDirectives(consumer ISecurityDirectiveConsumer) (unsubscribe func()) {
	if consumer == nil {
		panic("consumer must not be nil")
	}
	return b.layer.SubscribeToDirectives(&aetherDirectiveAdapter{consumer: consumer})
}

// GetPosture returns the current posture, mapping PeerSecurityPosture →
// SecurityPosture. Ports GetPostureAsync.
func (b *AetherSecurityBridge) GetPosture(ctx context.Context) (SecurityPosture, error) {
	posture, err := b.layer.GetPosture(ctx)
	if err != nil {
		return SecurityPosture{}, err
	}
	return SecurityPosture{
		OverallThreatLevel:   aetherMapToAetherThreatLevel(posture.OverallThreatLevel),
		QuarantinedNodeCount: posture.QuarantinedPeerCount,
		MonitoredNodeCount:   posture.MonitoredPeerCount,
		IsActive:             posture.IsActive,
		AssessedAt:           posture.GeneratedAt,
	}, nil
}

var _ IAISecurityLayer = (*AetherSecurityBridge)(nil)

// aetherBridgeObserver translates Aether telemetry events into Peer events and
// feeds them to the security layer. Ports AetherSecurityBridge.Observer.
type aetherBridgeObserver struct {
	bridge *AetherSecurityBridge
}

// OnSecurityEvent translates an Aether security event → PeerSecurityEvent →
// security layer. Ports Observer.OnSecurityEvent.
func (o *aetherBridgeObserver) OnSecurityEvent(e AetherSecurityEvent) {
	peer := PeerSecurityEvent{
		NodeID:      e.NodeID,
		Kind:        aetherMapToPeerEventKind(e.Kind),
		ThreatLevel: aetherMapToPeerThreatLevel(e.ThreatLevel),
		Description: e.Description,
		TransportID: "aether",
		OccurredAt:  e.OccurredAt,
	}
	o.bridge.layer.HandlePeerEvent(peer)
}

// OnNodeEvent notifies the layer when a node leaves. Ports Observer.OnNodeEvent.
func (o *aetherBridgeObserver) OnNodeEvent(e AetherNodeEvent) {
	if e.IsExit() {
		o.bridge.layer.HandlePeerLeft(e.NodeID)
	}
}

// OnTransportEvent is not relevant to security scoring — ignored.
func (o *aetherBridgeObserver) OnTransportEvent(_ AetherTransportEvent) {}

// OnRouteEvent is not relevant to security scoring — ignored.
func (o *aetherBridgeObserver) OnRouteEvent(_ AetherRouteEvent) {}

// OnNetworkEvent is not relevant to security scoring — ignored.
func (o *aetherBridgeObserver) OnNetworkEvent(_ AetherNetworkEvent) {}

var _ IAetherTelemetryObserver = (*aetherBridgeObserver)(nil)

// aetherDirectiveAdapter adapts an Aether ISecurityDirectiveConsumer so it can
// receive PeerDirective instances from the transport-agnostic layer, translating
// them back to SecurityDirective before delivery. Ports
// AetherSecurityBridge.DirectiveAdapter.
type aetherDirectiveAdapter struct {
	consumer ISecurityDirectiveConsumer
}

// OnDirective translates PeerDirective → SecurityDirective and forwards it. The
// Peer TrustScore (a plain float) becomes the pointer-valued TrustScoreOverride
// on the Aether side, and TargetNodeID becomes an optional pointer. Ports
// DirectiveAdapter.OnDirective.
func (a *aetherDirectiveAdapter) OnDirective(directive PeerDirective) {
	// The C# record maps directive.TargetNodeId (a string) straight into the
	// nullable TargetNodeId, and directive.TrustScore (a double) into the
	// nullable TrustScoreOverride. Preserve that: both become non-nil pointers.
	target := directive.TargetNodeID
	trust := directive.TrustScore
	aether := SecurityDirective{
		Kind:               aetherMapToSecurityDirectiveKind(directive.Kind),
		TargetNodeID:       &target,
		TrustScoreOverride: &trust,
		ThreatLevel:        aetherMapToAetherThreatLevel(directive.ThreatLevel),
		Reason:             directive.Reason,
		Duration:           directive.Duration,
		IssuedAt:           directive.IssuedAt,
	}
	a.consumer.OnDirective(aether)
}

var _ IPeerDirectiveConsumer = (*aetherDirectiveAdapter)(nil)

// ─────────────────────────────────────────────────────────────────────────────
// AetherIntelligenceAdapter (AetherIntelligenceAdapter.cs)
// ─────────────────────────────────────────────────────────────────────────────

// AetherIntelligenceAdapter implements IAetherIntelligence by wrapping
// PeerIntelligenceService and mapping transport-agnostic result types to their
// Aether equivalents. Ports AetherIntelligenceAdapter.
type AetherIntelligenceAdapter struct {
	inner *PeerIntelligenceService
}

// NewAetherIntelligenceAdapter constructs the adapter. Panics if inner is nil
// (mirrors ArgumentNullException).
func NewAetherIntelligenceAdapter(inner *PeerIntelligenceService) *AetherIntelligenceAdapter {
	if inner == nil {
		panic("inner must not be nil")
	}
	return &AetherIntelligenceAdapter{inner: inner}
}

// GetNetworkHealth maps PeerNetworkHealthReport → NetworkHealthReport. Ports
// GetNetworkHealthAsync.
func (a *AetherIntelligenceAdapter) GetNetworkHealth(ctx context.Context) (NetworkHealthReport, error) {
	r, err := a.inner.GetNetworkHealth(ctx)
	if err != nil {
		return NetworkHealthReport{}, err
	}
	return NetworkHealthReport{
		OverallScore:        r.OverallScore,
		TrustedNodeCount:    r.TrustedPeerCount,
		SuspiciousNodeCount: r.SuspiciousPeerCount,
		Summary:             r.Summary,
		GeneratedAt:         r.GeneratedAt,
	}, nil
}

// AssessThreat maps PeerThreatAssessment → ThreatAssessment. Ports
// AssessThreatAsync.
func (a *AetherIntelligenceAdapter) AssessThreat(ctx context.Context, nodeID string) (ThreatAssessment, error) {
	t, err := a.inner.AssessThreat(ctx, nodeID)
	if err != nil {
		return ThreatAssessment{}, err
	}
	return ThreatAssessment{
		NodeID:           t.NodeID,
		ThreatConfidence: t.Confidence,
		Level:            aetherMapToAetherThreatLevel(t.ThreatLevel),
		Indicators:       t.Indicators,
		AssessedAt:       t.AssessedAt,
	}, nil
}

// GetRoutingAdvice maps PeerRoutingAdvice → RoutingAdvice. Ports
// GetRoutingAdviceAsync.
func (a *AetherIntelligenceAdapter) GetRoutingAdvice(ctx context.Context, destinationNodeID string) (RoutingAdvice, error) {
	r, err := a.inner.GetRoutingAdvice(ctx, destinationNodeID)
	if err != nil {
		return RoutingAdvice{}, err
	}
	return RoutingAdvice{
		DestinationNodeID: r.DestinationNodeID,
		RecommendedPath:   r.RecommendedPath,
		AvoidNodes:        r.AvoidNodeIDs,
		Confidence:        r.Confidence,
		Reasoning:         r.Reasoning,
		GeneratedAt:       r.GeneratedAt,
	}, nil
}

// StreamTrustScores maps each PeerTrustScoreUpdate → TrustScoreUpdate as it
// streams. Ports StreamTrustScoresAsync.
//
// Concurrency: the inner channel is obtained SYNCHRONOUSLY before the mapping
// goroutine is spawned, so the subscription to the underlying registry channel
// is established before this call returns — no update published right after the
// call can be lost. The out channel closes when the inner channel closes (ctx
// cancellation) so the consumer terminates cleanly.
func (a *AetherIntelligenceAdapter) StreamTrustScores(ctx context.Context) <-chan TrustScoreUpdate {
	inner := a.inner.StreamTrustScores(ctx) // subscribe synchronously
	out := make(chan TrustScoreUpdate)
	go func() {
		defer close(out)
		for u := range inner {
			mapped := TrustScoreUpdate{
				NodeID:        u.NodeID,
				PreviousScore: u.PreviousScore,
				CurrentScore:  u.NewScore,
				Reason:        u.Reason,
				UpdatedAt:     u.ChangedAt,
			}
			select {
			case out <- mapped:
			case <-ctx.Done():
				return
			}
		}
	}()
	return out
}

var _ IAetherIntelligence = (*AetherIntelligenceAdapter)(nil)
