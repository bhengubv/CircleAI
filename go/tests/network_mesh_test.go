// network_mesh_test.go
//
// Verifies network_mesh.go InMemoryMeshNetwork: node identity, peer table
// mutation, sorted peer ids, and mesh-health derivation (Offline with no peers,
// MeshOnly + best signal with peers).

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func dbm(v int) *int { return &v }

func TestMesh_LocalNodeIDAndEmptyHealth(t *testing.T) {
	m := circleai.NewInMemoryMeshNetwork("node-local")
	if m.LocalNodeID() != "node-local" {
		t.Errorf("LocalNodeID got %q", m.LocalNodeID())
	}
	ids, err := m.GetPeerIDs(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(ids) != 0 {
		t.Errorf("expected no peers, got %v", ids)
	}
	health, err := m.GetMeshHealth(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if health.State != circleai.ConnectivityStateOffline {
		t.Errorf("empty mesh health State got %v want Offline", health.State)
	}
	if health.PreferredTransport != circleai.TransportKindLocalStore {
		t.Errorf("empty mesh PreferredTransport got %v want LocalStore", health.PreferredTransport)
	}
	if health.NearbyPeerCount != 0 {
		t.Errorf("empty mesh NearbyPeerCount got %d", health.NearbyPeerCount)
	}
}

func TestMesh_PeersSortedAndHealth(t *testing.T) {
	m := circleai.NewInMemoryMeshNetwork("me")
	m.AddPeer(circleai.PeerInfo{NodeID: "zeta", Role: circleai.PeerRolePeer, SignalStrengthDbm: dbm(-70), LastSeen: time.Now().UTC()})
	m.AddPeer(circleai.PeerInfo{NodeID: "alpha", Role: circleai.PeerRoleRelay, SignalStrengthDbm: dbm(-55), LastSeen: time.Now().UTC()})
	m.AddPeer(circleai.PeerInfo{NodeID: "mid", Role: circleai.PeerRolePeer, LastSeen: time.Now().UTC()}) // nil signal

	ids, err := m.GetPeerIDs(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	want := []string{"alpha", "mid", "zeta"}
	if len(ids) != len(want) {
		t.Fatalf("peer ids got %v want %v", ids, want)
	}
	for i := range want {
		if ids[i] != want[i] {
			t.Fatalf("peer ids not sorted: got %v want %v", ids, want)
		}
	}

	health, err := m.GetMeshHealth(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if health.State != circleai.ConnectivityStateMeshOnly {
		t.Errorf("mesh health State got %v want MeshOnly", health.State)
	}
	if health.PreferredTransport != circleai.TransportKindAether {
		t.Errorf("mesh PreferredTransport got %v want Aether", health.PreferredTransport)
	}
	if health.NearbyPeerCount != 3 {
		t.Errorf("NearbyPeerCount got %d want 3", health.NearbyPeerCount)
	}
	if health.SignalStrengthDbm == nil || *health.SignalStrengthDbm != -55 {
		t.Errorf("mesh best signal got %v want -55", health.SignalStrengthDbm)
	}
	if len(health.AvailableTransports) != 1 || health.AvailableTransports[0] != circleai.TransportKindAether {
		t.Errorf("mesh AvailableTransports got %v want [Aether]", health.AvailableTransports)
	}
}

func TestMesh_RemovePeer(t *testing.T) {
	m := circleai.NewInMemoryMeshNetwork("me")
	m.AddPeer(circleai.PeerInfo{NodeID: "p1", LastSeen: time.Now().UTC()})
	m.AddPeer(circleai.PeerInfo{NodeID: "p2", LastSeen: time.Now().UTC()})
	m.RemovePeer("p1")
	m.RemovePeer("absent") // no-op
	ids, _ := m.GetPeerIDs(context.Background())
	if len(ids) != 1 || ids[0] != "p2" {
		t.Errorf("after remove got %v want [p2]", ids)
	}
}

func TestMesh_ContextCancelled(t *testing.T) {
	m := circleai.NewInMemoryMeshNetwork("me")
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := m.GetPeerIDs(ctx); err == nil {
		t.Error("GetPeerIDs should honour cancelled ctx")
	}
	if _, err := m.GetMeshHealth(ctx); err == nil {
		t.Error("GetMeshHealth should honour cancelled ctx")
	}
}
