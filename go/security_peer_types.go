// security_peer_types.go
//
// Ports CircleAI.Security.PeerSecurityTypes (PeerSecurityTypes.cs):
//   Enums:   PeerSecurityEventKind, PeerThreatLevel, PeerDirectiveKind
//   Records: PeerSecurityEvent, PeerDirective, PeerTrustScoreUpdate,
//            PeerSecurityPosture, PeerNetworkHealthReport,
//            PeerThreatAssessment, PeerRoutingAdvice
//   Interfaces: IPeerDirectiveConsumer, IPeerSecurityLayer, IPeerIntelligence,
//               IPeerSecurityEventFeed
//
// Transport-agnostic security primitives — deliberately free of any transport
// dependency (Aether, WiFi, BLE, NearLink, HTTP). Every transport adapter
// translates its own event vocabulary into these types before feeding the
// security layer.
//
// Enum ordinals are int consts with stable ordinals matching the C# declaration
// order (PeerThreatLevel is explicitly numbered None=0..Critical=4 in C#).

package circleai

import (
	"context"
	"time"
)

// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

// PeerSecurityEventKind is the transport-neutral classification of a peer
// security event. Ports PeerSecurityEventKind.
type PeerSecurityEventKind int

const (
	// PeerSecurityEventKindAuthAttempt is an authentication attempt (login,
	// handshake, re-auth).
	PeerSecurityEventKindAuthAttempt PeerSecurityEventKind = 0
	// PeerSecurityEventKindRoutingAnomaly is anomalous routing behaviour
	// (loop, black-hole, etc.).
	PeerSecurityEventKindRoutingAnomaly PeerSecurityEventKind = 1
	// PeerSecurityEventKindBehaviourChange is an unexpected peer behaviour
	// change (rate, pattern, protocol).
	PeerSecurityEventKindBehaviourChange PeerSecurityEventKind = 2
	// PeerSecurityEventKindEncryptionEvent is an encryption negotiation event
	// (downgrade, cipher mismatch).
	PeerSecurityEventKindEncryptionEvent PeerSecurityEventKind = 3
	// PeerSecurityEventKindIntrusionSignal is an active intrusion probe or
	// exploitation attempt.
	PeerSecurityEventKindIntrusionSignal PeerSecurityEventKind = 4
	// PeerSecurityEventKindPrivilegeAttempt is a privilege escalation or
	// capability violation attempt.
	PeerSecurityEventKindPrivilegeAttempt PeerSecurityEventKind = 5
	// PeerSecurityEventKindConnectionAnomaly is an unusual connection pattern
	// (port scan, rapid reconnect).
	PeerSecurityEventKindConnectionAnomaly PeerSecurityEventKind = 6
	// PeerSecurityEventKindDataExfiltration is suspected data exfiltration
	// (volume, destination anomaly).
	PeerSecurityEventKindDataExfiltration PeerSecurityEventKind = 7
	// PeerSecurityEventKindDenialOfService is a denial-of-service signal
	// (flooding, resource exhaustion).
	PeerSecurityEventKindDenialOfService PeerSecurityEventKind = 8
	// PeerSecurityEventKindUnknown is the catch-all for events that do not map
	// to a specific category.
	PeerSecurityEventKindUnknown PeerSecurityEventKind = 9
)

// PeerThreatLevel is the severity level for a peer security event or threat
// assessment. Ports PeerThreatLevel; values are explicitly ordered None=0
// (safest) → Critical=4 (worst) to match the C# enum.
type PeerThreatLevel int

const (
	// PeerThreatLevelNone — no threat; event carries no security significance.
	PeerThreatLevelNone PeerThreatLevel = 0
	// PeerThreatLevelLow — low-level anomaly; monitor but no action required.
	PeerThreatLevelLow PeerThreatLevel = 1
	// PeerThreatLevelMedium — notable anomaly; elevated monitoring recommended.
	PeerThreatLevelMedium PeerThreatLevel = 2
	// PeerThreatLevelHigh — significant threat; routing around the peer
	// recommended.
	PeerThreatLevelHigh PeerThreatLevel = 3
	// PeerThreatLevelCritical — active or confirmed attack; quarantine the peer.
	PeerThreatLevelCritical PeerThreatLevel = 4
)

// PeerDirectiveKind is the action recommended by the security layer for a given
// peer. Ports PeerDirectiveKind.
type PeerDirectiveKind int

const (
	// PeerDirectiveKindElevateMonitoring — increase observation cadence; no
	// traffic restriction yet.
	PeerDirectiveKindElevateMonitoring PeerDirectiveKind = 0
	// PeerDirectiveKindAvoidNode — exclude the peer from routing; still accept
	// inbound connections.
	PeerDirectiveKindAvoidNode PeerDirectiveKind = 1
	// PeerDirectiveKindQuarantineNode — hard-block the peer; no traffic to or
	// from it.
	PeerDirectiveKindQuarantineNode PeerDirectiveKind = 2
	// PeerDirectiveKindReleaseNode — lift a previous directive; the peer has
	// recovered sufficient trust. Not issued automatically — requires explicit
	// operator action.
	PeerDirectiveKindReleaseNode PeerDirectiveKind = 3
)

// ---------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------

// PeerSecurityEvent is one security incident observed on any transport.
// Ports the PeerSecurityEvent record.
type PeerSecurityEvent struct {
	// NodeID is the stable identifier of the peer that generated the event.
	NodeID string
	// Kind is the transport-neutral event category.
	Kind PeerSecurityEventKind
	// ThreatLevel is the assessed severity at the time of observation.
	ThreatLevel PeerThreatLevel
	// Description is a human-readable description of the event.
	Description string
	// TransportID identifies the transport that produced the event
	// (e.g. "aether", "wifi", "ble", "nearlink", "http").
	TransportID string
	// OccurredAt is the UTC timestamp of the event.
	OccurredAt time.Time
}

// PeerDirective is a security directive issued to all registered
// IPeerDirectiveConsumer subscribers when a peer's trust crosses a threshold.
// Ports the PeerDirective record.
type PeerDirective struct {
	// Kind is the recommended action.
	Kind PeerDirectiveKind
	// TargetNodeID is the peer to which the directive applies.
	TargetNodeID string
	// TrustScore is the current trust score of the peer at time of issue.
	TrustScore float64
	// ThreatLevel is the threat level at time of issue.
	ThreatLevel PeerThreatLevel
	// Reason is a human-readable explanation for the directive.
	Reason string
	// Duration is an optional duration after which the directive should be
	// re-evaluated. nil means permanent until an explicit ReleaseNode directive
	// is issued.
	Duration *time.Duration
	// IssuedAt is the UTC timestamp of issue.
	IssuedAt time.Time
}

// PeerTrustScoreUpdate is the notification emitted by NodeTrustRegistry whenever
// a node's trust score changes. Ports the PeerTrustScoreUpdate record.
type PeerTrustScoreUpdate struct {
	// NodeID is the peer whose score changed.
	NodeID string
	// PreviousScore is the score before this change.
	PreviousScore float64
	// NewScore is the score after this change.
	NewScore float64
	// Reason is a short description of the cause (event description or
	// "passive-recovery").
	Reason string
	// ChangedAt is the UTC timestamp of the change.
	ChangedAt time.Time
}

// PeerSecurityPosture is a snapshot of the overall security posture across all
// observed peers. Ports the PeerSecurityPosture record.
type PeerSecurityPosture struct {
	// OverallThreatLevel is the worst-case threat level in the current peer set.
	OverallThreatLevel PeerThreatLevel
	// QuarantinedPeerCount is the number of peers at or below the quarantine
	// threshold.
	QuarantinedPeerCount int
	// MonitoredPeerCount is the number of peers elevated beyond the monitoring
	// threshold but not yet quarantined.
	MonitoredPeerCount int
	// IsActive is whether the security layer is currently running.
	IsActive bool
	// GeneratedAt is the UTC timestamp of this snapshot.
	GeneratedAt time.Time
}

// PeerNetworkHealthReport is the aggregate network health across all observed
// peers. Ports the PeerNetworkHealthReport record.
type PeerNetworkHealthReport struct {
	// OverallScore is the average trust score [0.0, 1.0] across all peers.
	OverallScore float64
	// TrustedPeerCount is the number of peers above the avoid-node threshold.
	TrustedPeerCount int
	// SuspiciousPeerCount is the number of peers at or below the elevate-
	// monitoring threshold.
	SuspiciousPeerCount int
	// Summary is a human-readable health summary.
	Summary string
	// GeneratedAt is the UTC timestamp of this report.
	GeneratedAt time.Time
}

// PeerThreatAssessment is a per-peer threat assessment: confidence score,
// threat level, and detected indicators. Ports the PeerThreatAssessment record.
type PeerThreatAssessment struct {
	// NodeID is the assessed peer.
	NodeID string
	// Confidence is the likelihood the peer is a genuine threat [0.0, 1.0],
	// derived from trust deficit + indicator count.
	Confidence float64
	// ThreatLevel is the classified severity.
	ThreatLevel PeerThreatLevel
	// Indicators are human-readable indicator tags (e.g. "brute-force-auth").
	Indicators []string
	// AssessedAt is the UTC timestamp of this assessment.
	AssessedAt time.Time
}

// PeerRoutingAdvice is a trust-aware routing recommendation for reaching a
// destination peer. Ports the PeerRoutingAdvice record.
type PeerRoutingAdvice struct {
	// DestinationNodeID is the target peer.
	DestinationNodeID string
	// RecommendedPath is the ordered list of peer IDs forming the recommended
	// path. Empty when no safe path is available.
	RecommendedPath []string
	// AvoidNodeIDs are peers that should be excluded from routing.
	AvoidNodeIDs []string
	// Confidence is the confidence in the recommendation [0.0, 1.0].
	Confidence float64
	// Reasoning is a human-readable explanation.
	Reasoning string
	// GeneratedAt is the UTC timestamp of this advice.
	GeneratedAt time.Time
}

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

// IPeerDirectiveConsumer receives security directives from any IPeerSecurityLayer
// implementation. Ports IPeerDirectiveConsumer.
type IPeerDirectiveConsumer interface {
	// OnDirective is called when the security layer issues a directive for a
	// peer.
	OnDirective(directive PeerDirective)
}

// IPeerSecurityLayer is the transport-agnostic security layer lifecycle and
// posture surface. Ports IPeerSecurityLayer.
//
// C# async surfaces map to Go idiom:
//   - Task StartAsync/StopAsync              -> Start/Stop(ctx) error
//   - void HandlePeerEvent                   -> HandlePeerEvent(e)
//   - IDisposable SubscribeToDirectives      -> SubscribeToDirectives returns
//     an unsubscribe func
//   - Task<PeerSecurityPosture> GetPosture   -> GetPosture(ctx)
type IPeerSecurityLayer interface {
	// Start starts the background trust-recovery loop.
	Start(ctx context.Context) error
	// Stop stops the recovery loop and releases resources.
	Stop(ctx context.Context) error
	// HandlePeerEvent feeds a security event from any transport into the
	// security layer. The layer degrades the peer's trust score and issues
	// directives as needed.
	HandlePeerEvent(e PeerSecurityEvent)
	// SubscribeToDirectives subscribes to receive directives. Call the returned
	// func to unsubscribe.
	SubscribeToDirectives(consumer IPeerDirectiveConsumer) (unsubscribe func())
	// GetPosture returns a snapshot of the current security posture.
	GetPosture(ctx context.Context) (PeerSecurityPosture, error)
}

// IPeerIntelligence exposes transport-agnostic intelligence queries over
// accumulated trust data. Ports IPeerIntelligence.
type IPeerIntelligence interface {
	// GetNetworkHealth returns aggregate network health across all observed
	// peers.
	GetNetworkHealth(ctx context.Context) (PeerNetworkHealthReport, error)
	// AssessThreat returns a threat assessment for a specific peer.
	AssessThreat(ctx context.Context, nodeID string) (PeerThreatAssessment, error)
	// GetRoutingAdvice returns trust-aware routing advice toward a destination
	// peer.
	GetRoutingAdvice(ctx context.Context, destinationNodeID string) (PeerRoutingAdvice, error)
	// StreamTrustScores streams every trust score change as it occurs. The
	// returned channel closes when ctx is cancelled. Mirrors the C#
	// IAsyncEnumerable<PeerTrustScoreUpdate>.
	StreamTrustScores(ctx context.Context) <-chan PeerTrustScoreUpdate
}

// IPeerSecurityEventFeed is implemented by transport adapters to register an
// event source with the security layer. The security layer calls Start once to
// begin pumping events. Ports IPeerSecurityEventFeed.
type IPeerSecurityEventFeed interface {
	// TransportID is a human-readable identifier for this transport
	// (e.g. "wifi", "ble", "aether").
	TransportID() string
	// Start begins feeding events into handler until ctx is cancelled.
	Start(ctx context.Context, handler func(PeerSecurityEvent)) error
}
