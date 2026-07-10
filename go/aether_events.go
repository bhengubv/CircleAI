// aether_events.go
//
// Ports CircleAI.Aether telemetry vocabulary (the Events/*.cs files +
// IAetherTelemetry.cs) — the outward-facing, AI-free surface Aether publishes
// and BhenguAI subscribes to. Aether never calls into BhenguAI; the boundary is
// strictly one-way.
//
// C# types ported here:
//   Events/AetherNodeEvent.cs     -> AetherNodeEventKind, AetherNodeHealth, AetherNodeEvent
//   Events/AetherTransportEvent.cs-> AetherTransportKind, AetherTransportEventKind, AetherTransportEvent
//   Events/AetherRouteEvent.cs    -> AetherRouteEventKind, AetherRouteEvent
//   Events/AetherSecurityEvent.cs -> AetherSecurityEventKind, AetherThreatLevel, AetherSecurityEvent
//   Events/AetherNetworkEvent.cs  -> AetherNetworkEventKind, AetherNetworkEvent
//   IAetherTelemetry.cs           -> IAetherTelemetryObserver, IAetherTelemetry,
//                                    NullAetherTelemetry (+ InMemoryAetherTelemetry,
//                                    a working thread-safe publisher used as the
//                                    non-null in-memory implementation)
//
// Enum ordinals are int consts with stable ordinals matching C# declaration order.

package circleai

import (
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// Node events (AetherNodeEvent.cs)
// ---------------------------------------------------------------------------

// AetherNodeEventKind enumerates node lifecycle transitions Aether can emit.
// Ports AetherNodeEventKind.
type AetherNodeEventKind int

const (
	// AetherNodeEventKindJoined — a node joined the mesh.
	AetherNodeEventKindJoined AetherNodeEventKind = 0
	// AetherNodeEventKindLeft — a node left the mesh.
	AetherNodeEventKindLeft AetherNodeEventKind = 1
	// AetherNodeEventKindHealthChanged — a node's health snapshot changed.
	AetherNodeEventKindHealthChanged AetherNodeEventKind = 2
)

// AetherNodeHealth is a point-in-time health snapshot for a single mesh node.
// Ports the AetherNodeHealth record.
type AetherNodeHealth struct {
	// TrustScore is 0.0 (untrusted) to 1.0 (fully trusted). Maintained by the AI
	// Security Layer when active; defaults to 1.0 when security layer is off.
	TrustScore float64
	// IsReachable is whether the node is currently reachable.
	IsReachable bool
	// Latency is the observed round-trip latency to the node.
	Latency time.Duration
	// HopCount is the number of hops to reach the node.
	HopCount int
}

// IsValid returns true when TrustScore is within the valid 0–1 range. Ports
// AetherNodeHealth.IsValid.
func (h AetherNodeHealth) IsValid() bool {
	return h.TrustScore >= 0.0 && h.TrustScore <= 1.0
}

// AetherNodeEvent is emitted whenever a node joins, leaves, or changes health.
// Ports the AetherNodeEvent record.
type AetherNodeEvent struct {
	// NodeID identifies the node.
	NodeID string
	// Kind is the lifecycle transition.
	Kind AetherNodeEventKind
	// Health is the node's health snapshot at event time.
	Health AetherNodeHealth
	// OccurredAt is the UTC timestamp of the event.
	OccurredAt time.Time
}

// IsExit returns true when this is a departure event. Ports AetherNodeEvent.IsExit.
func (e AetherNodeEvent) IsExit() bool { return e.Kind == AetherNodeEventKindLeft }

// ---------------------------------------------------------------------------
// Transport events (AetherTransportEvent.cs)
// ---------------------------------------------------------------------------

// AetherTransportKind is the physical or logical transport medium Aether uses.
// Ports AetherTransportKind.
type AetherTransportKind int

const (
	// AetherTransportKindWiFi — Wi-Fi transport.
	AetherTransportKindWiFi AetherTransportKind = 0
	// AetherTransportKindBluetooth — Bluetooth transport.
	AetherTransportKindBluetooth AetherTransportKind = 1
	// AetherTransportKindLoRa — LoRa transport.
	AetherTransportKindLoRa AetherTransportKind = 2
	// AetherTransportKindNFC — NFC transport.
	AetherTransportKindNFC AetherTransportKind = 3
	// AetherTransportKindCellular — cellular transport.
	AetherTransportKindCellular AetherTransportKind = 4
	// AetherTransportKindEthernet — wired Ethernet transport.
	AetherTransportKindEthernet AetherTransportKind = 5
	// AetherTransportKindUnknown — transport medium is unknown.
	AetherTransportKindUnknown AetherTransportKind = 6
)

// AetherTransportEventKind enumerates transport-layer observations. Ports
// AetherTransportEventKind.
type AetherTransportEventKind int

const (
	// AetherTransportEventKindSelected — a transport was selected.
	AetherTransportEventKindSelected AetherTransportEventKind = 0
	// AetherTransportEventKindChanged — the active transport changed.
	AetherTransportEventKindChanged AetherTransportEventKind = 1
	// AetherTransportEventKindLatencyMeasured — latency was measured.
	AetherTransportEventKindLatencyMeasured AetherTransportEventKind = 2
	// AetherTransportEventKindPacketLoss — packet loss was observed.
	AetherTransportEventKindPacketLoss AetherTransportEventKind = 3
)

// AetherTransportEvent is emitted when Aether selects, changes, or measures
// quality on a transport channel. Ports the AetherTransportEvent record.
//
// Latency and PacketLossRate are optional (C# nullable) and modelled as
// pointers; nil means "not set".
type AetherTransportEvent struct {
	// NodeID identifies the node the transport reaches.
	NodeID string
	// Kind is the transport observation kind.
	Kind AetherTransportEventKind
	// Transport is the medium in use.
	Transport AetherTransportKind
	// Latency is the measured latency, or nil when not set.
	Latency *time.Duration
	// PacketLossRate is the observed loss rate [0,1], or nil when not set.
	PacketLossRate *float64
	// OccurredAt is the UTC timestamp of the event.
	OccurredAt time.Time
}

// ExceedsLoss returns true when PacketLossRate is set and exceeds the given
// threshold (0.0–1.0). Ports AetherTransportEvent.ExceedsLoss.
func (e AetherTransportEvent) ExceedsLoss(threshold float64) bool {
	return e.PacketLossRate != nil && *e.PacketLossRate > threshold
}

// ---------------------------------------------------------------------------
// Route events (AetherRouteEvent.cs)
// ---------------------------------------------------------------------------

// AetherRouteEventKind enumerates routing changes. Ports AetherRouteEventKind.
type AetherRouteEventKind int

const (
	// AetherRouteEventKindDiscovered — a route was discovered.
	AetherRouteEventKindDiscovered AetherRouteEventKind = 0
	// AetherRouteEventKindChanged — a route changed.
	AetherRouteEventKindChanged AetherRouteEventKind = 1
	// AetherRouteEventKindFailed — a route failed.
	AetherRouteEventKindFailed AetherRouteEventKind = 2
)

// AetherRouteEvent is emitted when Aether discovers, updates, or loses a route
// between two nodes. Ports the AetherRouteEvent record.
type AetherRouteEvent struct {
	// SourceNodeID is the route's origin node.
	SourceNodeID string
	// DestinationNodeID is the route's destination node.
	DestinationNodeID string
	// Path is the ordered sequence of node IDs traversed.
	Path []string
	// Kind is the routing change kind.
	Kind AetherRouteEventKind
	// FailureReason carries the reason on a failure event, else nil.
	FailureReason *string
	// OccurredAt is the UTC timestamp of the event.
	OccurredAt time.Time
}

// HopCount returns the number of hops in this route, including source and
// destination. Ports AetherRouteEvent.HopCount.
func (e AetherRouteEvent) HopCount() int { return len(e.Path) }

// IsFailed returns true when this event represents a routing failure. Ports
// AetherRouteEvent.IsFailed.
func (e AetherRouteEvent) IsFailed() bool { return e.Kind == AetherRouteEventKindFailed }

// ---------------------------------------------------------------------------
// Security events (AetherSecurityEvent.cs)
// ---------------------------------------------------------------------------

// AetherSecurityEventKind enumerates security-relevant observations Aether can
// detect at the protocol layer, without AI. Ports AetherSecurityEventKind.
type AetherSecurityEventKind int

const (
	// AetherSecurityEventKindNodeAuthAttempt — a node attempted to authenticate.
	AetherSecurityEventKindNodeAuthAttempt AetherSecurityEventKind = 0
	// AetherSecurityEventKindRoutingAnomaly — traffic deviated from expected paths.
	AetherSecurityEventKindRoutingAnomaly AetherSecurityEventKind = 1
	// AetherSecurityEventKindNodeBehaviourChange — a node deviated from its baseline.
	AetherSecurityEventKindNodeBehaviourChange AetherSecurityEventKind = 2
	// AetherSecurityEventKindEncryptionEvent — a key/cert validation event occurred.
	AetherSecurityEventKindEncryptionEvent AetherSecurityEventKind = 3
	// AetherSecurityEventKindIntrusionSignal — active attack signature detected.
	AetherSecurityEventKindIntrusionSignal AetherSecurityEventKind = 4
	// AetherSecurityEventKindPrivilegeAttempt — a node requested beyond its grant.
	AetherSecurityEventKindPrivilegeAttempt AetherSecurityEventKind = 5
)

// AetherThreatLevel is protocol-level threat severity as assessed by Aether
// itself, before any AI reasoning. Ports AetherThreatLevel; None=0..Critical=4.
type AetherThreatLevel int

const (
	// AetherThreatLevelNone — no threat.
	AetherThreatLevelNone AetherThreatLevel = 0
	// AetherThreatLevelLow — low severity.
	AetherThreatLevelLow AetherThreatLevel = 1
	// AetherThreatLevelMedium — medium severity.
	AetherThreatLevelMedium AetherThreatLevel = 2
	// AetherThreatLevelHigh — high severity.
	AetherThreatLevelHigh AetherThreatLevel = 3
	// AetherThreatLevelCritical — critical severity.
	AetherThreatLevelCritical AetherThreatLevel = 4
)

// AetherSecurityEvent is emitted by Aether when a security-relevant event occurs
// at the protocol layer — the primary feed for the AI Security Layer. Ports the
// AetherSecurityEvent record.
type AetherSecurityEvent struct {
	// NodeID identifies the node the event concerns.
	NodeID string
	// Kind is the security event category.
	Kind AetherSecurityEventKind
	// ThreatLevel is the protocol-assessed severity.
	ThreatLevel AetherThreatLevel
	// Description is a human-readable description.
	Description string
	// Metadata carries event-specific key/value pairs.
	Metadata map[string]string
	// OccurredAt is the UTC timestamp of the event.
	OccurredAt time.Time
}

// IsHighSeverity returns true when ThreatLevel is High or Critical. Ports
// AetherSecurityEvent.IsHighSeverity.
func (e AetherSecurityEvent) IsHighSeverity() bool {
	return e.ThreatLevel == AetherThreatLevelHigh || e.ThreatLevel == AetherThreatLevelCritical
}

// ---------------------------------------------------------------------------
// Network events (AetherNetworkEvent.cs)
// ---------------------------------------------------------------------------

// AetherNetworkEventKind enumerates mesh-wide topology and congestion
// observations. Ports AetherNetworkEventKind.
type AetherNetworkEventKind int

const (
	// AetherNetworkEventKindTopologyChanged — the topology changed.
	AetherNetworkEventKindTopologyChanged AetherNetworkEventKind = 0
	// AetherNetworkEventKindCongestionDetected — congestion was detected.
	AetherNetworkEventKindCongestionDetected AetherNetworkEventKind = 1
	// AetherNetworkEventKindPartitionDetected — a partition was detected.
	AetherNetworkEventKindPartitionDetected AetherNetworkEventKind = 2
)

// AetherNetworkEvent is emitted when the mesh topology or overall network health
// changes. Ports the AetherNetworkEvent record.
type AetherNetworkEvent struct {
	// Kind is the network observation kind.
	Kind AetherNetworkEventKind
	// NodeCount is the number of nodes in the mesh.
	NodeCount int
	// ActiveRouteCount is the number of active routes.
	ActiveRouteCount int
	// CongestionLevel is the aggregate congestion level [0,1].
	CongestionLevel float64
	// OccurredAt is the UTC timestamp of the event.
	OccurredAt time.Time
}

// IsHighCongestion returns true when CongestionLevel exceeds 0.75. Ports
// AetherNetworkEvent.IsHighCongestion.
func (e AetherNetworkEvent) IsHighCongestion() bool { return e.CongestionLevel > 0.75 }

// ---------------------------------------------------------------------------
// Telemetry surface (IAetherTelemetry.cs)
// ---------------------------------------------------------------------------

// IAetherTelemetryObserver receives events emitted by Aether. Implement this to
// react to mesh activity. Ports IAetherTelemetryObserver.
type IAetherTelemetryObserver interface {
	OnNodeEvent(e AetherNodeEvent)
	OnTransportEvent(e AetherTransportEvent)
	OnRouteEvent(e AetherRouteEvent)
	OnSecurityEvent(e AetherSecurityEvent)
	OnNetworkEvent(e AetherNetworkEvent)
}

// IAetherTelemetry is the outward-facing telemetry surface of Aether. Consumers
// subscribe; the returned unsubscribe func detaches (mirrors disposing the C#
// IDisposable handle). Ports IAetherTelemetry.
//
// The C# Subscribe returns IDisposable; Go idiom returns an unsubscribe func.
type IAetherTelemetry interface {
	// Subscribe registers observer for all Aether telemetry events and returns an
	// unsubscribe func. Calling it detaches the observer (idempotent).
	Subscribe(observer IAetherTelemetryObserver) (unsubscribe func())
}

// NullAetherTelemetry is a no-op telemetry — useful for unit tests and
// environments where Aether is absent. Subscribe returns a no-op unsubscribe; no
// events are ever emitted. Ports NullAetherTelemetry.
type NullAetherTelemetry struct{}

// NullAetherTelemetryInstance is the shared singleton, mirroring
// NullAetherTelemetry.Instance.
var NullAetherTelemetryInstance = &NullAetherTelemetry{}

// Subscribe registers observer and returns a no-op unsubscribe. Panics if
// observer is nil (mirrors ArgumentNullException.ThrowIfNull). Ports
// NullAetherTelemetry.Subscribe.
func (NullAetherTelemetry) Subscribe(observer IAetherTelemetryObserver) (unsubscribe func()) {
	if observer == nil {
		panic("observer must not be nil")
	}
	return func() {}
}

var _ IAetherTelemetry = (*NullAetherTelemetry)(nil)

// InMemoryAetherTelemetry is a working, thread-safe in-memory implementation of
// IAetherTelemetry: it fans every published event out to all current
// subscribers. This is the non-null in-memory implementation the port mandates —
// it plays the role Aether's real transport would (publishing events) so the AI
// Security Layer can be wired and exercised end-to-end.
//
// Concurrency: Publish* snapshots the observer list UNDER the lock and invokes
// callbacks OUTSIDE it, so a callback that (un)subscribes cannot self-deadlock
// the publisher and a slow observer cannot block subscription churn. The zero
// value is ready to use.
type InMemoryAetherTelemetry struct {
	mu        sync.Mutex
	observers []*aetherObserverSub
}

// NewInMemoryAetherTelemetry returns an empty in-memory telemetry publisher.
func NewInMemoryAetherTelemetry() *InMemoryAetherTelemetry { return &InMemoryAetherTelemetry{} }

// aetherObserverSub wraps one observer so identical observer values can be
// unsubscribed by pointer identity.
type aetherObserverSub struct {
	observer IAetherTelemetryObserver
}

// Subscribe registers observer to receive all telemetry events and returns an
// idempotent unsubscribe func. Ports IAetherTelemetry.Subscribe.
func (t *InMemoryAetherTelemetry) Subscribe(observer IAetherTelemetryObserver) (unsubscribe func()) {
	if observer == nil {
		panic("observer must not be nil")
	}
	sub := &aetherObserverSub{observer: observer}
	t.mu.Lock()
	t.observers = append(t.observers, sub)
	t.mu.Unlock()

	var once sync.Once
	return func() { once.Do(func() { t.unsubscribe(sub) }) }
}

func (t *InMemoryAetherTelemetry) unsubscribe(sub *aetherObserverSub) {
	t.mu.Lock()
	defer t.mu.Unlock()
	for i, s := range t.observers {
		if s == sub {
			t.observers = append(t.observers[:i], t.observers[i+1:]...)
			return
		}
	}
}

// snapshot returns a copy of the current observer list taken under the lock.
func (t *InMemoryAetherTelemetry) snapshot() []*aetherObserverSub {
	t.mu.Lock()
	out := make([]*aetherObserverSub, len(t.observers))
	copy(out, t.observers)
	t.mu.Unlock()
	return out
}

// SubscriberCount returns the number of active subscribers. Useful in tests.
func (t *InMemoryAetherTelemetry) SubscriberCount() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.observers)
}

// PublishNodeEvent fans a node event out to all subscribers.
func (t *InMemoryAetherTelemetry) PublishNodeEvent(e AetherNodeEvent) {
	for _, s := range t.snapshot() {
		s.observer.OnNodeEvent(e)
	}
}

// PublishTransportEvent fans a transport event out to all subscribers.
func (t *InMemoryAetherTelemetry) PublishTransportEvent(e AetherTransportEvent) {
	for _, s := range t.snapshot() {
		s.observer.OnTransportEvent(e)
	}
}

// PublishRouteEvent fans a route event out to all subscribers.
func (t *InMemoryAetherTelemetry) PublishRouteEvent(e AetherRouteEvent) {
	for _, s := range t.snapshot() {
		s.observer.OnRouteEvent(e)
	}
}

// PublishSecurityEvent fans a security event out to all subscribers.
func (t *InMemoryAetherTelemetry) PublishSecurityEvent(e AetherSecurityEvent) {
	for _, s := range t.snapshot() {
		s.observer.OnSecurityEvent(e)
	}
}

// PublishNetworkEvent fans a network event out to all subscribers.
func (t *InMemoryAetherTelemetry) PublishNetworkEvent(e AetherNetworkEvent) {
	for _, s := range t.snapshot() {
		s.observer.OnNetworkEvent(e)
	}
}

var _ IAetherTelemetry = (*InMemoryAetherTelemetry)(nil)
