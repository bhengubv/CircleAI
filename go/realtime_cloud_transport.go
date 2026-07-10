// realtime_cloud_transport.go
//
// Ports the CircleAI.Realtime.Cloud host-supplied transport surface
// (IRealtimeTransport.cs):
//   IRealtimeTransport            -> IRealtimeTransport interface
//   IRealtimeTransportFactory     -> IRealtimeTransportFactory interface
//   NullRealtimeTransportFactory  -> NullRealtimeTransportFactory
//
// The C# reference is framework-free — it ships only the null factory and lets
// the ASP.NET / native host wire a real ClientWebSocket. The Go port additionally
// provides a deterministic in-memory transport (InMemoryRealtimeTransport) and a
// matching factory (InMemoryRealtimeTransportFactory) so the contract has a real,
// hermetic, no-network implementation (per the no-stubs rule). Nothing here dials
// a socket.
//
// STREAMS: C# IAsyncEnumerable<T> -> a <-chan T returned from a method taking a
// context; the channel closes when the transport is closed or ctx cancels.
// IAsyncDisposable -> Close(ctx).
//
// CONCURRENCY (this wave is stream/transport-heavy): the in-memory transport
// backs each receive direction with the package's unboundedChannel[T], so a
// frame sent before the peer starts receiving is BUFFERED, not dropped — the
// unbounded C# Channel it emulates retains writes until read. ReceiveText/
// ReceiveBinary subscribe synchronously (ReadAll spawns its reader goroutine
// before returning), so there is no message-lost-before-subscribe race. Close is
// idempotent and completes both of this endpoint's inbound streams; the paired
// endpoint keeps its own streams and observes IsOpen=false via the shared flag.

package circleai

import (
	"context"
	"errors"
	"net/url"
	"sync"
)

// IRealtimeTransport is a WebSocket-style transport for a realtime session.
// Ports IRealtimeTransport (IAsyncDisposable -> Close). SendText/SendBinary push
// one frame; ReceiveText/ReceiveBinary stream inbound frames until Close or ctx
// cancels.
type IRealtimeTransport interface {
	// SendText sends one JSON text frame.
	SendText(ctx context.Context, text string) error
	// SendBinary sends one binary frame.
	SendBinary(ctx context.Context, bytes []byte) error
	// ReceiveText streams incoming text frames.
	ReceiveText(ctx context.Context) <-chan string
	// ReceiveBinary streams incoming binary frames.
	ReceiveBinary(ctx context.Context) <-chan []byte
	// CloseConn closes the connection cleanly (ports CloseAsync).
	CloseConn(ctx context.Context) error
	// IsOpen is true while the underlying socket is open.
	IsOpen() bool
	// Close disposes the transport (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// IRealtimeTransportFactory produces transports for a given endpoint. Ports
// IRealtimeTransportFactory.
type IRealtimeTransportFactory interface {
	// Connect connects to endpoint with the given headers (nil = none) and
	// returns an open transport.
	Connect(ctx context.Context, endpoint *url.URL, headers map[string]string) (IRealtimeTransport, error)
}

// ---------------------------------------------------------------------------
// NullRealtimeTransportFactory (IRealtimeTransport.cs)
// ---------------------------------------------------------------------------

// NullRealtimeTransportFactory throws on Connect — the host wires the real one.
// Ports NullRealtimeTransportFactory. Use NullRealtimeTransportFactoryInstance
// for the singleton.
type NullRealtimeTransportFactory struct{}

// NullRealtimeTransportFactoryInstance is the shared singleton (ports the C#
// static readonly Instance).
var NullRealtimeTransportFactoryInstance = NullRealtimeTransportFactory{}

// Connect always errors "no factory registered". Ports the C#
// InvalidOperationException.
func (NullRealtimeTransportFactory) Connect(_ context.Context, _ *url.URL, _ map[string]string) (IRealtimeTransport, error) {
	return nil, errors.New("No IRealtimeTransportFactory is registered. Add the host package that provides a real ClientWebSocket-based factory.")
}

// ---------------------------------------------------------------------------
// InMemoryRealtimeTransport — deterministic hermetic transport
// ---------------------------------------------------------------------------

// InMemoryRealtimeTransport is a deterministic, no-network IRealtimeTransport.
// It is one endpoint of a bidirectional in-memory pipe: text/binary sent on this
// endpoint arrive on the paired endpoint's Receive streams and vice versa,
// modelling a duplex WebSocket without any socket. Both endpoints share an open
// flag so either side closing flips IsOpen for both. Constructed in pairs via
// NewInMemoryRealtimeTransportPair.
type InMemoryRealtimeTransport struct {
	// outbound streams feed the PEER's receive channels.
	outboundText   *unboundedChannel[string]
	outboundBinary *unboundedChannel[[]byte]
	// inbound streams are what THIS endpoint receives (fed by the peer).
	inboundText   *unboundedChannel[string]
	inboundBinary *unboundedChannel[[]byte]

	shared *transportShared
}

// transportShared holds the open/closed state common to both paired endpoints.
type transportShared struct {
	mu     sync.Mutex
	open   bool
	closed bool
}

// NewInMemoryRealtimeTransportPair constructs two connected endpoints (a, b).
// Anything a sends, b receives, and vice versa. Both start open; closing either
// endpoint marks the pair closed and completes both endpoints' inbound streams.
func NewInMemoryRealtimeTransportPair() (a, b *InMemoryRealtimeTransport) {
	aToB := newUnboundedChannel[string]()
	bToA := newUnboundedChannel[string]()
	aToBBin := newUnboundedChannel[[]byte]()
	bToABin := newUnboundedChannel[[]byte]()
	shared := &transportShared{open: true}

	a = &InMemoryRealtimeTransport{
		outboundText: aToB, outboundBinary: aToBBin,
		inboundText: bToA, inboundBinary: bToABin,
		shared: shared,
	}
	b = &InMemoryRealtimeTransport{
		outboundText: bToA, outboundBinary: bToABin,
		inboundText: aToB, inboundBinary: aToBBin,
		shared: shared,
	}
	return a, b
}

// SendText delivers text to the peer's ReceiveText stream. Errors when the
// transport is closed.
func (t *InMemoryRealtimeTransport) SendText(ctx context.Context, text string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if !t.IsOpen() {
		return errors.New("transport is closed")
	}
	t.outboundText.Write(text)
	return nil
}

// SendBinary delivers a copy of bytes to the peer's ReceiveBinary stream. Errors
// when the transport is closed. The payload is copied so a caller mutating its
// buffer after Send cannot corrupt an already-queued frame.
func (t *InMemoryRealtimeTransport) SendBinary(ctx context.Context, bytes []byte) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if !t.IsOpen() {
		return errors.New("transport is closed")
	}
	cp := append([]byte(nil), bytes...)
	t.outboundBinary.Write(cp)
	return nil
}

// ReceiveText streams text frames sent by the peer until the transport closes or
// ctx cancels. Frames queued before the first call are buffered, not lost.
func (t *InMemoryRealtimeTransport) ReceiveText(ctx context.Context) <-chan string {
	return t.inboundText.ReadAll(ctx)
}

// ReceiveBinary streams binary frames sent by the peer until the transport
// closes or ctx cancels. Frames queued before the first call are buffered.
func (t *InMemoryRealtimeTransport) ReceiveBinary(ctx context.Context) <-chan []byte {
	return t.inboundBinary.ReadAll(ctx)
}

// CloseConn closes the connection cleanly (ports CloseAsync). Same effect as
// Close for the in-memory transport.
func (t *InMemoryRealtimeTransport) CloseConn(ctx context.Context) error {
	return t.Close(ctx)
}

// IsOpen reports whether the pair is still open.
func (t *InMemoryRealtimeTransport) IsOpen() bool {
	t.shared.mu.Lock()
	defer t.shared.mu.Unlock()
	return t.shared.open
}

// Close marks the pair closed and completes THIS endpoint's inbound streams so
// in-flight receivers drain then finish. Idempotent (ports DisposeAsync). The
// peer's inbound streams are completed when it closes; marking the shared flag
// flips IsOpen to false for both endpoints immediately.
func (t *InMemoryRealtimeTransport) Close(_ context.Context) error {
	t.shared.mu.Lock()
	already := t.shared.closed
	t.shared.closed = true
	t.shared.open = false
	t.shared.mu.Unlock()
	if already {
		return nil
	}
	// Complete this endpoint's inbound streams (what this side reads). The peer
	// completing its own Close finishes the other direction. Completing our
	// outbound streams too lets a peer still draining them observe end-of-stream.
	t.inboundText.Complete()
	t.inboundBinary.Complete()
	t.outboundText.Complete()
	t.outboundBinary.Complete()
	return nil
}

// InMemoryRealtimeTransportFactory hands out one endpoint of a fresh in-memory
// pair per Connect and retains the peer endpoint for the test/host to drive.
// This is the deterministic, hermetic factory (no network); the null factory
// stands in for a real ClientWebSocket host.
type InMemoryRealtimeTransportFactory struct {
	mu    sync.Mutex
	peers []*InMemoryRealtimeTransport
}

// NewInMemoryRealtimeTransportFactory constructs an empty factory.
func NewInMemoryRealtimeTransportFactory() *InMemoryRealtimeTransportFactory {
	return &InMemoryRealtimeTransportFactory{}
}

// Connect returns the "client" endpoint of a new pair; the paired "server"
// endpoint is retained and retrievable via LastPeer. endpoint/headers are
// accepted for signature parity and do not affect the in-memory pipe. endpoint
// must be non-nil (ports the C# contract that requires a target Uri).
func (f *InMemoryRealtimeTransportFactory) Connect(ctx context.Context, endpoint *url.URL, _ map[string]string) (IRealtimeTransport, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if endpoint == nil {
		return nil, errors.New("endpoint required")
	}
	client, server := NewInMemoryRealtimeTransportPair()
	f.mu.Lock()
	f.peers = append(f.peers, server)
	f.mu.Unlock()
	return client, nil
}

// LastPeer returns the server endpoint paired with the most recent Connect, and
// true, or (nil, false) if Connect was never called. Lets a caller inject frames
// as the "vendor" side.
func (f *InMemoryRealtimeTransportFactory) LastPeer() (*InMemoryRealtimeTransport, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if len(f.peers) == 0 {
		return nil, false
	}
	return f.peers[len(f.peers)-1], true
}

// Interface guards.
var (
	_ IRealtimeTransportFactory = NullRealtimeTransportFactory{}
	_ IRealtimeTransport        = (*InMemoryRealtimeTransport)(nil)
	_ IRealtimeTransportFactory = (*InMemoryRealtimeTransportFactory)(nil)
)
