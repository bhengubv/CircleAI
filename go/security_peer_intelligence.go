// security_peer_intelligence.go
//
// Ports CircleAI.Security.PeerIntelligenceService (AetherIntelligenceService.cs)
// — the transport-agnostic intelligence output, a full implementation of
// IPeerIntelligence.
//
// Reads trust scores and event history from NodeTrustRegistry and packages them
// as the four intelligence outputs consumed by apps and the security layer:
//   PeerNetworkHealthReport   aggregate health (overall score, counts)
//   PeerThreatAssessment      per-peer confidence + level + indicators
//   PeerRoutingAdvice         trust-aware path with avoid-list
//   PeerTrustScoreUpdate      live channel of every score change
//
// StreamTrustScores delegates to the registry's unbounded update channel, so it
// carries the SAME competing-consumer semantics as the C# ReadAllAsync over the
// shared ChannelReader.

package circleai

import (
	"context"
	"fmt"
	"time"
)

// PeerIntelligenceService reads NodeTrustRegistry state to produce
// transport-agnostic intelligence outputs. Ports PeerIntelligenceService.
type PeerIntelligenceService struct {
	registry *NodeTrustRegistry
	options  *SecurityOptions
}

// NewPeerIntelligenceService constructs the service over its collaborators.
func NewPeerIntelligenceService(registry *NodeTrustRegistry, options *SecurityOptions) *PeerIntelligenceService {
	return &PeerIntelligenceService{registry: registry, options: options}
}

// GetNetworkHealth returns aggregate network health across all observed peers.
// Ports PeerIntelligenceService.GetNetworkHealthAsync.
func (s *PeerIntelligenceService) GetNetworkHealth(ctx context.Context) (PeerNetworkHealthReport, error) {
	nodeIDs := s.registry.AllNodeIDs()

	if len(nodeIDs) == 0 {
		return PeerNetworkHealthReport{
			OverallScore:        1.0,
			TrustedPeerCount:    0,
			SuspiciousPeerCount: 0,
			Summary:             "No peers observed.",
			GeneratedAt:         time.Now().UTC(),
		}, nil
	}

	var sum float64
	trusted := 0
	suspicious := 0
	for _, id := range nodeIDs {
		score := s.registry.GetTrustScore(id)
		sum += score
		if score > s.options.AvoidNodeThreshold {
			trusted++
		}
		if score <= s.options.ElevateMonitoringThreshold {
			suspicious++
		}
	}
	overall := sum / float64(len(nodeIDs))

	var summary string
	switch {
	case overall > 0.90:
		summary = "Network health is excellent."
	case overall > 0.75:
		summary = "Network health is good; minor anomalies detected."
	case overall > 0.50:
		summary = "Network health is degraded; elevated monitoring active."
	case overall > 0.25:
		summary = "Network health is poor; routing around compromised peers."
	default:
		summary = "Network health is critical; quarantine directives in effect."
	}

	return PeerNetworkHealthReport{
		OverallScore:        overall,
		TrustedPeerCount:    trusted,
		SuspiciousPeerCount: suspicious,
		Summary:             summary,
		GeneratedAt:         time.Now().UTC(),
	}, nil
}

// AssessThreat returns a threat assessment for a specific peer. Ports
// PeerIntelligenceService.AssessThreatAsync.
func (s *PeerIntelligenceService) AssessThreat(ctx context.Context, nodeID string) (PeerThreatAssessment, error) {
	score := s.registry.GetTrustScore(nodeID)
	deficit := 1.0 - score // 0 = fully trusted, 1 = fully lost

	indicators := DetectIndicators(s.registry.GetRecentEvents(nodeID), s.options.EventWindow)

	var level PeerThreatLevel
	switch {
	case score <= 0.25:
		level = PeerThreatLevelCritical
	case score <= 0.50:
		level = PeerThreatLevelHigh
	case score <= 0.75:
		level = PeerThreatLevelMedium
	case score <= 0.90:
		level = PeerThreatLevelLow
	default:
		level = PeerThreatLevelNone
	}

	// Confidence is proportional to trust deficit, boosted by each indicator.
	confidence := deficit + float64(len(indicators))*0.1
	if confidence > 1.0 {
		confidence = 1.0
	}

	return PeerThreatAssessment{
		NodeID:      nodeID,
		Confidence:  confidence,
		ThreatLevel: level,
		Indicators:  indicators,
		AssessedAt:  time.Now().UTC(),
	}, nil
}

// GetRoutingAdvice returns trust-aware routing advice toward a destination peer.
// Ports PeerIntelligenceService.GetRoutingAdviceAsync.
func (s *PeerIntelligenceService) GetRoutingAdvice(ctx context.Context, destinationNodeID string) (PeerRoutingAdvice, error) {
	allNodes := s.registry.AllNodeIDs()
	avoidNodes := make([]string, 0)
	for _, id := range allNodes {
		if s.registry.GetTrustScore(id) <= s.options.AvoidNodeThreshold {
			avoidNodes = append(avoidNodes, id)
		}
	}

	destScore := s.registry.GetTrustScore(destinationNodeID)

	// Recommended path is direct only when destination is above avoid-threshold.
	recommended := []string{}
	if destScore > s.options.AvoidNodeThreshold {
		recommended = []string{destinationNodeID}
	}

	var reasoning string
	switch {
	case destScore > 0.75:
		reasoning = fmt.Sprintf("Direct path to %s is trusted (score %s).", destinationNodeID, formatF2(destScore))
	case destScore > 0.50:
		reasoning = fmt.Sprintf("Destination %s is under monitoring; routing with caution.", destinationNodeID)
	case destScore > 0.25:
		reasoning = fmt.Sprintf("Destination %s has degraded trust; avoid recommended.", destinationNodeID)
	default:
		reasoning = fmt.Sprintf("Destination %s is quarantined; no safe path available.", destinationNodeID)
	}

	return PeerRoutingAdvice{
		DestinationNodeID: destinationNodeID,
		RecommendedPath:   recommended,
		AvoidNodeIDs:      avoidNodes,
		Confidence:        destScore,
		Reasoning:         reasoning,
		GeneratedAt:       time.Now().UTC(),
	}, nil
}

// StreamTrustScores streams every trust score change as it occurs, delegating to
// the registry's unbounded update channel. The returned channel closes when ctx
// is cancelled. Ports PeerIntelligenceService.StreamTrustScoresAsync.
func (s *PeerIntelligenceService) StreamTrustScores(ctx context.Context) <-chan PeerTrustScoreUpdate {
	return s.registry.TrustScoreUpdates(ctx)
}

// formatF2 renders a float with two fixed decimal places, matching .NET's ":F2"
// format (e.g. 0.9 → "0.90"). Used in routing-advice reasoning strings.
func formatF2(v float64) string {
	return fmt.Sprintf("%.2f", v)
}

var _ IPeerIntelligence = (*PeerIntelligenceService)(nil)
