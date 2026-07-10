// network_types.go
//
// Ports the CircleAI.Networking transport-abstraction vocabulary:
//   NetworkTypes.cs   -> TransportKind, ConnectivityState, MessagePriority, PeerRole
//                        (SyncDeliveryMode already lives in sync.go — reused, not re-declared)
//   NetworkPayload.cs -> NetworkPayload (+ NewNetworkPayload == NetworkPayload.Create)
//   NetworkContext.cs -> NetworkContext (+ NetworkContextOffline == NetworkContext.Offline)
//   PeerInfo.cs       -> PeerInfo
//
// These are the in-memory / deterministic abstraction that the 10 concrete
// transports implement. A real socket is injected behind INetworkTransport
// (network_transport.go); nothing here touches a wire.

package circleai

import (
	"strings"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// TransportKind — NetworkTypes.cs enum TransportKind
// ---------------------------------------------------------------------------

// TransportKind enumerates every transport medium the abstraction can select.
// Ordinals are stable and match the C# declaration order exactly, because the
// default cascade and policy checks reason about them by ordinal.
type TransportKind int

const (
	// TransportKindHttp — request/response over HTTP(S).
	TransportKindHttp TransportKind = iota
	// TransportKindWebSocket — full-duplex WebSocket.
	TransportKindWebSocket
	// TransportKindGrpc — gRPC streaming.
	TransportKindGrpc
	// TransportKindMqtt — MQTT pub/sub broker.
	TransportKindMqtt
	// TransportKindTcp — raw TCP socket.
	TransportKindTcp
	// TransportKindUdp — raw UDP datagrams.
	TransportKindUdp
	// TransportKindWiFi — WiFi Direct / mDNS / LAN — no Aether required.
	TransportKindWiFi
	// TransportKindBluetooth — raw BLE GATT — no Aether required.
	TransportKindBluetooth
	// TransportKindNearLink — Huawei SLE / HarmonyOS — no Aether required.
	TransportKindNearLink
	// TransportKindAether — full Aether mesh (Signal E2E + AODV + SOS).
	TransportKindAether
	// TransportKindDtn — 72hr store-and-forward over any transport.
	TransportKindDtn
	// TransportKindLocalStore — offline queue — no live path at all.
	TransportKindLocalStore
)

// String renders the C# enum member name for a TransportKind.
func (k TransportKind) String() string {
	switch k {
	case TransportKindHttp:
		return "Http"
	case TransportKindWebSocket:
		return "WebSocket"
	case TransportKindGrpc:
		return "Grpc"
	case TransportKindMqtt:
		return "Mqtt"
	case TransportKindTcp:
		return "Tcp"
	case TransportKindUdp:
		return "Udp"
	case TransportKindWiFi:
		return "WiFi"
	case TransportKindBluetooth:
		return "Bluetooth"
	case TransportKindNearLink:
		return "NearLink"
	case TransportKindAether:
		return "Aether"
	case TransportKindDtn:
		return "Dtn"
	case TransportKindLocalStore:
		return "LocalStore"
	default:
		return "Unknown"
	}
}

// IsCloudTransport reports whether k is one of the cloud transports gated by
// INetworkPolicy.AllowCloudTransports (Http/WebSocket/Grpc/Mqtt). Mirrors the
// grouping used by NetworkPolicyBuilder.Policy.Permits when NoCloud is set.
func (k TransportKind) IsCloudTransport() bool {
	switch k {
	case TransportKindHttp, TransportKindWebSocket, TransportKindGrpc, TransportKindMqtt:
		return true
	default:
		return false
	}
}

// ---------------------------------------------------------------------------
// ConnectivityState — NetworkTypes.cs enum ConnectivityState
// ---------------------------------------------------------------------------

// ConnectivityState summarises the overall reachability of the node.
type ConnectivityState int

const (
	// ConnectivityStateOnline — a cloud/internet path is available.
	ConnectivityStateOnline ConnectivityState = iota
	// ConnectivityStateLocalOnly — only LAN/local transports are usable.
	ConnectivityStateLocalOnly
	// ConnectivityStateMeshOnly — only mesh (Aether/BLE/NearLink) is usable.
	ConnectivityStateMeshOnly
	// ConnectivityStateOffline — no live path; local store only.
	ConnectivityStateOffline
)

// String renders the C# enum member name for a ConnectivityState.
func (s ConnectivityState) String() string {
	switch s {
	case ConnectivityStateOnline:
		return "Online"
	case ConnectivityStateLocalOnly:
		return "LocalOnly"
	case ConnectivityStateMeshOnly:
		return "MeshOnly"
	case ConnectivityStateOffline:
		return "Offline"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// MessagePriority — NetworkTypes.cs enum MessagePriority
// ---------------------------------------------------------------------------

// MessagePriority ranks a payload from Low to Emergency. Higher ordinals are
// more urgent; the selector uses this to bias toward mesh/SOS transports.
type MessagePriority int

const (
	// MessagePriorityLow — deferrable background traffic.
	MessagePriorityLow MessagePriority = iota
	// MessagePriorityNormal — default interactive traffic.
	MessagePriorityNormal
	// MessagePriorityHigh — latency-sensitive traffic.
	MessagePriorityHigh
	// MessagePriorityUrgent — must go out promptly.
	MessagePriorityUrgent
	// MessagePriorityEmergency — SOS / life-safety traffic.
	MessagePriorityEmergency
)

// String renders the C# enum member name for a MessagePriority.
func (p MessagePriority) String() string {
	switch p {
	case MessagePriorityLow:
		return "Low"
	case MessagePriorityNormal:
		return "Normal"
	case MessagePriorityHigh:
		return "High"
	case MessagePriorityUrgent:
		return "Urgent"
	case MessagePriorityEmergency:
		return "Emergency"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// PeerRole — NetworkTypes.cs enum PeerRole
// ---------------------------------------------------------------------------

// PeerRole classifies a discovered peer's function in the mesh.
type PeerRole int

const (
	// PeerRolePeer — an ordinary endpoint.
	PeerRolePeer PeerRole = iota
	// PeerRoleRelay — forwards traffic for others.
	PeerRoleRelay
	// PeerRoleBridge — bridges two transports/networks.
	PeerRoleBridge
	// PeerRoleSink — a terminal collector (e.g. gateway to cloud).
	PeerRoleSink
)

// String renders the C# enum member name for a PeerRole.
func (r PeerRole) String() string {
	switch r {
	case PeerRolePeer:
		return "Peer"
	case PeerRoleRelay:
		return "Relay"
	case PeerRoleBridge:
		return "Bridge"
	case PeerRoleSink:
		return "Sink"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// NetworkPayload — NetworkPayload.cs
// ---------------------------------------------------------------------------

// NetworkPayload is the immutable envelope for a single message or data unit
// traversing any transport. Transports MUST NOT mutate it — create a new
// payload instead. Ports the C# `sealed record NetworkPayload`.
//
// Go note: the C# record uses ReadOnlyMemory<byte> for Data and an immutable
// IReadOnlyDictionary for Metadata. Go has no readonly slices/maps, so callers
// honour immutability by convention exactly as the C# XML doc mandates; helper
// constructors here never share mutable references with the caller.
type NetworkPayload struct {
	// ID is a unique payload identifier (32-char hex, no dashes — Guid "N").
	ID string
	// SourceID is the origin node id, or empty if unset.
	SourceID string
	// DestinationID is the intended recipient, or empty for broadcast/unset.
	DestinationID string
	// Data is the raw payload bytes.
	Data []byte
	// Priority ranks delivery urgency.
	Priority MessagePriority
	// TTL is the optional time-to-live; nil means no expiry.
	TTL *time.Duration
	// ContentType is a MIME-ish media type; defaults to application/octet-stream.
	ContentType string
	// Metadata is a free-form string→string bag. Never nil after construction.
	Metadata map[string]string
	// CreatedAt is the UTC creation time.
	CreatedAt time.Time
}

// NewNetworkPayload mirrors NetworkPayload.Create: it stamps a fresh Guid "N"
// id, no SourceID, an empty Metadata map, and a UTC CreatedAt. Pass nil for
// data to send an empty payload. destinationID may be "" for no destination.
//
// The optional parameters follow the C# defaults:
//
//	priority    = MessagePriorityNormal
//	contentType = "application/octet-stream"
//	ttl         = nil
//
// Use NewNetworkPayloadWith to override them.
func NewNetworkPayload(data []byte, destinationID string) NetworkPayload {
	return NewNetworkPayloadWith(data, destinationID, MessagePriorityNormal, "application/octet-stream", nil)
}

// NewNetworkPayloadWith is the full-control constructor behind NewNetworkPayload,
// exposing the priority/contentType/ttl the C# Create overload defaults. An
// empty contentType is normalised to "application/octet-stream" to match the
// C# default-argument behaviour.
func NewNetworkPayloadWith(data []byte, destinationID string, priority MessagePriority, contentType string, ttl *time.Duration) NetworkPayload {
	if contentType == "" {
		contentType = "application/octet-stream"
	}
	// Defensive copy so the payload never shares its backing array with the
	// caller — upholds the "immutable envelope" contract.
	var buf []byte
	if len(data) > 0 {
		buf = make([]byte, len(data))
		copy(buf, data)
	}
	return NetworkPayload{
		ID:            newPayloadID(),
		SourceID:      "",
		DestinationID: destinationID,
		Data:          buf,
		Priority:      priority,
		TTL:           ttl,
		ContentType:   contentType,
		Metadata:      map[string]string{},
		CreatedAt:     time.Now().UTC(),
	}
}

// WithSource returns a copy of the payload with SourceID set — the immutable
// "create a new payload instead" pattern the C# doc-comment prescribes. The
// Metadata map is copied so the two payloads never alias.
func (p NetworkPayload) WithSource(sourceID string) NetworkPayload {
	clone := p
	clone.SourceID = sourceID
	clone.Metadata = copyStringMap(p.Metadata)
	return clone
}

// WithMetadata returns a copy of the payload with key=value merged into a fresh
// Metadata map, leaving the receiver untouched.
func (p NetworkPayload) WithMetadata(key, value string) NetworkPayload {
	clone := p
	clone.Metadata = copyStringMap(p.Metadata)
	if clone.Metadata == nil {
		clone.Metadata = map[string]string{}
	}
	clone.Metadata[key] = value
	return clone
}

// newPayloadID returns a 32-character lowercase hex id with no dashes,
// matching Guid.NewGuid().ToString("N").
func newPayloadID() string {
	return strings.ReplaceAll(uuid.NewString(), "-", "")
}

// copyStringMap returns a shallow copy of m (nil-safe).
func copyStringMap(m map[string]string) map[string]string {
	if m == nil {
		return nil
	}
	out := make(map[string]string, len(m))
	for k, v := range m {
		out[k] = v
	}
	return out
}

// ---------------------------------------------------------------------------
// NetworkContext — NetworkContext.cs
// ---------------------------------------------------------------------------

// NetworkContext is a snapshot of the current connectivity state. Ports the
// C# `sealed record NetworkContext`. Pointer fields model the C# `int?`/`long?`
// nullable columns (nil == "unknown").
type NetworkContext struct {
	// State is the overall connectivity classification.
	State ConnectivityState
	// PreferredTransport is the transport the monitor would pick right now.
	PreferredTransport TransportKind
	// AvailableTransports lists every currently usable transport.
	AvailableTransports []TransportKind
	// SignalStrengthDbm is the radio RSSI in dBm, or nil if unknown.
	SignalStrengthDbm *int
	// EstimatedBandwidthBps is the estimated throughput, or nil if unknown.
	EstimatedBandwidthBps *int64
	// LatencyMs is the estimated round-trip latency, or nil if unknown.
	LatencyMs *int64
	// NearbyPeerCount is the number of peers currently in range.
	NearbyPeerCount int
	// SnapshotAt is the UTC time this snapshot was taken.
	SnapshotAt time.Time
}

// NewNetworkContextOffline mirrors NetworkContext.Offline: a fully-offline
// snapshot preferring LocalStore with no transports, no radio metrics, zero
// peers, stamped at the current UTC time. A constructor (not a package var) so
// each snapshot carries a fresh SnapshotAt, matching the C# static initialiser
// semantics used at call sites.
func NewNetworkContextOffline() NetworkContext {
	return NetworkContext{
		State:                 ConnectivityStateOffline,
		PreferredTransport:    TransportKindLocalStore,
		AvailableTransports:   []TransportKind{},
		SignalStrengthDbm:     nil,
		EstimatedBandwidthBps: nil,
		LatencyMs:             nil,
		NearbyPeerCount:       0,
		SnapshotAt:            time.Now().UTC(),
	}
}

// ---------------------------------------------------------------------------
// PeerInfo — PeerInfo.cs
// ---------------------------------------------------------------------------

// PeerInfo describes a discovered peer on any transport. Ports the C#
// `sealed record PeerInfo`. SignalStrengthDbm is a pointer to model `int?`.
type PeerInfo struct {
	// NodeID is the peer's stable node identifier.
	NodeID string
	// DisplayName is a human-friendly name, or empty if unknown.
	DisplayName string
	// SupportedTransports lists the transports the peer advertises.
	SupportedTransports []TransportKind
	// Role is the peer's function in the mesh.
	Role PeerRole
	// SignalStrengthDbm is the last-seen RSSI in dBm, or nil if unknown.
	SignalStrengthDbm *int
	// LastSeen is the UTC time the peer was last observed.
	LastSeen time.Time
}
