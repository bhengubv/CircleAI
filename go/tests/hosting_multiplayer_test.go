// hosting_multiplayer_test.go
//
// Verifies CircleAI.Hosting.Multiplayer ports: GuestPeerIdentity defaults,
// MultiplayerHub join/leave/cursor/edit broadcasts through an injected
// broadcaster, LWW-by-rev edit acceptance/rejection, presence snapshots, and the
// ColourFor cursor-colour hash.

package circleai_test

import (
	"sync"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// captureBroadcaster records every SendToOthersInGroup call.
type captureBroadcaster struct {
	mu    sync.Mutex
	calls []broadcast
}

type broadcast struct {
	group  string
	except string
	method string
	args   []interface{}
}

func (b *captureBroadcaster) SendToOthersInGroup(group, except, method string, args ...interface{}) {
	b.mu.Lock()
	b.calls = append(b.calls, broadcast{group, except, method, args})
	b.mu.Unlock()
}

func (b *captureBroadcaster) methods() []string {
	b.mu.Lock()
	defer b.mu.Unlock()
	var m []string
	for _, c := range b.calls {
		m = append(m, c.method)
	}
	return m
}

func TestGuestPeerIdentity_Defaults(t *testing.T) {
	g := circleai.NewGuestPeerIdentity("", "")
	if g.DisplayName() != "Guest" {
		t.Errorf("display name = %q, want Guest", g.DisplayName())
	}
	if len(g.PeerID()) != 32 { // GUID hex without dashes
		t.Errorf("peerID length = %d, want 32", len(g.PeerID()))
	}
	g2 := circleai.NewGuestPeerIdentity("pid", "Alice")
	if g2.PeerID() != "pid" || g2.DisplayName() != "Alice" {
		t.Errorf("explicit values not honoured: %q / %q", g2.PeerID(), g2.DisplayName())
	}
}

func TestMultiplayerHub_JoinCursorEdit(t *testing.T) {
	circleai.MultiplayerResetStateForTesting()
	defer circleai.MultiplayerResetStateForTesting()

	bc := &captureBroadcaster{}
	hub, err := circleai.NewMultiplayerHub(circleai.NewGuestPeerIdentity("peer-1", "Alice"), bc)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	hub.OnConnected("conn-1")
	hub.JoinDocument("conn-1", "doc-A")

	peers := circleai.MultiplayerPeers("doc-A")
	if len(peers) != 1 || peers[0].DisplayName != "Alice" || peers[0].DocID != "doc-A" {
		t.Fatalf("presence wrong: %+v", peers)
	}

	hub.SendCursor("conn-1", "doc-A", 3, 7)

	// First edit at rev 5 is accepted.
	if got := hub.SendEdit("conn-1", "doc-A", "hello", 5); got != 5 {
		t.Errorf("rev5 accept returned %d, want 5", got)
	}
	if circleai.MultiplayerCurrentRev("doc-A") != 5 {
		t.Errorf("current rev = %d, want 5", circleai.MultiplayerCurrentRev("doc-A"))
	}

	// Strictly-stale edit at rev 2 (< current 5) is rejected → server returns the
	// current rev (5) and does NOT broadcast EditApplied (newRev.Rev != rev).
	if got := hub.SendEdit("conn-1", "doc-A", "stale", 2); got != 5 {
		t.Errorf("stale edit returned %d, want current rev 5", got)
	}

	// Newer edit at rev 9 accepted.
	if got := hub.SendEdit("conn-1", "doc-A", "newer", 9); got != 9 {
		t.Errorf("rev9 accept returned %d, want 9", got)
	}
	if circleai.MultiplayerCurrentRev("doc-A") != 9 {
		t.Errorf("current rev = %d, want 9", circleai.MultiplayerCurrentRev("doc-A"))
	}

	methods := bc.methods()
	// Expected broadcasts: PeerJoined, CursorChanged, EditApplied (rev5),
	// EditApplied (rev9). The strictly-stale rev2 does NOT broadcast.
	countEditApplied := 0
	for _, m := range methods {
		if m == "EditApplied" {
			countEditApplied++
		}
	}
	if countEditApplied != 2 {
		t.Errorf("EditApplied broadcasts = %d, want 2 (strictly-stale one suppressed): %v", countEditApplied, methods)
	}
}

func TestMultiplayerHub_Disconnect(t *testing.T) {
	circleai.MultiplayerResetStateForTesting()
	defer circleai.MultiplayerResetStateForTesting()

	bc := &captureBroadcaster{}
	hub, _ := circleai.NewMultiplayerHub(circleai.NewGuestPeerIdentity("p", "Bob"), bc)
	hub.OnConnected("c1")
	hub.JoinDocument("c1", "doc-X")
	hub.OnDisconnected("c1")

	if len(circleai.MultiplayerPeers("doc-X")) != 0 {
		t.Error("peer should be removed on disconnect")
	}
	found := false
	for _, m := range bc.methods() {
		if m == "PeerLeft" {
			found = true
		}
	}
	if !found {
		t.Error("PeerLeft not broadcast on disconnect")
	}
}

func TestMultiplayerHub_NilIdentityRejected(t *testing.T) {
	if _, err := circleai.NewMultiplayerHub(nil, nil); err == nil {
		t.Error("nil identity should error")
	}
}

func TestColourForPeer(t *testing.T) {
	// Empty id → fixed fallback.
	if circleai.ColourForPeer("") != "#5a4fcf" {
		t.Errorf("empty peer colour = %q", circleai.ColourForPeer(""))
	}
	// Deterministic + valid HSL for a non-empty id.
	c1 := circleai.ColourForPeer("alice")
	c2 := circleai.ColourForPeer("alice")
	if c1 != c2 {
		t.Error("colour must be deterministic")
	}
	if len(c1) < 4 || c1[:4] != "hsl(" {
		t.Errorf("colour = %q, want hsl(...)", c1)
	}
	// Different ids generally differ.
	if circleai.ColourForPeer("bob") == circleai.ColourForPeer("carol") {
		t.Log("hash collision (rare) — acceptable")
	}
}
