// network_websocket.go
//
// Ports CircleAI.Networking.WebSocket:
//   WebSocketTransportCommons.cs -> WebSocketLinkState, WebSocketMessageType,
//                                   WebSocketEndpointDescriptor,
//                                   WebSocketFrameSummary,
//                                   InMemoryWebSocketSessionRegistry
//   WebSocketTransport.cs        -> WebSocketTransport (INetworkTransport)
//
// The C# WebSocketTransport wraps a real ClientWebSocket: it connects to a Uri,
// pumps received binary frames into an unbounded inbound channel, and sends each
// payload as a single binary end-of-message frame. Per the porting rules (NO
// stubs — every contract gets a working deterministic implementation), the Go
// port replaces the live ClientWebSocket with a shared in-memory WebSocketFabric
// so two transports pointed at the same endpoint Uri exchange frames, recording
// a WebSocketFrameSummary per frame into the shared registry and tracking the
// WebSocketLinkState state machine. Send frames are Binary, matching the C#
// SendAsync (WebSocketMessageType.Binary, endOfMessage: true).
//
// Concurrency (Wave-1 lessons): the inbound stream is an unbounded channel — a
// frame delivered before any Receive consumer attaches is BUFFERED, never lost;
// fabric membership is snapshotted under the lock and the enqueue happens
// off-lock so a slow/(dis)connecting peer cannot deadlock the sender.

package circleai

import (
	"context"
	"errors"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// WebSocketLinkState — WebSocketTransportCommons.cs enum WebSocketLinkState
// ---------------------------------------------------------------------------

// WebSocketLinkState is the state machine of a WebSocket link. Ordinals match
// the C# declaration order exactly (Closed=0 .. Closed_Error=5).
type WebSocketLinkState int

const (
	// WebSocketLinkStateClosed — link closed (and the default).
	WebSocketLinkStateClosed WebSocketLinkState = iota
	// WebSocketLinkStateConnecting — handshake in progress.
	WebSocketLinkStateConnecting
	// WebSocketLinkStateOpen — link open.
	WebSocketLinkStateOpen
	// WebSocketLinkStateCloseSent — close frame sent, awaiting peer close.
	WebSocketLinkStateCloseSent
	// WebSocketLinkStateCloseReceived — peer close received.
	WebSocketLinkStateCloseReceived
	// WebSocketLinkStateClosedError — closed due to error (C# member Closed_Error).
	WebSocketLinkStateClosedError
)

// String renders the C# enum member name for a WebSocketLinkState (including the
// underscore in Closed_Error).
func (s WebSocketLinkState) String() string {
	switch s {
	case WebSocketLinkStateClosed:
		return "Closed"
	case WebSocketLinkStateConnecting:
		return "Connecting"
	case WebSocketLinkStateOpen:
		return "Open"
	case WebSocketLinkStateCloseSent:
		return "CloseSent"
	case WebSocketLinkStateCloseReceived:
		return "CloseReceived"
	case WebSocketLinkStateClosedError:
		return "Closed_Error"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// WebSocketMessageType — WebSocketTransportCommons.cs enum WebSocketMessageType
// ---------------------------------------------------------------------------

// WebSocketMessageType is the type of a WebSocket frame. Ordinals match the C#
// declaration order exactly.
type WebSocketMessageType int

const (
	// WebSocketMessageTypeText — a UTF-8 text frame.
	WebSocketMessageTypeText WebSocketMessageType = iota
	// WebSocketMessageTypeBinary — a binary frame.
	WebSocketMessageTypeBinary
	// WebSocketMessageTypePing — a ping control frame.
	WebSocketMessageTypePing
	// WebSocketMessageTypePong — a pong control frame.
	WebSocketMessageTypePong
	// WebSocketMessageTypeClose — a close control frame.
	WebSocketMessageTypeClose
)

// String renders the C# enum member name for a WebSocketMessageType.
func (m WebSocketMessageType) String() string {
	switch m {
	case WebSocketMessageTypeText:
		return "Text"
	case WebSocketMessageTypeBinary:
		return "Binary"
	case WebSocketMessageTypePing:
		return "Ping"
	case WebSocketMessageTypePong:
		return "Pong"
	case WebSocketMessageTypeClose:
		return "Close"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Records — endpoint descriptor, frame summary
// ---------------------------------------------------------------------------

// WebSocketEndpointDescriptor describes a WebSocket endpoint. Ports the C#
// `sealed record WebSocketEndpointDescriptor(Uri, Headers, PingInterval,
// Subprotocols)`. Uri is the string form; Headers is a copied bag (never shared
// with the caller); Subprotocols is the ordered subprotocol list.
type WebSocketEndpointDescriptor struct {
	Uri          string
	Headers      map[string]string
	PingInterval time.Duration
	Subprotocols []string
}

// WebSocketFrameSummary is a per-frame accounting record. Ports the C#
// `sealed record WebSocketFrameSummary(SessionId, Type, Bytes, AtUtc)`.
type WebSocketFrameSummary struct {
	SessionId string
	Type      WebSocketMessageType
	Bytes     int
	AtUtc     time.Time
}

// ---------------------------------------------------------------------------
// InMemoryWebSocketSessionRegistry — WebSocketTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryWebSocketSessionRegistry tracks endpoint descriptors, per-session link
// state, and a frame log. Ports the C# `InMemoryWebSocketSessionRegistry`. Safe
// for concurrent use.
type InMemoryWebSocketSessionRegistry struct {
	mu        sync.Mutex
	endpoints map[string]WebSocketEndpointDescriptor
	states    map[string]WebSocketLinkState
	frames    []WebSocketFrameSummary
}

// NewInMemoryWebSocketSessionRegistry constructs an empty registry.
func NewInMemoryWebSocketSessionRegistry() *InMemoryWebSocketSessionRegistry {
	return &InMemoryWebSocketSessionRegistry{
		endpoints: make(map[string]WebSocketEndpointDescriptor),
		states:    make(map[string]WebSocketLinkState),
	}
}

// Register records an endpoint descriptor under sessionId. Panics on empty
// sessionId (the C# ArgumentNullException guard is on the descriptor; an empty
// sessionId is the analogue).
func (r *InMemoryWebSocketSessionRegistry) Register(sessionId string, d WebSocketEndpointDescriptor) {
	if sessionId == "" {
		panic("websocket session requires sessionId")
	}
	r.mu.Lock()
	r.endpoints[sessionId] = d
	r.mu.Unlock()
}

// Get returns the descriptor for sessionId and true, or a zero value and false.
func (r *InMemoryWebSocketSessionRegistry) Get(sessionId string) (WebSocketEndpointDescriptor, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	d, ok := r.endpoints[sessionId]
	return d, ok
}

// SetState records the link state for sessionId.
func (r *InMemoryWebSocketSessionRegistry) SetState(sessionId string, s WebSocketLinkState) {
	r.mu.Lock()
	r.states[sessionId] = s
	r.mu.Unlock()
}

// State returns sessionId's link state, defaulting to Closed.
func (r *InMemoryWebSocketSessionRegistry) State(sessionId string) WebSocketLinkState {
	r.mu.Lock()
	defer r.mu.Unlock()
	if s, ok := r.states[sessionId]; ok {
		return s
	}
	return WebSocketLinkStateClosed
}

// RecordFrame appends a frame summary.
func (r *InMemoryWebSocketSessionRegistry) RecordFrame(f WebSocketFrameSummary) {
	r.mu.Lock()
	r.frames = append(r.frames, f)
	r.mu.Unlock()
}

// TotalBytes returns the sum of frame Bytes for sessionId (mirrors
// Where(...).Sum(f => (long)f.Bytes)).
func (r *InMemoryWebSocketSessionRegistry) TotalBytes(sessionId string) int64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	var sum int64
	for _, f := range r.frames {
		if f.SessionId == sessionId {
			sum += int64(f.Bytes)
		}
	}
	return sum
}

// FrameCount returns how many frames of the given type sessionId logged (mirrors
// Count(f => f.SessionId == sessionId && f.Type == type)).
func (r *InMemoryWebSocketSessionRegistry) FrameCount(sessionId string, t WebSocketMessageType) int {
	r.mu.Lock()
	defer r.mu.Unlock()
	var n int
	for _, f := range r.frames {
		if f.SessionId == sessionId && f.Type == t {
			n++
		}
	}
	return n
}

// ---------------------------------------------------------------------------
// WebSocketFabric — the injected in-memory WebSocket medium
// ---------------------------------------------------------------------------

// WebSocketFabric is the in-process substitute for the WebSocket transport.
// Transports built against the same fabric AND the same endpoint Uri share a
// link: a Send on one is delivered to every OTHER started transport with a
// matching Uri (loopback excluded), modelling a WebSocket relay/hub fanning a
// frame to connected peers. Carries the shared registry so link state / frame
// logs stay coherent.
type WebSocketFabric struct {
	// Registry is the shared endpoint/state/frame store.
	Registry *InMemoryWebSocketSessionRegistry

	mu      sync.Mutex
	members map[*WebSocketTransport]struct{}
}

// NewWebSocketFabric constructs a fabric with a fresh registry (or reg when
// non-nil).
func NewWebSocketFabric(reg *InMemoryWebSocketSessionRegistry) *WebSocketFabric {
	if reg == nil {
		reg = NewInMemoryWebSocketSessionRegistry()
	}
	return &WebSocketFabric{
		Registry: reg,
		members:  make(map[*WebSocketTransport]struct{}),
	}
}

func (f *WebSocketFabric) join(t *WebSocketTransport) {
	f.mu.Lock()
	f.members[t] = struct{}{}
	f.mu.Unlock()
}

func (f *WebSocketFabric) leave(t *WebSocketTransport) {
	f.mu.Lock()
	delete(f.members, t)
	f.mu.Unlock()
}

// peersOf snapshots the other started transports on the same Uri under the lock;
// delivery happens off-lock.
func (f *WebSocketFabric) peersOf(sender *WebSocketTransport) []*WebSocketTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]*WebSocketTransport, 0, len(f.members))
	for m := range f.members {
		if m != sender && m.endpoint == sender.endpoint {
			out = append(out, m)
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// WebSocketTransport — WebSocketTransport.cs
// ---------------------------------------------------------------------------

// WebSocketTransport is a full-duplex INetworkTransport backed by a shared
// WebSocketFabric. Kind() is TransportKindWebSocket; IsAvailable() reflects the
// Open link state (the C# `_ws?.State == WebSocketState.Open` gate). Start
// connects (Connecting -> Open) and joins the fabric; Send delivers the payload
// as a Binary frame to same-Uri peers and records a WebSocketFrameSummary; Stop
// sends a close (Open -> CloseSent -> Closed) and completes the inbound stream.
// Where the C# drives a ClientWebSocket, the Go port drives the in-memory fabric
// the rules require. Safe for concurrent use.
type WebSocketTransport struct {
	endpoint   string
	fabric     *WebSocketFabric
	sessionId  string
	descriptor WebSocketEndpointDescriptor

	mu      sync.Mutex
	open    bool
	inbound *unboundedChannel[NetworkPayload]
}

// NewWebSocketTransport builds a transport connecting to endpoint over fabric.
// fabric is required (the injected WebSocket medium); endpoint is the target Uri
// string (required, mirrors the C# `new Uri(endpoint)` — an empty endpoint is
// rejected). The sessionId defaults to the endpoint if empty.
func NewWebSocketTransport(endpoint string, fabric *WebSocketFabric) (*WebSocketTransport, error) {
	return NewWebSocketTransportWithSession(endpoint, fabric, endpoint)
}

// NewWebSocketTransportWithSession is NewWebSocketTransport with an explicit
// sessionId (so several transports on the same endpoint can be told apart in the
// frame registry).
func NewWebSocketTransportWithSession(endpoint string, fabric *WebSocketFabric, sessionId string) (*WebSocketTransport, error) {
	if fabric == nil {
		return nil, errors.New("websocket fabric required")
	}
	if endpoint == "" {
		return nil, errors.New("websocket endpoint required")
	}
	if sessionId == "" {
		sessionId = endpoint
	}
	desc := WebSocketEndpointDescriptor{
		Uri:          endpoint,
		Headers:      map[string]string{},
		PingInterval: 0,
		Subprotocols: []string{},
	}
	fabric.Registry.Register(sessionId, desc)
	fabric.Registry.SetState(sessionId, WebSocketLinkStateClosed)
	return &WebSocketTransport{
		endpoint:   endpoint,
		fabric:     fabric,
		sessionId:  sessionId,
		descriptor: desc,
		inbound:    newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindWebSocket.
func (t *WebSocketTransport) Kind() TransportKind { return TransportKindWebSocket }

// IsAvailable reports whether the link is Open (matches the C# Open-state gate).
func (t *WebSocketTransport) IsAvailable() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.open
}

// SessionId is the registry key for this transport's link — exposed for
// assertions/tooling.
func (t *WebSocketTransport) SessionId() string { return t.sessionId }

// Start connects the link (Connecting -> Open) and joins the fabric. Idempotent.
func (t *WebSocketTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.open {
		t.mu.Unlock()
		return nil
	}
	t.fabric.Registry.SetState(t.sessionId, WebSocketLinkStateConnecting)
	t.inbound = newUnboundedChannel[NetworkPayload]()
	t.open = true
	t.mu.Unlock()

	t.fabric.join(t)
	t.fabric.Registry.SetState(t.sessionId, WebSocketLinkStateOpen)
	return nil
}

// Stop closes the link (Open -> CloseSent -> Closed), leaves the fabric, and
// completes the inbound stream so active Receive streams drain and close.
// Idempotent. Mirrors the C# StopAsync (CloseAsync NormalClosure + TryComplete).
func (t *WebSocketTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.open {
		t.mu.Unlock()
		return nil
	}
	t.open = false
	inbound := t.inbound
	t.mu.Unlock()

	t.fabric.Registry.SetState(t.sessionId, WebSocketLinkStateCloseSent)
	t.fabric.Registry.RecordFrame(WebSocketFrameSummary{
		SessionId: t.sessionId,
		Type:      WebSocketMessageTypeClose,
		Bytes:     0,
		AtUtc:     time.Now().UTC(),
	})
	t.fabric.leave(t)
	inbound.Complete()
	t.fabric.Registry.SetState(t.sessionId, WebSocketLinkStateClosed)
	return nil
}

// Send delivers payload as a single Binary end-of-message frame to every
// same-Uri peer and records a WebSocketFrameSummary. Returns an error if the
// link is not open or ctx is cancelled. Mirrors the C# SendAsync (Binary,
// endOfMessage: true; ArgumentNullException when the socket is null i.e. not
// started).
func (t *WebSocketTransport) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	open := t.open
	t.mu.Unlock()
	if !open {
		return errors.New("websocket transport not open")
	}

	for _, peer := range t.fabric.peersOf(t) {
		peer.inbound.Write(payload)
	}
	t.fabric.Registry.RecordFrame(WebSocketFrameSummary{
		SessionId: t.sessionId,
		Type:      WebSocketMessageTypeBinary,
		Bytes:     len(payload.Data),
		AtUtc:     time.Now().UTC(),
	})
	return nil
}

// Receive returns a stream of inbound payloads. Frames delivered before this
// call are replayed first (unbounded buffering). The stream closes on ctx
// cancellation or Stop.
func (t *WebSocketTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbound := t.inbound
	t.mu.Unlock()
	return inbound.ReadAll(ctx)
}

var _ INetworkTransport = (*WebSocketTransport)(nil)
