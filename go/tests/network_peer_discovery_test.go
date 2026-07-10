// network_peer_discovery_test.go
//
// Verifies network_peer_discovery.go InMemoryPeerDiscovery (IPeerDiscovery) and
// the SchedulingHint / SyncDelta.SchedulingHint port:
//   - Announce fans out to every Discover subscriber across a shared plane
//   - subscribe-before-announce: an Announce right after Discover() is not lost
//   - ctx cancel deregisters the subscriber; Close completes all streams
//   - Discover on a closed discoverer yields a closed stream
//   - SchedulingHint attaches to a SyncDelta and defaults to nil

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// recvPeer is provided by network_aethernet_test.go (same package) — reused here.

func samplePeer(id string) circleai.PeerInfo {
	return circleai.PeerInfo{
		NodeID:              id,
		DisplayName:         id,
		SupportedTransports: []circleai.TransportKind{circleai.TransportKindAether},
		Role:                circleai.PeerRolePeer,
		LastSeen:            time.Now().UTC(),
	}
}

func TestPeerDiscovery_AnnounceFansOutAcrossPlane(t *testing.T) {
	plane := circleai.NewPeerAnnouncePlane()
	a := circleai.NewInMemoryPeerDiscovery(plane)
	b := circleai.NewInMemoryPeerDiscovery(plane)
	defer a.Close()
	defer b.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	streamA := a.Discover(ctx)
	streamB := b.Discover(ctx)

	if err := a.Announce(ctx, samplePeer("node-x")); err != nil {
		t.Fatalf("Announce error: %v", err)
	}

	// Both A's own subscriber and B's subscriber see the announcement.
	if got := recvPeer(t, streamA); got.NodeID != "node-x" {
		t.Errorf("streamA got %q want node-x", got.NodeID)
	}
	if got := recvPeer(t, streamB); got.NodeID != "node-x" {
		t.Errorf("streamB got %q want node-x", got.NodeID)
	}
}

func TestPeerDiscovery_SubscribeBeforeAnnounceNotLost(t *testing.T) {
	plane := circleai.NewPeerAnnouncePlane()
	d := circleai.NewInMemoryPeerDiscovery(plane)
	defer d.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Discover registers synchronously; the Announce that immediately follows
	// must be captured, not raced away.
	stream := d.Discover(ctx)
	if err := d.Announce(ctx, samplePeer("node-y")); err != nil {
		t.Fatalf("Announce error: %v", err)
	}
	if got := recvPeer(t, stream); got.NodeID != "node-y" {
		t.Errorf("got %q want node-y", got.NodeID)
	}
}

func TestPeerDiscovery_CancelDeregisters(t *testing.T) {
	plane := circleai.NewPeerAnnouncePlane()
	d := circleai.NewInMemoryPeerDiscovery(plane)
	defer d.Close()

	ctx, cancel := context.WithCancel(context.Background())
	stream := d.Discover(ctx)
	if d.SubscriberCount() != 1 {
		t.Fatalf("SubscriberCount got %d want 1", d.SubscriberCount())
	}

	cancel()
	// The stream must close, and the subscriber must be dropped.
	select {
	case _, ok := <-stream:
		if ok {
			// Drain until closed.
			for range stream {
			}
		}
	case <-time.After(2 * time.Second):
		t.Fatal("cancelled Discover stream did not close")
	}

	deadline := time.After(2 * time.Second)
	for d.SubscriberCount() != 0 {
		select {
		case <-deadline:
			t.Fatalf("subscriber not deregistered after cancel, count=%d", d.SubscriberCount())
		default:
			time.Sleep(5 * time.Millisecond)
		}
	}
}

func TestPeerDiscovery_CloseCompletesStreams(t *testing.T) {
	plane := circleai.NewPeerAnnouncePlane()
	d := circleai.NewInMemoryPeerDiscovery(plane)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	stream := d.Discover(ctx)

	d.Close()
	select {
	case _, ok := <-stream:
		if ok {
			for range stream {
			}
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Close did not complete the Discover stream")
	}

	// Discover after Close yields an already-closed stream.
	post := d.Discover(ctx)
	select {
	case _, ok := <-post:
		if ok {
			t.Error("Discover after Close should yield a closed stream")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("post-Close Discover stream never closed")
	}
}

func TestPeerDiscovery_IsolatedPlanes(t *testing.T) {
	// Discoverers on different planes do not hear each other.
	a := circleai.NewInMemoryPeerDiscovery(circleai.NewPeerAnnouncePlane())
	b := circleai.NewInMemoryPeerDiscovery(circleai.NewPeerAnnouncePlane())
	defer a.Close()
	defer b.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	streamB := b.Discover(ctx)

	if err := a.Announce(ctx, samplePeer("secret")); err != nil {
		t.Fatalf("Announce error: %v", err)
	}
	select {
	case p := <-streamB:
		t.Errorf("b on a separate plane should not hear a's announcement, got %q", p.NodeID)
	case <-time.After(150 * time.Millisecond):
		// expected: nothing crosses planes
	}
}

func TestSchedulingHint_AttachesToSyncDelta(t *testing.T) {
	// Default: a SyncDelta with no hint leaves the pointer nil.
	bare := circleai.SyncDelta{OwnerID: "u", DomainKey: circleai.SyncDomainKeys.Persona}
	if bare.SchedulingHint != nil {
		t.Error("SyncDelta.SchedulingHint should default to nil")
	}

	when := time.Now().UTC().Add(30 * time.Minute)
	hint := &circleai.SchedulingHint{
		PreferredPeerIds:   []string{"dev-1", "dev-2"},
		SuggestedWindowUtc: &when,
		ConfidenceScore:    0.9,
	}
	d := circleai.SyncDelta{
		OwnerID:        "u",
		DomainKey:      circleai.SyncDomainKeys.MemoryEpisodic,
		DeliveryMode:   circleai.SyncDeliveryModeGuaranteed,
		SchedulingHint: hint,
	}
	if d.SchedulingHint == nil {
		t.Fatal("SchedulingHint was not attached")
	}
	if len(d.SchedulingHint.PreferredPeerIds) != 2 || d.SchedulingHint.PreferredPeerIds[0] != "dev-1" {
		t.Errorf("PreferredPeerIds got %v", d.SchedulingHint.PreferredPeerIds)
	}
	if d.SchedulingHint.SuggestedWindowUtc == nil || !d.SchedulingHint.SuggestedWindowUtc.Equal(when) {
		t.Errorf("SuggestedWindowUtc got %v want %v", d.SchedulingHint.SuggestedWindowUtc, when)
	}
	if d.SchedulingHint.ConfidenceScore != 0.9 {
		t.Errorf("ConfidenceScore got %v want 0.9", d.SchedulingHint.ConfidenceScore)
	}
}
