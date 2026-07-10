// network_tcp.go
//
// Ports CircleAI.Networking.Tcp:
//   TcpTransportCommons.cs  -> TcpConnectionState, TcpEndpointDescriptor,
//                              TcpThroughputSample, TcpKnownPorts,
//                              InMemoryTcpConnectionRegistry
//   TcpNetworkTransport.cs  -> TcpNetworkTransport (INetworkTransport)
//
// The C# TcpNetworkTransport speaks raw TCP: it acts as client when a remote
// endpoint is set and as a listener when only a listen port is set, framing each
// payload as a 4-byte little-endian length prefix followed by the data bytes
// (BitConverter.GetBytes(len) then data; the pump reads the length then reads
// exactly that many bytes). Per the porting rules (NO stubs — every contract
// gets a working deterministic implementation), the Go port replaces the OS
// socket with a shared in-memory TcpFabric so two transports pointed at the same
// endpoint converge, while preserving the exact length-prefixed wire format via
// FrameTcpPayload / ParseTcpFrame so the byte layout is faithfully ported and
// testable. Throughput is accounted into the shared registry.
//
// Concurrency (Wave-1 lessons): the inbound stream is an unbounded channel — a
// frame delivered before any Receive consumer attaches is BUFFERED, never lost;
// fabric membership is snapshotted under the lock and the enqueue happens
// off-lock so a slow/(dis)connecting peer cannot deadlock the sender.

package circleai

import (
	"context"
	"encoding/binary"
	"errors"
	"strconv"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// TcpConnectionState — TcpTransportCommons.cs enum TcpConnectionState
// ---------------------------------------------------------------------------

// TcpConnectionState is the lifecycle state of a TCP connection. Ordinals match
// the C# declaration order exactly.
type TcpConnectionState int

const (
	// TcpConnectionStateDisconnected — no connection (and the default).
	TcpConnectionStateDisconnected TcpConnectionState = iota
	// TcpConnectionStateConnecting — establishing the connection.
	TcpConnectionStateConnecting
	// TcpConnectionStateConnected — connection is up.
	TcpConnectionStateConnected
	// TcpConnectionStateClosing — connection is closing.
	TcpConnectionStateClosing
	// TcpConnectionStateFailed — connection failed.
	TcpConnectionStateFailed
)

// String renders the C# enum member name for a TcpConnectionState.
func (s TcpConnectionState) String() string {
	switch s {
	case TcpConnectionStateDisconnected:
		return "Disconnected"
	case TcpConnectionStateConnecting:
		return "Connecting"
	case TcpConnectionStateConnected:
		return "Connected"
	case TcpConnectionStateClosing:
		return "Closing"
	case TcpConnectionStateFailed:
		return "Failed"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Records — endpoint descriptor, throughput sample
// ---------------------------------------------------------------------------

// TcpEndpointDescriptor describes a TCP endpoint and its socket options. Ports
// the C# `sealed record TcpEndpointDescriptor(Host, Port, NoDelay, KeepAlive,
// ConnectTimeout)`.
type TcpEndpointDescriptor struct {
	Host           string
	Port           int
	NoDelay        bool
	KeepAlive      bool
	ConnectTimeout time.Duration
}

// TcpThroughputSample is a per-endpoint byte-count measurement. Ports the C#
// `sealed record TcpThroughputSample(EndpointId, BytesSent, BytesReceived,
// AtUtc)`.
type TcpThroughputSample struct {
	EndpointId    string
	BytesSent     int64
	BytesReceived int64
	AtUtc         time.Time
}

// ---------------------------------------------------------------------------
// TcpKnownPorts — TcpTransportCommons.cs static TcpKnownPorts
// ---------------------------------------------------------------------------

// TcpKnownPorts mirrors the C# static TcpKnownPorts constants (well-known TCP
// service ports).
const (
	TcpPortHttp    = 80
	TcpPortHttps   = 443
	TcpPortSsh     = 22
	TcpPortSmtp    = 25
	TcpPortImap    = 143
	TcpPortImapSsl = 993
	TcpPortPop3    = 110
	TcpPortPop3Ssl = 995
	TcpPortMqtt    = 1883
	TcpPortMqttSsl = 8883
)

// ---------------------------------------------------------------------------
// TCP framing — TcpNetworkTransport.cs wire format
// ---------------------------------------------------------------------------

// FrameTcpPayload encodes data as the C# TcpNetworkTransport wire frame: a
// 4-byte little-endian length prefix (BitConverter.GetBytes(int) is
// little-endian on all supported runtimes) followed by the data bytes. Exposed
// so the exact byte layout is testable independent of the fabric.
func FrameTcpPayload(data []byte) []byte {
	out := make([]byte, 4+len(data))
	binary.LittleEndian.PutUint32(out[:4], uint32(len(data)))
	copy(out[4:], data)
	return out
}

// ParseTcpFrame decodes one length-prefixed frame produced by FrameTcpPayload,
// returning the data bytes and the total number of bytes consumed (4 + len). It
// mirrors the C# pump: read the 4-byte length, then read exactly that many data
// bytes. Returns an error if buf is too short for the prefix or the declared
// body.
func ParseTcpFrame(buf []byte) (data []byte, consumed int, err error) {
	if len(buf) < 4 {
		return nil, 0, errors.New("tcp frame truncated: missing length prefix")
	}
	n := int(binary.LittleEndian.Uint32(buf[:4]))
	if n < 0 || len(buf) < 4+n {
		return nil, 0, errors.New("tcp frame truncated: body shorter than declared length")
	}
	out := make([]byte, n)
	copy(out, buf[4:4+n])
	return out, 4 + n, nil
}

// ---------------------------------------------------------------------------
// InMemoryTcpConnectionRegistry — TcpTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryTcpConnectionRegistry tracks endpoint descriptors, per-endpoint
// connection state, and throughput samples. Ports the C#
// `InMemoryTcpConnectionRegistry`. Safe for concurrent use.
type InMemoryTcpConnectionRegistry struct {
	mu         sync.Mutex
	endpoints  map[string]TcpEndpointDescriptor
	states     map[string]TcpConnectionState
	throughput []TcpThroughputSample
}

// NewInMemoryTcpConnectionRegistry constructs an empty registry.
func NewInMemoryTcpConnectionRegistry() *InMemoryTcpConnectionRegistry {
	return &InMemoryTcpConnectionRegistry{
		endpoints: make(map[string]TcpEndpointDescriptor),
		states:    make(map[string]TcpConnectionState),
	}
}

// Register records an endpoint descriptor under id. Panics on empty id (the C#
// ArgumentNullException guard is on the descriptor; an empty id is the analogue).
func (r *InMemoryTcpConnectionRegistry) Register(id string, d TcpEndpointDescriptor) {
	if id == "" {
		panic("tcp endpoint requires id")
	}
	r.mu.Lock()
	r.endpoints[id] = d
	r.mu.Unlock()
}

// Get returns the descriptor for id and true, or a zero value and false.
func (r *InMemoryTcpConnectionRegistry) Get(id string) (TcpEndpointDescriptor, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	d, ok := r.endpoints[id]
	return d, ok
}

// SetState records the connection state for id.
func (r *InMemoryTcpConnectionRegistry) SetState(id string, s TcpConnectionState) {
	r.mu.Lock()
	r.states[id] = s
	r.mu.Unlock()
}

// State returns id's connection state, defaulting to Disconnected.
func (r *InMemoryTcpConnectionRegistry) State(id string) TcpConnectionState {
	r.mu.Lock()
	defer r.mu.Unlock()
	if s, ok := r.states[id]; ok {
		return s
	}
	return TcpConnectionStateDisconnected
}

// RecordSample appends a throughput sample.
func (r *InMemoryTcpConnectionRegistry) RecordSample(s TcpThroughputSample) {
	r.mu.Lock()
	r.throughput = append(r.throughput, s)
	r.mu.Unlock()
}

// TotalBytesSent returns the sum of BytesSent across id's samples (mirrors
// Where(...).Sum(t => t.BytesSent)).
func (r *InMemoryTcpConnectionRegistry) TotalBytesSent(id string) int64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	var sum int64
	for _, t := range r.throughput {
		if t.EndpointId == id {
			sum += t.BytesSent
		}
	}
	return sum
}

// ---------------------------------------------------------------------------
// TcpFabric — the injected in-memory TCP medium
// ---------------------------------------------------------------------------

// TcpFabric is the in-process substitute for the TCP transport. Transports built
// against the same fabric AND the same endpoint key (host:port) share a
// connection: a Send on one is delivered to every OTHER started transport with a
// matching endpoint (loopback excluded), modelling a connected socket pair /
// listener accepting peers. Carries the shared registry so state / throughput
// stay coherent.
type TcpFabric struct {
	// Registry is the shared endpoint/state/throughput store.
	Registry *InMemoryTcpConnectionRegistry

	mu      sync.Mutex
	members map[*TcpNetworkTransport]struct{}
}

// NewTcpFabric constructs a fabric with a fresh registry (or reg when non-nil).
func NewTcpFabric(reg *InMemoryTcpConnectionRegistry) *TcpFabric {
	if reg == nil {
		reg = NewInMemoryTcpConnectionRegistry()
	}
	return &TcpFabric{
		Registry: reg,
		members:  make(map[*TcpNetworkTransport]struct{}),
	}
}

func (f *TcpFabric) join(t *TcpNetworkTransport) {
	f.mu.Lock()
	f.members[t] = struct{}{}
	f.mu.Unlock()
}

func (f *TcpFabric) leave(t *TcpNetworkTransport) {
	f.mu.Lock()
	delete(f.members, t)
	f.mu.Unlock()
}

// peersOf snapshots the other started transports on the same endpoint key under
// the lock; delivery happens off-lock.
func (f *TcpFabric) peersOf(sender *TcpNetworkTransport) []*TcpNetworkTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]*TcpNetworkTransport, 0, len(f.members))
	for m := range f.members {
		if m != sender && m.endpointKey == sender.endpointKey {
			out = append(out, m)
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// TcpNetworkTransport — TcpNetworkTransport.cs
// ---------------------------------------------------------------------------

// TcpNetworkTransport is an INetworkTransport over raw TCP, backed by a shared
// TcpFabric. Kind() is TransportKindTcp; IsAvailable() reflects the connected
// state (the C# `_client?.Connected` gate). Start connects (client) / listens
// (server) and joins the fabric; Send frames the payload with the 4-byte length
// prefix, delivers it to same-endpoint peers (which decode the frame back into a
// payload), and records a TcpThroughputSample; Stop closes and completes the
// inbound stream. Where the C# drives OS sockets, the Go port drives the
// in-memory fabric the rules require. Safe for concurrent use.
type TcpNetworkTransport struct {
	descriptor  TcpEndpointDescriptor
	fabric      *TcpFabric
	endpointKey string
	// isClient mirrors the C# client-vs-listener distinction (remote set vs
	// listen-only). Both roles converge on the same endpoint key on the fabric.
	isClient bool

	mu        sync.Mutex
	connected bool
	inbound   *unboundedChannel[NetworkPayload]
}

// NewTcpClientTransport builds a client transport connecting to descriptor's
// host:port. fabric is required. Mirrors the C# ctor with a non-null remote
// endpoint.
func NewTcpClientTransport(descriptor TcpEndpointDescriptor, fabric *TcpFabric) (*TcpNetworkTransport, error) {
	return newTcpTransport(descriptor, fabric, true)
}

// NewTcpListenerTransport builds a listener transport bound to descriptor's port.
// fabric is required. Mirrors the C# ctor with only a listen port. The
// descriptor's Host is typically "" / "0.0.0.0"; the endpoint key is derived
// from Host:Port either way.
func NewTcpListenerTransport(descriptor TcpEndpointDescriptor, fabric *TcpFabric) (*TcpNetworkTransport, error) {
	return newTcpTransport(descriptor, fabric, false)
}

func newTcpTransport(descriptor TcpEndpointDescriptor, fabric *TcpFabric, isClient bool) (*TcpNetworkTransport, error) {
	if fabric == nil {
		return nil, errors.New("tcp fabric required")
	}
	if descriptor.Port <= 0 {
		return nil, errors.New("tcp endpoint requires a positive Port")
	}
	key := descriptor.Host + ":" + strconv.Itoa(descriptor.Port)
	fabric.Registry.Register(key, descriptor)
	fabric.Registry.SetState(key, TcpConnectionStateDisconnected)
	return &TcpNetworkTransport{
		descriptor:  descriptor,
		fabric:      fabric,
		endpointKey: key,
		isClient:    isClient,
		inbound:     newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindTcp.
func (t *TcpNetworkTransport) Kind() TransportKind { return TransportKindTcp }

// IsAvailable reports whether the transport is connected (matches the C#
// `_client?.Connected ?? false`).
func (t *TcpNetworkTransport) IsAvailable() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.connected
}

// EndpointKey is the host:port key that scopes this transport's connection on
// the fabric — exposed for assertions/tooling.
func (t *TcpNetworkTransport) EndpointKey() string { return t.endpointKey }

// Start connects (client) or begins listening (server) and joins the fabric,
// marking the endpoint Connected. Idempotent.
func (t *TcpNetworkTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.connected {
		t.mu.Unlock()
		return nil
	}
	t.fabric.Registry.SetState(t.endpointKey, TcpConnectionStateConnecting)
	t.inbound = newUnboundedChannel[NetworkPayload]()
	t.connected = true
	t.mu.Unlock()

	t.fabric.join(t)
	t.fabric.Registry.SetState(t.endpointKey, TcpConnectionStateConnected)
	return nil
}

// Stop closes the connection, leaves the fabric, and completes the inbound
// stream so active Receive streams drain and close. Idempotent.
func (t *TcpNetworkTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.connected {
		t.mu.Unlock()
		return nil
	}
	t.connected = false
	inbound := t.inbound
	t.mu.Unlock()

	t.fabric.Registry.SetState(t.endpointKey, TcpConnectionStateClosing)
	t.fabric.leave(t)
	inbound.Complete()
	t.fabric.Registry.SetState(t.endpointKey, TcpConnectionStateDisconnected)
	return nil
}

// Send frames payload.Data with the 4-byte length prefix, delivers it to every
// same-endpoint peer (which decodes the frame back into a NetworkPayload), and
// records a TcpThroughputSample of the framed size. Returns an error if the
// transport is not connected or ctx is cancelled. Mirrors the C# SendAsync
// (which errors when not connected).
func (t *TcpNetworkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	connected := t.connected
	t.mu.Unlock()
	if !connected {
		return errors.New("tcp transport not connected")
	}

	frame := FrameTcpPayload(payload.Data)
	peers := t.fabric.peersOf(t)
	for _, peer := range peers {
		// Decode the frame back into a payload at the receiver, mirroring the C#
		// pump that reads the length prefix then constructs NetworkPayload.Create.
		data, _, err := ParseTcpFrame(frame)
		if err != nil {
			continue
		}
		peer.inbound.Write(NewNetworkPayload(data, ""))
	}
	t.fabric.Registry.RecordSample(TcpThroughputSample{
		EndpointId:    t.endpointKey,
		BytesSent:     int64(len(frame)),
		BytesReceived: 0,
		AtUtc:         time.Now().UTC(),
	})
	return nil
}

// Receive returns a stream of inbound payloads. Frames delivered before this
// call are replayed first (unbounded buffering). The stream closes on ctx
// cancellation or Stop.
func (t *TcpNetworkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbound := t.inbound
	t.mu.Unlock()
	return inbound.ReadAll(ctx)
}

var _ INetworkTransport = (*TcpNetworkTransport)(nil)
