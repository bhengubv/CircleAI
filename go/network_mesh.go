// network_mesh.go
//
// Ports CircleAI.Networking.IMeshNetwork (IMeshNetwork.cs) and its mandated
// working in-memory implementation.
//
// IMeshNetwork exposes mesh-specific concerns: local node identity, the current
// peer set, and mesh health as a NetworkContext snapshot.
//
// Go modelling:
//   string LocalNodeId { get; }                      -> LocalNodeID() string
//   Task<IReadOnlyList<string>> GetPeerIdsAsync(ct)  -> GetPeerIDs(ctx) ([]string, error)
//   Task<NetworkContext> GetMeshHealthAsync(ct)      -> GetMeshHealth(ctx) (NetworkContext, error)

package circleai

import (
	"context"
	"sort"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// IMeshNetwork — IMeshNetwork.cs
// ---------------------------------------------------------------------------

// IMeshNetwork is the mesh-specific surface: topology, node identity, health.
type IMeshNetwork interface {
	// LocalNodeID is this node's stable identifier on the mesh.
	LocalNodeID() string
	// GetPeerIDs returns the ids of peers currently reachable on the mesh.
	GetPeerIDs(ctx context.Context) ([]string, error)
	// GetMeshHealth returns a NetworkContext snapshot describing mesh health.
	GetMeshHealth(ctx context.Context) (NetworkContext, error)
}

// ---------------------------------------------------------------------------
// InMemoryMeshNetwork — working IMeshNetwork
// ---------------------------------------------------------------------------

// InMemoryMeshNetwork is a deterministic IMeshNetwork whose peer table is driven
// by AddPeer/RemovePeer. GetMeshHealth derives a MeshOnly/Offline NetworkContext
// from the live peer set. Safe for concurrent use.
type InMemoryMeshNetwork struct {
	localNodeID string

	mu    sync.Mutex
	peers map[string]PeerInfo
}

// NewInMemoryMeshNetwork creates a mesh rooted at localNodeID.
func NewInMemoryMeshNetwork(localNodeID string) *InMemoryMeshNetwork {
	return &InMemoryMeshNetwork{
		localNodeID: localNodeID,
		peers:       make(map[string]PeerInfo),
	}
}

// LocalNodeID returns this node's id.
func (m *InMemoryMeshNetwork) LocalNodeID() string { return m.localNodeID }

// AddPeer inserts or updates a peer in the mesh table (keyed by NodeID).
func (m *InMemoryMeshNetwork) AddPeer(peer PeerInfo) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.peers[peer.NodeID] = peer
}

// RemovePeer drops a peer by node id. No-op if absent.
func (m *InMemoryMeshNetwork) RemovePeer(nodeID string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.peers, nodeID)
}

// GetPeerIDs returns the reachable peer ids, sorted for deterministic output.
func (m *InMemoryMeshNetwork) GetPeerIDs(ctx context.Context) ([]string, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	m.mu.Lock()
	ids := make([]string, 0, len(m.peers))
	for id := range m.peers {
		ids = append(ids, id)
	}
	m.mu.Unlock()
	sort.Strings(ids)
	return ids, nil
}

// GetMeshHealth returns a NetworkContext derived from the current peer set. With
// zero peers the mesh reports Offline (LocalStore preferred); with one or more
// peers it reports MeshOnly with Aether preferred and the strongest peer signal.
func (m *InMemoryMeshNetwork) GetMeshHealth(ctx context.Context) (NetworkContext, error) {
	if err := ctx.Err(); err != nil {
		return NetworkContext{}, err
	}
	m.mu.Lock()
	peerCount := len(m.peers)
	var bestSignal *int
	for _, p := range m.peers {
		if p.SignalStrengthDbm == nil {
			continue
		}
		if bestSignal == nil || *p.SignalStrengthDbm > *bestSignal {
			v := *p.SignalStrengthDbm
			bestSignal = &v
		}
	}
	m.mu.Unlock()

	if peerCount == 0 {
		return NewNetworkContextOffline(), nil
	}
	return NetworkContext{
		State:                 ConnectivityStateMeshOnly,
		PreferredTransport:    TransportKindAether,
		AvailableTransports:   []TransportKind{TransportKindAether},
		SignalStrengthDbm:     bestSignal,
		EstimatedBandwidthBps: nil,
		LatencyMs:             nil,
		NearbyPeerCount:       peerCount,
		SnapshotAt:            time.Now().UTC(),
	}, nil
}

var _ IMeshNetwork = (*InMemoryMeshNetwork)(nil)
