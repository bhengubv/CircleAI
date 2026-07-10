// security_node_trust_registry.go
//
// Ports CircleAI.Security.NodeTrustRegistry + NodeTrustEntry
// (NodeTrustRegistry.cs).
//
// Thread-safe, per-peer trust store:
//   - Each peer gets a score in [0, 1]. 1.0 = fully trusted; 0.0 = fully lost.
//   - ApplyDegradation drops the score and records the triggering event.
//   - ApplyRecovery heals all peers passively (called by a background timer).
//   - TrustScoreUpdates is an unbounded channel; readers receive every change.
//
// Transport-agnostic: stores PeerSecurityEvent, emits PeerTrustScoreUpdate.
//
// Locking: the registry holds a map guarded by its own mutex; each NodeTrustEntry
// carries its own mutex so per-peer mutation is fine-grained (mirrors the C#
// `lock (entry)` pattern over a ConcurrentDictionary). Trust-update publication
// goes through the non-blocking unbounded channel Write, so holding an entry
// lock while publishing cannot deadlock.

package circleai

import (
	"context"
	"math"
	"sync"
	"time"
)

// NodeTrustEntry is the per-peer mutable trust state. Exposed for diagnostics
// and tests. Ports NodeTrustEntry.
//
// Read TrustScore / LastUpdated / RecentEvents only after taking a registry
// snapshot method; direct field reads from concurrent goroutines are not
// synchronised (the registry mediates all mutation under the entry mutex).
type NodeTrustEntry struct {
	// NodeID is the stable peer identifier.
	NodeID string
	// TrustScore is the current trust score in [0, 1].
	TrustScore float64
	// LastUpdated is the UTC timestamp of the last mutation.
	LastUpdated time.Time
	// RecentEvents is a bounded history of security events (oldest-first).
	RecentEvents []PeerSecurityEvent

	mu sync.Mutex
}

// NodeTrustRegistry maintains per-peer trust scores, event history, and a live
// unbounded channel of trust score changes consumed by PeerIntelligenceService.
// Ports NodeTrustRegistry.
type NodeTrustRegistry struct {
	options *SecurityOptions

	mu    sync.Mutex
	nodes map[string]*NodeTrustEntry

	channel *unboundedChannel[PeerTrustScoreUpdate]
}

// NewNodeTrustRegistry constructs a registry over the given options.
func NewNodeTrustRegistry(options *SecurityOptions) *NodeTrustRegistry {
	return &NodeTrustRegistry{
		options: options,
		nodes:   make(map[string]*NodeTrustEntry),
		channel: newUnboundedChannel[PeerTrustScoreUpdate](),
	}
}

// TrustScoreUpdates returns a stream of trust score changes. It never completes
// during normal operation; callers should cancel ctx to break out. Each item is
// delivered to exactly one reader (competing consumers), matching the C#
// unbounded ChannelReader. Ports NodeTrustRegistry.TrustScoreUpdates.
func (r *NodeTrustRegistry) TrustScoreUpdates(ctx context.Context) <-chan PeerTrustScoreUpdate {
	return r.channel.ReadAll(ctx)
}

// GetOrCreate returns the existing entry for nodeID, or creates a new one
// initialised to InitialTrustScore. Ports NodeTrustRegistry.GetOrCreate.
func (r *NodeTrustRegistry) GetOrCreate(nodeID string) *NodeTrustEntry {
	r.mu.Lock()
	defer r.mu.Unlock()
	if entry, ok := r.nodes[nodeID]; ok {
		return entry
	}
	entry := &NodeTrustEntry{
		NodeID:      nodeID,
		TrustScore:  r.options.InitialTrustScore,
		LastUpdated: time.Now().UTC(),
	}
	r.nodes[nodeID] = entry
	return entry
}

// AllNodeIDs returns all peer IDs currently tracked. Ports
// NodeTrustRegistry.AllNodeIds.
func (r *NodeTrustRegistry) AllNodeIDs() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	ids := make([]string, 0, len(r.nodes))
	for id := range r.nodes {
		ids = append(ids, id)
	}
	return ids
}

// GetTrustScore returns the current trust score for nodeID, or InitialTrustScore
// for unknown peers. Ports NodeTrustRegistry.GetTrustScore.
func (r *NodeTrustRegistry) GetTrustScore(nodeID string) float64 {
	r.mu.Lock()
	entry, ok := r.nodes[nodeID]
	r.mu.Unlock()
	if !ok {
		return r.options.InitialTrustScore
	}
	entry.mu.Lock()
	defer entry.mu.Unlock()
	return entry.TrustScore
}

// ApplyDegradation applies trust degradation for a security event. The score is
// clamped to [0, 1]; the event is appended to the per-peer history (oldest
// dropped first past MaxEventsPerNode); a PeerTrustScoreUpdate is published when
// the score actually moved. Returns (previous, current). Ports
// NodeTrustRegistry.ApplyDegradation.
func (r *NodeTrustRegistry) ApplyDegradation(securityEvent PeerSecurityEvent, degradationAmount float64) (previous, current float64) {
	entry := r.GetOrCreate(securityEvent.NodeID)

	entry.mu.Lock()
	previous = entry.TrustScore
	entry.TrustScore = clampFloat(previous-degradationAmount, 0.0, 1.0)
	entry.LastUpdated = securityEvent.OccurredAt

	entry.RecentEvents = append(entry.RecentEvents, securityEvent)
	for len(entry.RecentEvents) > r.options.MaxEventsPerNode {
		entry.RecentEvents = entry.RecentEvents[1:]
	}

	current = entry.TrustScore
	nodeID := entry.NodeID
	entry.mu.Unlock()

	if math.Abs(current-previous) > 0.0001 {
		r.publish(nodeID, previous, current, securityEvent.Description, securityEvent.OccurredAt)
	}
	return previous, current
}

// ApplyRecovery passively heals all tracked peers by
// RecoveryRatePerSecond × elapsed seconds. Peers already at 1.0 are skipped.
// Called by the background recovery timer. Ports NodeTrustRegistry.ApplyRecovery.
func (r *NodeTrustRegistry) ApplyRecovery(elapsed time.Duration) {
	amount := r.options.RecoveryRatePerSecond * elapsed.Seconds()
	if amount <= 0 {
		return
	}

	r.mu.Lock()
	entries := make([]*NodeTrustEntry, 0, len(r.nodes))
	for _, e := range r.nodes {
		entries = append(entries, e)
	}
	r.mu.Unlock()

	now := time.Now().UTC()
	for _, entry := range entries {
		entry.mu.Lock()
		if entry.TrustScore >= 1.0 {
			entry.mu.Unlock()
			continue
		}
		previous := entry.TrustScore
		entry.TrustScore = math.Min(1.0, previous+amount)
		entry.LastUpdated = now
		newScore := entry.TrustScore
		nodeID := entry.NodeID
		entry.mu.Unlock()

		r.publish(nodeID, previous, newScore, "passive-recovery", now)
	}
}

// GetRecentEvents returns events for nodeID that fall within EventWindow of now.
// Returns an empty slice for unknown peers. Ports
// NodeTrustRegistry.GetRecentEvents.
func (r *NodeTrustRegistry) GetRecentEvents(nodeID string) []PeerSecurityEvent {
	r.mu.Lock()
	entry, ok := r.nodes[nodeID]
	r.mu.Unlock()
	if !ok {
		return []PeerSecurityEvent{}
	}

	cutoff := time.Now().UTC().Add(-r.options.EventWindow)
	entry.mu.Lock()
	defer entry.mu.Unlock()
	out := make([]PeerSecurityEvent, 0, len(entry.RecentEvents))
	for _, e := range entry.RecentEvents {
		if !e.OccurredAt.Before(cutoff) {
			out = append(out, e)
		}
	}
	return out
}

func (r *NodeTrustRegistry) publish(nodeID string, previous, current float64, reason string, at time.Time) {
	r.channel.Write(PeerTrustScoreUpdate{
		NodeID:        nodeID,
		PreviousScore: previous,
		NewScore:      current,
		Reason:        reason,
		ChangedAt:     at,
	})
}
