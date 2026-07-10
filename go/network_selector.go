// network_selector.go
//
// Ports CircleAI.Networking.ITransportSelector (ITransportSelector.cs) and
// supplies the mandated working DefaultTransportSelector implementation.
//
// The C# interface documents the default cascade verbatim:
//   gRPC → WebSocket → HTTP → MQTT → TCP →
//     WiFi → Bluetooth → NearLink → Aether → DTN → LocalStore
// DefaultTransportSelector reproduces that ordering, filtered by what the
// NetworkContext reports as available, and biased by MessagePriority: an
// Emergency/Urgent payload floats mesh + SOS-capable transports (Aether,
// NearLink, Bluetooth, WiFi, DTN) ahead of the cloud tier so life-safety
// traffic survives a degraded link. LocalStore is ALWAYS the terminal fallback
// so a payload can never be undeliverable to the offline queue.

package circleai

// ---------------------------------------------------------------------------
// ITransportSelector — ITransportSelector.cs
// ---------------------------------------------------------------------------

// ITransportSelector selects the best transport for a payload+context.
type ITransportSelector interface {
	// SelectBest returns the single best transport for payload in context.
	SelectBest(payload NetworkPayload, context NetworkContext) TransportKind
	// GetCascade returns the ordered fallback list to try, best first. The
	// list always ends with TransportKindLocalStore.
	GetCascade(payload NetworkPayload, context NetworkContext) []TransportKind
}

// defaultCascadeOrder is the canonical preference order from the C# doc-comment.
var defaultCascadeOrder = []TransportKind{
	TransportKindGrpc,
	TransportKindWebSocket,
	TransportKindHttp,
	TransportKindMqtt,
	TransportKindTcp,
	TransportKindWiFi,
	TransportKindBluetooth,
	TransportKindNearLink,
	TransportKindAether,
	TransportKindDtn,
	TransportKindLocalStore,
}

// meshPreferredOrder floats mesh / local-radio / store-and-forward transports
// ahead of the cloud tier. Used for Urgent+ payloads so SOS traffic prefers a
// path that survives an internet outage. LocalStore stays terminal.
var meshPreferredOrder = []TransportKind{
	TransportKindAether,
	TransportKindNearLink,
	TransportKindBluetooth,
	TransportKindWiFi,
	TransportKindDtn,
	TransportKindGrpc,
	TransportKindWebSocket,
	TransportKindHttp,
	TransportKindMqtt,
	TransportKindTcp,
	TransportKindLocalStore,
}

// DefaultTransportSelector is the working ITransportSelector. It is stateless
// and safe for concurrent use.
type DefaultTransportSelector struct {
	// Policy gates which transports are eligible. When nil,
	// DefaultNetworkPolicyInstance (permit-all) is used.
	Policy INetworkPolicy
}

// NewDefaultTransportSelector returns a selector using policy. Pass nil for the
// permissive DefaultNetworkPolicy.
func NewDefaultTransportSelector(policy INetworkPolicy) *DefaultTransportSelector {
	if policy == nil {
		policy = DefaultNetworkPolicyInstance
	}
	return &DefaultTransportSelector{Policy: policy}
}

// SelectBest returns the first transport in the computed cascade — i.e. the
// most-preferred permitted+available transport. It never returns "nothing":
// the cascade always ends with LocalStore.
func (s *DefaultTransportSelector) SelectBest(payload NetworkPayload, context NetworkContext) TransportKind {
	cascade := s.GetCascade(payload, context)
	return cascade[0]
}

// GetCascade builds the ordered fallback list. Ordering rules:
//   - If the policy forces a transport, that transport leads (still followed by
//     LocalStore as the guaranteed terminal fallback).
//   - Otherwise start from mesh-preferred order when the payload is Urgent+ or
//     the policy sets MeshFirst; else the default cloud-first order.
//   - Keep only transports that are BOTH permitted by the policy AND present in
//     context.AvailableTransports.
//   - Always append LocalStore last (dedup-safe) so the queue is reachable.
func (s *DefaultTransportSelector) GetCascade(payload NetworkPayload, context NetworkContext) []TransportKind {
	policy := s.Policy
	if policy == nil {
		policy = DefaultNetworkPolicyInstance
	}

	available := make(map[TransportKind]struct{}, len(context.AvailableTransports))
	for _, t := range context.AvailableTransports {
		available[t] = struct{}{}
	}

	// A forced transport short-circuits ordering. It is honoured even if the
	// context does not list it as available (the caller has decided), but it
	// must still pass the policy's own Permits check.
	if forced := policy.ForceTransport(); forced != nil {
		out := []TransportKind{}
		if policy.Permits(*forced, payload) {
			out = append(out, *forced)
		}
		return appendLocalStore(out)
	}

	base := defaultCascadeOrder
	if policy.MeshFirst() || payload.Priority >= MessagePriorityUrgent {
		base = meshPreferredOrder
	}

	out := make([]TransportKind, 0, len(base))
	for _, t := range base {
		if t == TransportKindLocalStore {
			continue // appended unconditionally at the end
		}
		if _, ok := available[t]; !ok {
			continue
		}
		if !policy.Permits(t, payload) {
			continue
		}
		out = append(out, t)
	}
	return appendLocalStore(out)
}

// appendLocalStore appends TransportKindLocalStore unless it is already the
// last element, guaranteeing a non-empty cascade whose terminal entry is the
// offline queue.
func appendLocalStore(cascade []TransportKind) []TransportKind {
	if n := len(cascade); n > 0 && cascade[n-1] == TransportKindLocalStore {
		return cascade
	}
	return append(cascade, TransportKindLocalStore)
}

var _ ITransportSelector = (*DefaultTransportSelector)(nil)
