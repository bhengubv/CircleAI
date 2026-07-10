// network_policy.go
//
// Ports the CircleAI.Networking policy layer:
//   INetworkPolicy.cs       -> INetworkPolicy
//   DefaultNetworkPolicy.cs -> DefaultNetworkPolicy (+ DefaultNetworkPolicyInstance)
//   NetworkPolicyBuilder.cs -> NetworkPolicyBuilder (+ its private Policy impl)
//
// Policy rules are applied BEFORE a transport is chosen. Examples the C# doc
// lists: "WiFi-only", "mesh-first", "no cloud when roaming".

package circleai

// ---------------------------------------------------------------------------
// INetworkPolicy — INetworkPolicy.cs
// ---------------------------------------------------------------------------

// INetworkPolicy captures the policy rules applied before choosing a transport.
type INetworkPolicy interface {
	// Permits reports whether transport may carry payload under this policy.
	Permits(transport TransportKind, payload NetworkPayload) bool
	// ForceTransport returns a transport that must be used regardless of the
	// cascade, or nil when the selector is free to choose.
	ForceTransport() *TransportKind
	// MeshFirst reports whether mesh transports should be preferred first.
	MeshFirst() bool
	// OfflineQueueEnabled reports whether payloads may be queued when offline.
	OfflineQueueEnabled() bool
	// AllowCloudTransports reports whether cloud transports are permitted at all.
	AllowCloudTransports() bool
}

// ---------------------------------------------------------------------------
// DefaultNetworkPolicy — DefaultNetworkPolicy.cs
// ---------------------------------------------------------------------------

// DefaultNetworkPolicy is the permissive default: all transports allowed,
// offline queue on, no forced transport, mesh not prioritised, cloud allowed.
// Ports the C# `sealed class DefaultNetworkPolicy`.
type DefaultNetworkPolicy struct{}

// DefaultNetworkPolicyInstance is the shared singleton, mirroring
// DefaultNetworkPolicy.Instance.
var DefaultNetworkPolicyInstance INetworkPolicy = DefaultNetworkPolicy{}

// Permits always returns true — the default policy blocks nothing.
func (DefaultNetworkPolicy) Permits(_ TransportKind, _ NetworkPayload) bool { return true }

// ForceTransport returns nil — the default policy forces nothing.
func (DefaultNetworkPolicy) ForceTransport() *TransportKind { return nil }

// MeshFirst returns false — the default policy does not prioritise mesh.
func (DefaultNetworkPolicy) MeshFirst() bool { return false }

// OfflineQueueEnabled returns true — the default policy keeps the offline queue on.
func (DefaultNetworkPolicy) OfflineQueueEnabled() bool { return true }

// AllowCloudTransports returns true — the default policy allows cloud transports.
func (DefaultNetworkPolicy) AllowCloudTransports() bool { return true }

var _ INetworkPolicy = DefaultNetworkPolicy{}

// ---------------------------------------------------------------------------
// NetworkPolicyBuilder — NetworkPolicyBuilder.cs
// ---------------------------------------------------------------------------

// NetworkPolicyBuilder is a fluent builder for INetworkPolicy. Ports the C#
// `sealed class NetworkPolicyBuilder`. The zero value is NOT ready — use
// NewNetworkPolicyBuilder so the queue-enabled default (true) is set.
type NetworkPolicyBuilder struct {
	allowed      map[TransportKind]struct{}
	meshFirst    bool
	noCloud      bool
	queueEnabled bool
	force        *TransportKind
}

// NewNetworkPolicyBuilder returns a builder with the C# field defaults:
// no allow-list, mesh-first off, no-cloud off, queue ENABLED, no forced kind.
func NewNetworkPolicyBuilder() *NetworkPolicyBuilder {
	return &NetworkPolicyBuilder{
		allowed:      map[TransportKind]struct{}{},
		queueEnabled: true,
	}
}

// MeshFirst sets the mesh-first flag and returns the builder for chaining.
func (b *NetworkPolicyBuilder) MeshFirst() *NetworkPolicyBuilder { b.meshFirst = true; return b }

// NoCloud forbids cloud transports (Http/WebSocket/Grpc/Mqtt) and returns the
// builder for chaining.
func (b *NetworkPolicyBuilder) NoCloud() *NetworkPolicyBuilder { b.noCloud = true; return b }

// DisableQueue turns the offline queue off and returns the builder for chaining.
func (b *NetworkPolicyBuilder) DisableQueue() *NetworkPolicyBuilder { b.queueEnabled = false; return b }

// Force pins a single transport and returns the builder for chaining.
func (b *NetworkPolicyBuilder) Force(t TransportKind) *NetworkPolicyBuilder {
	tc := t
	b.force = &tc
	return b
}

// Allow adds one or more transports to the allow-list and returns the builder
// for chaining. When the allow-list is non-empty, Build's policy permits only
// listed transports (subject to the no-cloud rule).
func (b *NetworkPolicyBuilder) Allow(kinds ...TransportKind) *NetworkPolicyBuilder {
	for _, k := range kinds {
		b.allowed[k] = struct{}{}
	}
	return b
}

// Build materialises an INetworkPolicy from the accumulated settings. When no
// transports were allowed, the resulting policy has a nil allow-list (permit
// all except cloud-when-NoCloud), matching the C# `_allowed.Count > 0 ? ... :
// null` branch.
func (b *NetworkPolicyBuilder) Build() INetworkPolicy {
	var allowed map[TransportKind]struct{}
	if len(b.allowed) > 0 {
		allowed = make(map[TransportKind]struct{}, len(b.allowed))
		for k := range b.allowed {
			allowed[k] = struct{}{}
		}
	}
	var force *TransportKind
	if b.force != nil {
		f := *b.force
		force = &f
	}
	return &builtNetworkPolicy{
		allowed:      allowed,
		meshFirst:    b.meshFirst,
		noCloud:      b.noCloud,
		queueEnabled: b.queueEnabled,
		force:        force,
	}
}

// builtNetworkPolicy is the concrete INetworkPolicy produced by
// NetworkPolicyBuilder.Build, mirroring the C# private nested `Policy` class.
type builtNetworkPolicy struct {
	allowed      map[TransportKind]struct{} // nil == allow all
	meshFirst    bool
	noCloud      bool
	queueEnabled bool
	force        *TransportKind
}

// Permits mirrors the nested Policy.Permits: reject cloud transports when
// NoCloud, otherwise permit when the allow-list is nil or contains the kind.
func (p *builtNetworkPolicy) Permits(t TransportKind, _ NetworkPayload) bool {
	if p.noCloud && t.IsCloudTransport() {
		return false
	}
	if p.allowed == nil {
		return true
	}
	_, ok := p.allowed[t]
	return ok
}

// ForceTransport returns the pinned transport, or nil.
func (p *builtNetworkPolicy) ForceTransport() *TransportKind {
	if p.force == nil {
		return nil
	}
	f := *p.force
	return &f
}

// MeshFirst returns the mesh-first flag.
func (p *builtNetworkPolicy) MeshFirst() bool { return p.meshFirst }

// OfflineQueueEnabled returns the queue-enabled flag.
func (p *builtNetworkPolicy) OfflineQueueEnabled() bool { return p.queueEnabled }

// AllowCloudTransports returns !noCloud, matching the nested Policy.
func (p *builtNetworkPolicy) AllowCloudTransports() bool { return !p.noCloud }

var _ INetworkPolicy = (*builtNetworkPolicy)(nil)
