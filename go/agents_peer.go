// agents_peer.go
//
// Ports CircleAI.Agents.Peer (agent-to-agent protocol over the Aether mesh):
//   PeerAgent.cs                -> PeerAgent, AgentCapability
//   AgentInvocationException.cs -> AgentInvocationError
//   IAgentPeerProtocol.cs       -> IAgentPeerProtocol
//   AgentBus.cs                 -> AgentBus
//   InMemoryAgentPeerProtocol.cs-> InMemoryAgentPeerProtocol
//
// AgentMessage / AgentMessageKind / CreateAgentMessage (AgentMessage.cs) are
// already declared in observer.go and are reused verbatim here — the flat
// package shares those wire types. The constants are AgentMessageDiscover …
// AgentMessageHeartbeat and the correlation field is CorrelationID.
//
// AgentBus is an in-process coordinator that lets several
// InMemoryAgentPeerProtocol instances behave like devices on a mesh, for tests
// and samples. Each registered peer gets an UNBOUNDED inbox (competing-consumer
// semantics: one reader — the peer's pump). Not a production transport.
//
// Concurrency: the pump goroutine subscribes to the bus inbox synchronously
// before it starts ranging. Pending invocations complete by sending on a
// buffered (cap-1) result channel — the pending map's lock is never held while
// a waiter runs — and the map is snapshotted before signalling.

package circleai

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// AgentCapability / PeerAgent
// ---------------------------------------------------------------------------

// AgentCapability is a capability advertised by a PeerAgent. Ports the
// AgentCapability record. CostCurrency defaults to "SDPKT" within the ecosystem.
type AgentCapability struct {
	// Name is the canonical capability name (e.g. "translate", "summarise").
	Name string

	// Version is the semantic version of the capability contract.
	Version string

	// CostPerInvocation is the cost in CostCurrency; zero means free.
	CostPerInvocation Decimal

	// CostCurrency is the currency code (default "SDPKT").
	CostCurrency string
}

// PeerAgent is a peer Circle AI agent discoverable over the Aether mesh. It
// describes WHO another CircleAI is and HOW to reach them. Ports the PeerAgent
// record.
type PeerAgent struct {
	// ID is the local handle for this peer (stable per discovery session).
	ID uuid.UUID

	// UhidIdentityId is the hashed UHID identity reference — the routing key.
	UhidIdentityId string

	// DisplayName is the user-chosen display label.
	DisplayName string

	// Capabilities are the capabilities this peer advertises.
	Capabilities []AgentCapability

	// PublicKeyDer is the DER-encoded P-256 public key from the peer's UhidKeyRing.
	PublicKeyDer []byte

	// CurrentTransportId is the transport carrying this peer, or nil when offline.
	CurrentTransportId *string

	// LastSeenAt is the UTC timestamp of the last message/heartbeat from this peer.
	LastSeenAt time.Time
}

// ---------------------------------------------------------------------------
// AgentInvocationError
// ---------------------------------------------------------------------------

// AgentInvocationError is returned by Invoke when a peer declines an invocation
// or otherwise fails to return a Response envelope. Ports
// AgentInvocationException.
type AgentInvocationError struct {
	// Message is the human-readable error message.
	Message string

	// PeerUhid is the peer that declined or errored, if known.
	PeerUhid string

	// DeclineMessage is the decline envelope returned by the peer, if any.
	DeclineMessage *AgentMessage
}

// Error implements the error interface.
func (e *AgentInvocationError) Error() string { return e.Message }

// ---------------------------------------------------------------------------
// IAgentPeerProtocol
// ---------------------------------------------------------------------------

// IAgentPeerProtocol is the agent-to-agent protocol over the Aether mesh. Every
// method must be safe to call from any goroutine. Ports the IAgentPeerProtocol
// interface.
type IAgentPeerProtocol interface {
	// DiscoverPeers listens for Discover broadcasts and returns every peer
	// observed during a short discovery window.
	DiscoverPeers(ctx context.Context) ([]PeerAgent, error)

	// Greet initiates a handshake with targetUhid. Returns the peer's record on
	// success, or nil when the peer is unreachable.
	Greet(ctx context.Context, targetUhid string) (*PeerAgent, error)

	// QueryCapabilities returns the capabilities targetUhid currently advertises.
	QueryCapabilities(ctx context.Context, targetUhid string) ([]AgentCapability, error)

	// Invoke invokes capability on targetUhid with requestPayload and awaits a
	// single Response envelope. Returns an *AgentInvocationError when the peer
	// declines or the call fails.
	Invoke(ctx context.Context, targetUhid string, capability AgentCapability, requestPayload []byte) (AgentMessage, error)

	// StreamInbox streams every inbound message addressed to this agent
	// (including broadcasts). The stream terminates when ctx is cancelled.
	StreamInbox(ctx context.Context) <-chan AgentMessage
}

// ---------------------------------------------------------------------------
// AgentBus
// ---------------------------------------------------------------------------

// AgentBus is an in-process bus used to simulate a mesh of CircleAI peers for
// tests and samples. It owns the peer registry and one unbounded inbox per
// registered peer. Not a production transport. Ports AgentBus.
type AgentBus struct {
	mu      sync.RWMutex
	peers   map[string]PeerAgent
	inboxes map[string]*unboundedChannel[AgentMessage]
}

// NewAgentBus creates an empty bus.
func NewAgentBus() *AgentBus {
	return &AgentBus{
		peers:   make(map[string]PeerAgent),
		inboxes: make(map[string]*unboundedChannel[AgentMessage]),
	}
}

// Register registers a peer on the bus and ensures its inbox exists.
// Re-registering with the same UHID replaces the prior record.
func (b *AgentBus) Register(peer PeerAgent) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.peers[peer.UhidIdentityId] = peer
	if _, ok := b.inboxes[peer.UhidIdentityId]; !ok {
		b.inboxes[peer.UhidIdentityId] = newUnboundedChannel[AgentMessage]()
	}
}

// Unregister removes uhid from the bus and completes its inbox so any active
// Receive stream terminates cleanly.
func (b *AgentBus) Unregister(uhid string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	delete(b.peers, uhid)
	if inbox, ok := b.inboxes[uhid]; ok {
		inbox.Complete()
		delete(b.inboxes, uhid)
	}
}

// TryGetPeer returns the latest record for uhid.
func (b *AgentBus) TryGetPeer(uhid string) (PeerAgent, bool) {
	b.mu.RLock()
	defer b.mu.RUnlock()
	p, ok := b.peers[uhid]
	return p, ok
}

// RegisteredPeers returns a snapshot of every peer currently on the bus.
func (b *AgentBus) RegisteredPeers() []PeerAgent {
	b.mu.RLock()
	defer b.mu.RUnlock()
	out := make([]PeerAgent, 0, len(b.peers))
	for _, p := range b.peers {
		out = append(out, p)
	}
	return out
}

// Send routes message to its recipient(s). A "*" ToUhid fans out to every
// registered inbox except the sender's. Messages for an unknown UHID are
// dropped silently (the peer is considered offline). Ports AgentBus.Send.
func (b *AgentBus) Send(message AgentMessage) {
	b.mu.RLock()
	defer b.mu.RUnlock()
	if message.ToUhid == "*" {
		for uhid, inbox := range b.inboxes {
			if uhid == message.FromUhid {
				continue
			}
			inbox.Write(message)
		}
		return
	}
	if inbox, ok := b.inboxes[message.ToUhid]; ok {
		inbox.Write(message)
	}
}

// Receive streams every envelope delivered to uhid's inbox. The stream
// terminates when the inbox is completed (via Unregister) or ctx fires. The
// inbox is created on demand if it does not yet exist. Ports AgentBus.Receive.
func (b *AgentBus) Receive(ctx context.Context, uhid string) <-chan AgentMessage {
	b.mu.Lock()
	inbox, ok := b.inboxes[uhid]
	if !ok {
		inbox = newUnboundedChannel[AgentMessage]()
		b.inboxes[uhid] = inbox
	}
	b.mu.Unlock()
	return inbox.ReadAll(ctx)
}

// ---------------------------------------------------------------------------
// InMemoryAgentPeerProtocol
// ---------------------------------------------------------------------------

// AgentSigner signs an outbound payload for InMemoryAgentPeerProtocol. Ports the
// C# signer delegate. When nil, outbound messages carry an empty signature.
type AgentSigner func(data []byte) []byte

// AgentCapabilityHandler handles an inbound Invoke. Returning a non-nil byte
// slice sends a Response; returning nil sends a Decline. Ports the C#
// capabilityHandler delegate.
type AgentCapabilityHandler func(capability AgentCapability, payload []byte) []byte

const (
	agentDiscoveryWindow = 50 * time.Millisecond
	agentInvokeTimeout   = 5 * time.Second
)

// InMemoryAgentPeerProtocol is an in-memory IAgentPeerProtocol backed by an
// AgentBus so multiple instances can simulate a mesh of peers. Ports
// InMemoryAgentPeerProtocol. Construct with NewInMemoryAgentPeerProtocol and
// release with Close.
type InMemoryAgentPeerProtocol struct {
	ownUhid           string
	bus               *AgentBus
	ownCapabilities   []AgentCapability
	ownPublicKey      []byte
	signer            AgentSigner
	capabilityHandler AgentCapabilityHandler

	mu       sync.Mutex
	lastSeen map[string]time.Time
	pending  map[uuid.UUID]chan AgentMessage

	runCtx    context.Context
	runCancel context.CancelFunc
	pumpDone  chan struct{}

	external *unboundedChannel[AgentMessage]

	disposeOnce sync.Once
}

// NewInMemoryAgentPeerProtocol creates a protocol instance, registers it on the
// bus, and starts pumping the inbox. ownUhid, bus, ownCapabilities and
// ownPublicKey are required. signer and capabilityHandler may be nil.
func NewInMemoryAgentPeerProtocol(
	ownUhid string,
	bus *AgentBus,
	ownCapabilities []AgentCapability,
	ownPublicKey []byte,
	signer AgentSigner,
	capabilityHandler AgentCapabilityHandler,
) *InMemoryAgentPeerProtocol {
	if ownUhid == "" {
		panic("ownUhid is required")
	}
	if bus == nil {
		panic("bus is required")
	}
	if ownCapabilities == nil {
		panic("ownCapabilities is required")
	}
	if ownPublicKey == nil {
		panic("ownPublicKey is required")
	}

	runCtx, runCancel := context.WithCancel(context.Background())
	p := &InMemoryAgentPeerProtocol{
		ownUhid:           ownUhid,
		bus:               bus,
		ownCapabilities:   ownCapabilities,
		ownPublicKey:      ownPublicKey,
		signer:            signer,
		capabilityHandler: capabilityHandler,
		lastSeen:          make(map[string]time.Time),
		pending:           make(map[uuid.UUID]chan AgentMessage),
		runCtx:            runCtx,
		runCancel:         runCancel,
		pumpDone:          make(chan struct{}),
		external:          newUnboundedChannel[AgentMessage](),
	}

	transport := "in-memory"
	bus.Register(PeerAgent{
		ID:                 uuid.New(),
		UhidIdentityId:     ownUhid,
		DisplayName:        ownUhid,
		Capabilities:       ownCapabilities,
		PublicKeyDer:       ownPublicKey,
		CurrentTransportId: &transport,
		LastSeenAt:         time.Now().UTC(),
	})

	// Subscribe to the bus inbox synchronously BEFORE spawning the consumer so
	// no message sent between Register and the pump start is missed.
	inbox := bus.Receive(runCtx, ownUhid)
	go p.pump(inbox)

	return p
}

// OwnUhid returns the UHID identity owned by this agent.
func (p *InMemoryAgentPeerProtocol) OwnUhid() string { return p.ownUhid }

// DiscoverPeers broadcasts a Discover, waits a brief window, then returns every
// registered peer except itself (with refreshed LastSeen).
func (p *InMemoryAgentPeerProtocol) DiscoverPeers(ctx context.Context) ([]PeerAgent, error) {
	announcement := CreateAgentMessage(AgentMessageDiscover, p.ownUhid, "*", "application/json", []byte{}, p.sign([]byte{}), "")
	p.bus.Send(announcement)

	// Brief listen window; either the window elapses or the caller cancels.
	select {
	case <-time.After(agentDiscoveryWindow):
	case <-ctx.Done():
	}

	var out []PeerAgent
	for _, peer := range p.bus.RegisteredPeers() {
		if peer.UhidIdentityId == p.ownUhid {
			continue
		}
		out = append(out, p.withLastSeen(peer))
	}
	return out, nil
}

// Greet initiates a handshake with targetUhid, returning its record or nil.
func (p *InMemoryAgentPeerProtocol) Greet(ctx context.Context, targetUhid string) (*PeerAgent, error) {
	if targetUhid == "" {
		return nil, errors.New("targetUhid is required")
	}
	peer, ok := p.bus.TryGetPeer(targetUhid)
	if !ok {
		return nil, nil
	}
	greet := CreateAgentMessage(AgentMessageGreet, p.ownUhid, targetUhid, "application/json", []byte{}, p.sign([]byte{}), targetUhid)
	p.bus.Send(greet)
	out := p.withLastSeen(peer)
	return &out, nil
}

// QueryCapabilities returns the capabilities targetUhid advertises, or an empty
// slice when the peer is unknown.
func (p *InMemoryAgentPeerProtocol) QueryCapabilities(ctx context.Context, targetUhid string) ([]AgentCapability, error) {
	if targetUhid == "" {
		return nil, errors.New("targetUhid is required")
	}
	peer, ok := p.bus.TryGetPeer(targetUhid)
	if !ok {
		return []AgentCapability{}, nil
	}
	return peer.Capabilities, nil
}

// Invoke sends an Invoke to targetUhid and awaits a single Response, timing out
// after 5s. Returns an *AgentInvocationError when the peer is unreachable,
// declines, or the call times out.
func (p *InMemoryAgentPeerProtocol) Invoke(ctx context.Context, targetUhid string, capability AgentCapability, requestPayload []byte) (AgentMessage, error) {
	if targetUhid == "" {
		return AgentMessage{}, errors.New("targetUhid is required")
	}
	if requestPayload == nil {
		return AgentMessage{}, errors.New("requestPayload is required")
	}
	if _, ok := p.bus.TryGetPeer(targetUhid); !ok {
		return AgentMessage{}, &AgentInvocationError{
			Message:  "Peer '" + targetUhid + "' is not reachable on the current transport.",
			PeerUhid: targetUhid,
		}
	}

	invoke := CreateAgentMessage(AgentMessageInvoke, p.ownUhid, targetUhid, "application/octet-stream", requestPayload, p.sign(requestPayload), targetUhid)

	// Buffered (cap 1) so the pump can complete the invocation without blocking
	// and without holding the pending-map lock while this waiter runs.
	replyCh := make(chan AgentMessage, 1)
	p.mu.Lock()
	p.pending[invoke.ID] = replyCh
	p.mu.Unlock()

	p.bus.Send(invoke)

	defer func() {
		p.mu.Lock()
		delete(p.pending, invoke.ID)
		p.mu.Unlock()
	}()

	select {
	case reply := <-replyCh:
		if reply.Kind == AgentMessageDecline {
			r := reply
			return AgentMessage{}, &AgentInvocationError{
				Message:        "Peer '" + targetUhid + "' declined '" + capability.Name + "'.",
				PeerUhid:       targetUhid,
				DeclineMessage: &r,
			}
		}
		return reply, nil
	case <-time.After(agentInvokeTimeout):
		return AgentMessage{}, &AgentInvocationError{
			Message:  "Invocation of '" + capability.Name + "' on peer '" + targetUhid + "' timed out.",
			PeerUhid: targetUhid,
		}
	case <-ctx.Done():
		return AgentMessage{}, ctx.Err()
	case <-p.runCtx.Done():
		return AgentMessage{}, &AgentInvocationError{
			Message:  "Invocation of '" + capability.Name + "' on peer '" + targetUhid + "' was aborted (protocol disposed).",
			PeerUhid: targetUhid,
		}
	}
}

// StreamInbox streams every inbound message surfaced to external consumers. The
// stream terminates when ctx is cancelled or the protocol is closed.
func (p *InMemoryAgentPeerProtocol) StreamInbox(ctx context.Context) <-chan AgentMessage {
	return p.external.ReadAll(ctx)
}

// Close tears down the protocol, unregisters from the bus, and stops the pump.
// Idempotent.
func (p *InMemoryAgentPeerProtocol) Close() error {
	p.disposeOnce.Do(func() {
		p.runCancel()
		select {
		case <-p.pumpDone:
		case <-time.After(time.Second):
		}
		p.bus.Unregister(p.ownUhid)
		p.external.Complete()
	})
	return nil
}

// pump consumes the bus inbox, updates last-seen, routes invocations/responses,
// and surfaces every message to external consumers.
func (p *InMemoryAgentPeerProtocol) pump(inbox <-chan AgentMessage) {
	defer close(p.pumpDone)
	for {
		select {
		case <-p.runCtx.Done():
			return
		case message, ok := <-inbox:
			if !ok {
				return
			}
			p.mu.Lock()
			p.lastSeen[message.FromUhid] = message.SentAt
			p.mu.Unlock()
			p.handleIncoming(message)
		}
	}
}

func (p *InMemoryAgentPeerProtocol) handleIncoming(message AgentMessage) {
	switch message.Kind {
	case AgentMessageResponse, AgentMessageDecline:
		p.completePending(message)
	case AgentMessageInvoke:
		p.routeInvoke(message)
	}
	// Every inbound message is also surfaced to external consumers.
	p.external.Write(message)
}

// completePending correlates a Response/Decline to its Invoke via the first 16
// payload bytes and hands the reply to the waiter. The pending-map lock is
// released before the (buffered) send, so no waiter runs under the lock.
func (p *InMemoryAgentPeerProtocol) completePending(message AgentMessage) {
	if len(message.Payload) < 16 {
		return
	}
	var id uuid.UUID
	copy(id[:], message.Payload[:16])

	p.mu.Lock()
	ch := p.pending[id]
	p.mu.Unlock()
	if ch != nil {
		// Buffered cap-1; the waiter deletes the entry, so a single send lands.
		select {
		case ch <- message:
		default:
		}
	}
}

// routeInvoke runs the capability handler and replies with Response or Decline.
// The response payload is prefixed with the Invoke's 16-byte ID for correlation.
func (p *InMemoryAgentPeerProtocol) routeInvoke(invoke AgentMessage) {
	if p.capabilityHandler == nil {
		return
	}

	// The in-memory mock hands the first advertised capability to the handler.
	var capability AgentCapability
	if len(p.ownCapabilities) > 0 {
		capability = p.ownCapabilities[0]
	} else {
		capability = AgentCapability{Name: "unknown", Version: "0.0.0", CostPerInvocation: DecimalFromInt(0), CostCurrency: "SDPKT"}
	}

	var result []byte
	func() {
		defer func() {
			if r := recover(); r != nil {
				result = nil
			}
		}()
		result = p.capabilityHandler(capability, invoke.Payload)
	}()

	correlationPrefix := invoke.ID[:]

	if result == nil {
		payload := make([]byte, len(correlationPrefix))
		copy(payload, correlationPrefix)
		decline := CreateAgentMessage(AgentMessageDecline, p.ownUhid, invoke.FromUhid, "application/octet-stream", payload, p.sign(payload), "")
		p.bus.Send(decline)
		return
	}

	responsePayload := make([]byte, len(correlationPrefix)+len(result))
	copy(responsePayload, correlationPrefix)
	copy(responsePayload[len(correlationPrefix):], result)

	response := CreateAgentMessage(AgentMessageResponse, p.ownUhid, invoke.FromUhid, "application/octet-stream", responsePayload, p.sign(responsePayload), "")
	p.bus.Send(response)
}

func (p *InMemoryAgentPeerProtocol) sign(data []byte) []byte {
	if p.signer == nil {
		return []byte{}
	}
	return p.signer(data)
}

func (p *InMemoryAgentPeerProtocol) withLastSeen(peer PeerAgent) PeerAgent {
	p.mu.Lock()
	ts, ok := p.lastSeen[peer.UhidIdentityId]
	p.mu.Unlock()
	out := peer
	if ok {
		out.LastSeenAt = ts
	}
	return out
}

var _ IAgentPeerProtocol = (*InMemoryAgentPeerProtocol)(nil)
