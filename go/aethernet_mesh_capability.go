// aethernet_mesh_capability.go
//
// Ports CircleAI.AetherNet.MeshCapabilityRegistry (MeshCapabilityRegistry.cs) —
// RT-12 v1 mesh capability discovery. Peers broadcast what they have loaded
// ("I have Qwen3-1.7B-MNN with 2048 tokens of free KV budget on a Tier=Phone
// device"); v1 ships the contracts + an in-memory registry.
//
// C# types ported here:
//   MeshCapabilityAdvertisement       (record)
//   IMeshCapabilityRegistry           (interface)
//   InMemoryMeshCapabilityRegistry    (thread-safe in-memory impl)
//   IMeshCapabilityBroadcaster        (interface)
//   NullMeshCapabilityBroadcaster     (no-op default)
//
// DeviceTier is the existing package enum (selector.go) — the same concept the
// C# record imports from CircleAI.Core.

package circleai

import (
	"context"
	"sort"
	"strings"
	"sync"
	"time"
)

// MeshCapabilityAdvertisement is one peer's advertisement of what it can serve
// right now — pure data, no execution state. Ports the
// MeshCapabilityAdvertisement record.
//
// LatencyHintMs is C# nullable (int?); modelled as a pointer, nil when unknown.
type MeshCapabilityAdvertisement struct {
	// PeerID is a stable opaque identifier for the advertising peer.
	PeerID string
	// ModelID is the model the peer has loaded, e.g. "Qwen3-1.7B-MNN".
	ModelID string
	// FreeKvTokens is how many tokens of KV-cache budget the peer has spare.
	FreeKvTokens int
	// Tier is the peer's device tier.
	Tier DeviceTier
	// ContextWindowTokens is the model's configured context window.
	ContextWindowTokens int
	// AdvertisedAtUTC is when the peer last published this advertisement.
	AdvertisedAtUTC time.Time
	// LatencyHintMs is an optional round-trip estimate; nil when unknown.
	LatencyHintMs *int
}

// IMeshCapabilityRegistry holds the latest advertisement per peer and supports
// filtered query. Ports IMeshCapabilityRegistry.
//
// C# ValueTask maps to (…, error); the in-memory impl never errors, but the
// signature keeps parity with a future transport-backed impl. staleAfter is C#
// nullable (TimeSpan?); modelled as a pointer, nil = no staleness filter.
type IMeshCapabilityRegistry interface {
	// Upsert publishes or replaces an advertisement. Called by the transport on
	// receipt of a peer broadcast.
	Upsert(ctx context.Context, ad MeshCapabilityAdvertisement) error
	// Remove removes a peer (e.g. on explicit disconnect). Idempotent; returns
	// true when a peer was actually removed.
	Remove(ctx context.Context, peerID string) (bool, error)
	// List returns every advertisement currently known. When staleAfter is
	// non-nil, entries older than that are filtered out.
	List(staleAfter *time.Duration) []MeshCapabilityAdvertisement
	// Find returns every peer that has loaded modelID with at least
	// minFreeKvTokens of spare KV budget, sorted by spare budget descending.
	Find(modelID string, minFreeKvTokens int, staleAfter *time.Duration) []MeshCapabilityAdvertisement
}

// InMemoryMeshCapabilityRegistry is the default IMeshCapabilityRegistry —
// in-memory, thread-safe. The AetherNet transport plugs into this; without a
// transport the registry just stays empty. Ports InMemoryMeshCapabilityRegistry.
type InMemoryMeshCapabilityRegistry struct {
	mu      sync.RWMutex
	entries map[string]MeshCapabilityAdvertisement
	// nowUTC is an optional clock override for tests (mirrors the C# NowUtc
	// init-only property). Defaults to time.Now().UTC().
	nowUTC func() time.Time
}

// NewInMemoryMeshCapabilityRegistry constructs an empty registry using
// time.Now().UTC() as the clock.
func NewInMemoryMeshCapabilityRegistry() *InMemoryMeshCapabilityRegistry {
	return &InMemoryMeshCapabilityRegistry{
		entries: make(map[string]MeshCapabilityAdvertisement),
		nowUTC:  func() time.Time { return time.Now().UTC() },
	}
}

// WithClock overrides the staleness clock (test affordance mirroring the C#
// NowUtc init property). Returns the receiver for chaining.
func (r *InMemoryMeshCapabilityRegistry) WithClock(now func() time.Time) *InMemoryMeshCapabilityRegistry {
	if now != nil {
		r.mu.Lock()
		r.nowUTC = now
		r.mu.Unlock()
	}
	return r
}

func (r *InMemoryMeshCapabilityRegistry) clock() time.Time {
	if r.nowUTC != nil {
		return r.nowUTC()
	}
	return time.Now().UTC()
}

// Upsert implements IMeshCapabilityRegistry. Panics on a blank PeerID, mirroring
// the C# ArgumentException.ThrowIfNullOrWhiteSpace guard.
func (r *InMemoryMeshCapabilityRegistry) Upsert(_ context.Context, ad MeshCapabilityAdvertisement) error {
	if isBlankAether(ad.PeerID) {
		panic("advertisement PeerID must not be blank")
	}
	r.mu.Lock()
	r.entries[ad.PeerID] = ad
	r.mu.Unlock()
	return nil
}

// Remove implements IMeshCapabilityRegistry. Idempotent; returns true when a
// peer was removed. Panics on a blank peerID (matches the C# guard).
func (r *InMemoryMeshCapabilityRegistry) Remove(_ context.Context, peerID string) (bool, error) {
	if isBlankAether(peerID) {
		panic("peerID must not be blank")
	}
	r.mu.Lock()
	_, existed := r.entries[peerID]
	delete(r.entries, peerID)
	r.mu.Unlock()
	return existed, nil
}

// List implements IMeshCapabilityRegistry. When staleAfter is nil, returns every
// entry; otherwise filters out entries older than the cutoff.
func (r *InMemoryMeshCapabilityRegistry) List(staleAfter *time.Duration) []MeshCapabilityAdvertisement {
	r.mu.RLock()
	defer r.mu.RUnlock()

	if staleAfter == nil {
		out := make([]MeshCapabilityAdvertisement, 0, len(r.entries))
		for _, a := range r.entries {
			out = append(out, a)
		}
		return out
	}
	cutoff := r.clock().Add(-*staleAfter)
	out := make([]MeshCapabilityAdvertisement, 0, len(r.entries))
	for _, a := range r.entries {
		if !a.AdvertisedAtUTC.Before(cutoff) {
			out = append(out, a)
		}
	}
	return out
}

// Find implements IMeshCapabilityRegistry. Returns peers that loaded modelID
// (case-insensitive) with at least minFreeKvTokens spare, freshest-eligible
// only, sorted by FreeKvTokens descending. Panics on a blank modelID.
func (r *InMemoryMeshCapabilityRegistry) Find(modelID string, minFreeKvTokens int, staleAfter *time.Duration) []MeshCapabilityAdvertisement {
	if isBlankAether(modelID) {
		panic("modelID must not be blank")
	}
	r.mu.RLock()
	defer r.mu.RUnlock()

	// Mirror the C#: cutoff is DateTimeOffset.MinValue when staleAfter is null,
	// i.e. no entry is filtered out on freshness.
	var cutoff time.Time
	if staleAfter != nil {
		cutoff = r.clock().Add(-*staleAfter)
	} else {
		cutoff = time.Time{} // zero value == MinValue-equivalent lower bound
	}

	out := make([]MeshCapabilityAdvertisement, 0)
	for _, a := range r.entries {
		if !strings.EqualFold(a.ModelID, modelID) {
			continue
		}
		if a.FreeKvTokens < minFreeKvTokens {
			continue
		}
		if a.AdvertisedAtUTC.Before(cutoff) {
			continue
		}
		out = append(out, a)
	}
	// Sort by spare budget descending — most-capable peer first. Stable so equal
	// budgets keep a deterministic order (by PeerID) for reproducible tests.
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].FreeKvTokens != out[j].FreeKvTokens {
			return out[i].FreeKvTokens > out[j].FreeKvTokens
		}
		return out[i].PeerID < out[j].PeerID
	})
	return out
}

var _ IMeshCapabilityRegistry = (*InMemoryMeshCapabilityRegistry)(nil)

// IMeshCapabilityBroadcaster publishes OUR advertisement to the mesh. Ports
// IMeshCapabilityBroadcaster. v1 ships a no-op default; the AetherNet transport
// binding (v2) supersedes it.
type IMeshCapabilityBroadcaster interface {
	// Broadcast publishes our current advertisement to the mesh. v1 may be a
	// no-op when no transport is registered.
	Broadcast(ctx context.Context, ad MeshCapabilityAdvertisement) error
}

// NullMeshCapabilityBroadcaster is the default broadcaster — does nothing. Used
// when no AetherNet transport is bound. Ports NullMeshCapabilityBroadcaster.
type NullMeshCapabilityBroadcaster struct{}

// NullMeshCapabilityBroadcasterInstance is the shared singleton, mirroring
// NullMeshCapabilityBroadcaster.Instance.
var NullMeshCapabilityBroadcasterInstance = &NullMeshCapabilityBroadcaster{}

// Broadcast implements IMeshCapabilityBroadcaster as a no-op. Ports
// NullMeshCapabilityBroadcaster.BroadcastAsync.
func (NullMeshCapabilityBroadcaster) Broadcast(_ context.Context, _ MeshCapabilityAdvertisement) error {
	return nil
}

var _ IMeshCapabilityBroadcaster = (*NullMeshCapabilityBroadcaster)(nil)

// LocalLoopbackMeshCapabilityBroadcaster is a working in-memory broadcaster that
// feeds OUR advertisement straight into a local IMeshCapabilityRegistry — the
// mesh transport collapsed to a loopback. It lets a single-process deployment
// exercise the full broadcast → registry → query path deterministically without
// the 2.7.0 AetherNet transport. Not a stub: every Broadcast durably upserts.
type LocalLoopbackMeshCapabilityBroadcaster struct {
	registry IMeshCapabilityRegistry
}

// NewLocalLoopbackMeshCapabilityBroadcaster wires the broadcaster to a registry.
func NewLocalLoopbackMeshCapabilityBroadcaster(registry IMeshCapabilityRegistry) *LocalLoopbackMeshCapabilityBroadcaster {
	if registry == nil {
		panic("registry must not be nil")
	}
	return &LocalLoopbackMeshCapabilityBroadcaster{registry: registry}
}

// Broadcast upserts the advertisement into the bound registry.
func (b *LocalLoopbackMeshCapabilityBroadcaster) Broadcast(ctx context.Context, ad MeshCapabilityAdvertisement) error {
	return b.registry.Upsert(ctx, ad)
}

var _ IMeshCapabilityBroadcaster = (*LocalLoopbackMeshCapabilityBroadcaster)(nil)
