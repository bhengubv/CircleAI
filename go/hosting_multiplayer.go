// hosting_multiplayer.go
//
// Ports CircleAI.Hosting.Multiplayer:
//   IMultiplayerPeerIdentity, GuestPeerIdentity (Contracts.cs)
//   MultiplayerHub + PeerState (MultiplayerHub.cs)
//
// The C# hub is a SignalR Hub: it broadcasts to per-document groups via
// Clients.OthersInGroup(...).SendAsync and manages membership via Groups.Add/Remove.
// The Go port keeps all that logic (per-doc groups, LWW-by-rev edits, live
// cursors, presence, the ColourFor hash) but replaces the SignalR transport with
// an injected IMultiplayerBroadcaster so the hub is testable in-process. The
// LWW/rev algorithm and cursor-colour hash match the C# exactly.

package circleai

import (
	"fmt"
	"strings"
	"sync"
	"time"
	"unicode/utf16"

	"github.com/google/uuid"
)

// utf16CodeUnits returns the UTF-16 code units of s, so hashes that iterate a
// C# string char-by-char (which is UTF-16) reproduce identical values.
func utf16CodeUnits(s string) []uint16 {
	return utf16.Encode([]rune(s))
}

// IMultiplayerPeerIdentity resolves the identity of the peer making a hub call.
// Ports CircleAI.Hosting.Multiplayer.IMultiplayerPeerIdentity.
type IMultiplayerPeerIdentity interface {
	// PeerID is a stable id (used to derive a cursor colour).
	PeerID() string
	// DisplayName is the human-readable display name.
	DisplayName() string
}

// GuestPeerIdentity is an anonymous guest identity. Ports
// CircleAI.Hosting.Multiplayer.GuestPeerIdentity.
type GuestPeerIdentity struct {
	peerID      string
	displayName string
}

// NewGuestPeerIdentity builds a guest identity. Empty peerID gets a fresh GUID
// (hex, no dashes); empty displayName defaults to "Guest".
func NewGuestPeerIdentity(peerID, displayName string) *GuestPeerIdentity {
	if peerID == "" {
		peerID = strings.ReplaceAll(uuid.New().String(), "-", "")
	}
	if displayName == "" {
		displayName = "Guest"
	}
	return &GuestPeerIdentity{peerID: peerID, displayName: displayName}
}

// PeerID implements IMultiplayerPeerIdentity.
func (g *GuestPeerIdentity) PeerID() string { return g.peerID }

// DisplayName implements IMultiplayerPeerIdentity.
func (g *GuestPeerIdentity) DisplayName() string { return g.displayName }

var _ IMultiplayerPeerIdentity = (*GuestPeerIdentity)(nil)

// PeerState is a snapshot of one connected peer. Ports the nested
// MultiplayerHub.PeerState record. DocID is "" when the peer is not in a doc.
type PeerState struct {
	ConnectionID string
	DisplayName  string
	Color        string
	DocID        string
}

// docRevState mirrors the nested MultiplayerHub.DocRevState record.
type docRevState struct {
	Rev       int64
	UpdatedAt time.Time
}

// IMultiplayerBroadcaster is the transport the hub broadcasts through. It stands
// in for SignalR's Clients.OthersInGroup(group).SendAsync(method, args...). The
// hub only ever sends to "others in a doc group", so the surface is one method.
type IMultiplayerBroadcaster interface {
	// SendToOthersInGroup delivers event `method` with args to every connection
	// in `group` except `exceptConnectionID`.
	SendToOthersInGroup(group, exceptConnectionID, method string, args ...interface{})
}

// MultiplayerHub is the multiplayer collaboration hub. Ports
// CircleAI.Hosting.Multiplayer.MultiplayerHub. Unlike the C# hub (which derives
// per-connection state from SignalR's Context), the Go hub takes an explicit
// connectionID on each call so it is transport-agnostic. State (rev-by-doc,
// peer-by-conn) is shared across the process, matching the C# static fields.
type MultiplayerHub struct {
	peerIdentity IMultiplayerPeerIdentity
	broadcaster  IMultiplayerBroadcaster
}

var (
	mpMu         sync.Mutex
	mpRevByDoc   = map[string]docRevState{}
	mpPeerByConn = map[string]PeerState{}
)

// NewMultiplayerHub builds a hub for one connection's peer identity. Returns an
// error when peerIdentity is nil. broadcaster may be nil (broadcasts become
// no-ops), which is convenient for single-peer state-only tests.
func NewMultiplayerHub(peerIdentity IMultiplayerPeerIdentity, broadcaster IMultiplayerBroadcaster) (*MultiplayerHub, error) {
	if peerIdentity == nil {
		return nil, errNilArg("peerIdentity")
	}
	return &MultiplayerHub{peerIdentity: peerIdentity, broadcaster: broadcaster}, nil
}

// OnConnected registers a peer's connection. Ports MultiplayerHub.OnConnectedAsync.
func (h *MultiplayerHub) OnConnected(connectionID string) {
	mpMu.Lock()
	mpPeerByConn[connectionID] = PeerState{
		ConnectionID: connectionID,
		DisplayName:  h.peerIdentity.DisplayName(),
		Color:        ColourForPeer(h.peerIdentity.PeerID()),
		DocID:        "",
	}
	mpMu.Unlock()
}

// OnDisconnected removes a peer and notifies its doc group. Ports
// MultiplayerHub.OnDisconnectedAsync.
func (h *MultiplayerHub) OnDisconnected(connectionID string) {
	mpMu.Lock()
	peer, ok := mpPeerByConn[connectionID]
	delete(mpPeerByConn, connectionID)
	mpMu.Unlock()
	if ok && peer.DocID != "" {
		h.sendToOthers(docGroup(peer.DocID), connectionID, "PeerLeft", peer.DocID, peer.ConnectionID, peer.DisplayName)
	}
}

// JoinDocument subscribes a connection to a per-doc group and notifies peers.
// Ports MultiplayerHub.JoinDocument.
func (h *MultiplayerHub) JoinDocument(connectionID, docID string) {
	if isBlank(docID) {
		return
	}
	mpMu.Lock()
	peer, ok := mpPeerByConn[connectionID]
	if ok {
		peer.DocID = docID
		mpPeerByConn[connectionID] = peer
	}
	mpMu.Unlock()
	if ok {
		h.sendToOthers(docGroup(docID), connectionID, "PeerJoined", docID, peer.ConnectionID, peer.DisplayName, peer.Color)
	}
}

// LeaveDocument unsubscribes a connection and notifies peers. Ports
// MultiplayerHub.LeaveDocument.
func (h *MultiplayerHub) LeaveDocument(connectionID, docID string) {
	if isBlank(docID) {
		return
	}
	mpMu.Lock()
	peer, ok := mpPeerByConn[connectionID]
	if ok {
		peer.DocID = ""
		mpPeerByConn[connectionID] = peer
	}
	mpMu.Unlock()
	if ok {
		h.sendToOthers(docGroup(docID), connectionID, "PeerLeft", docID, peer.ConnectionID, peer.DisplayName)
	}
}

// SendCursor broadcasts a cursor position to peers. Ports MultiplayerHub.SendCursor.
func (h *MultiplayerHub) SendCursor(connectionID, docID string, line, ch int) {
	mpMu.Lock()
	peer, ok := mpPeerByConn[connectionID]
	mpMu.Unlock()
	if !ok {
		return
	}
	h.sendToOthers(docGroup(docID), connectionID, "CursorChanged", peer.ConnectionID, peer.DisplayName, peer.Color, line, ch)
}

// SendEdit applies an edit iff its rev is greater than the server's current rev.
// Returns the new rev (or the server's current rev if the client's rev was
// stale). Ports MultiplayerHub.SendEdit (LWW-by-rev).
func (h *MultiplayerHub) SendEdit(connectionID, docID, content string, rev int64) int64 {
	mpMu.Lock()
	prev, exists := mpRevByDoc[docID]
	var newState docRevState
	if !exists {
		newState = docRevState{Rev: maxInt64(rev, 1), UpdatedAt: time.Now().UTC()}
	} else if rev <= prev.Rev {
		newState = prev
	} else {
		newState = docRevState{Rev: rev, UpdatedAt: time.Now().UTC()}
	}
	mpRevByDoc[docID] = newState
	mpMu.Unlock()

	if newState.Rev != rev {
		// Rejected — client gets current rev back and can rebase.
		return newState.Rev
	}
	h.sendToOthers(docGroup(docID), connectionID, "EditApplied", docID, content, rev, connectionID)
	return rev
}

func (h *MultiplayerHub) sendToOthers(group, exceptConnectionID, method string, args ...interface{}) {
	if h.broadcaster != nil {
		h.broadcaster.SendToOthersInGroup(group, exceptConnectionID, method, args...)
	}
}

// MultiplayerPeers returns a snapshot of who is currently in a document. Ports
// the static MultiplayerHub.Peers.
func MultiplayerPeers(docID string) []PeerState {
	mpMu.Lock()
	defer mpMu.Unlock()
	var out []PeerState
	for _, p := range mpPeerByConn {
		if p.DocID == docID {
			out = append(out, p)
		}
	}
	return out
}

// MultiplayerCurrentRev returns the server-known rev for a document (0 when
// never touched). Ports the static MultiplayerHub.CurrentRev.
func MultiplayerCurrentRev(docID string) int64 {
	mpMu.Lock()
	defer mpMu.Unlock()
	if state, ok := mpRevByDoc[docID]; ok {
		return state.Rev
	}
	return 0
}

// MultiplayerResetStateForTesting wipes the shared hub state. Ports the static
// MultiplayerHub.ResetStateForTesting. Do NOT call in production.
func MultiplayerResetStateForTesting() {
	mpMu.Lock()
	mpRevByDoc = map[string]docRevState{}
	mpPeerByConn = map[string]PeerState{}
	mpMu.Unlock()
}

func docGroup(docID string) string { return "doc:" + docID }

// (maxInt64 lives in sync_hlc.go — reused here, not redeclared.)

// ColourForPeer hashes a peer id to an HSL cursor colour. Ports
// MultiplayerHub.ColourFor exactly (h = h*31 + c over UTF-16 code units; the C#
// unchecked int arithmetic is reproduced with int32 wraparound).
func ColourForPeer(peerID string) string {
	if peerID == "" {
		return "#5a4fcf"
	}
	var h int32
	for _, r := range utf16CodeUnits(peerID) {
		h = h*31 + int32(r)
	}
	hue := ((h % 360) + 360) % 360
	return fmt.Sprintf("hsl(%d, 70%%, 55%%)", hue)
}
