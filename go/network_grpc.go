// network_grpc.go
//
// Ports CircleAI.Networking.Grpc:
//   GrpcTransportCommons.cs   -> GrpcChannelState, GrpcChannelDescriptor,
//                                GrpcRetryPolicy, GrpcCallSummary,
//                                GrpcRetryPolicies, InMemoryGrpcCallMetrics
//   GrpcNetworkTransport.cs   -> GrpcNetworkTransport (INetworkTransport)
//
// The C# GrpcNetworkTransport wraps a real Grpc.Net.Client.GrpcChannel; its
// SendAsync throws NotSupportedException (typed proto clients use the channel
// directly) and ReceiveAsync yields nothing. Per the porting rules (NO stubs, NO
// NotImplementedException — every contract gets a working deterministic
// implementation), the Go port replaces the live gRPC channel with an injected
// GrpcChannelDescriptor + a shared in-memory GrpcFabric so Send/Receive actually
// move payloads between transports on the same channel target, applying the
// injected GrpcRetryPolicy and recording a GrpcCallSummary into
// InMemoryGrpcCallMetrics for each call. Lifecycle (Start/Stop → _running) is a
// faithful port.
//
// Concurrency (Wave-1 lessons): the inbound stream is an unbounded channel — a
// frame delivered before any Receive consumer attaches is BUFFERED, never lost;
// fabric membership is snapshotted under the lock and the enqueue happens
// off-lock so a slow/(dis)connecting peer cannot deadlock the sender.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strconv"
	"sync"
	"sync/atomic"
	"time"
)

// ---------------------------------------------------------------------------
// GrpcChannelState — GrpcTransportCommons.cs enum GrpcChannelState
// ---------------------------------------------------------------------------

// GrpcChannelState is the connectivity state of a gRPC channel. Ordinals match
// the C# declaration order exactly.
type GrpcChannelState int

const (
	// GrpcChannelStateIdle — channel created, not yet connected.
	GrpcChannelStateIdle GrpcChannelState = iota
	// GrpcChannelStateConnecting — establishing the connection.
	GrpcChannelStateConnecting
	// GrpcChannelStateReady — connected and ready.
	GrpcChannelStateReady
	// GrpcChannelStateTransientFailure — a recoverable failure.
	GrpcChannelStateTransientFailure
	// GrpcChannelStateShutdown — channel shut down.
	GrpcChannelStateShutdown
)

// String renders the C# enum member name for a GrpcChannelState.
func (s GrpcChannelState) String() string {
	switch s {
	case GrpcChannelStateIdle:
		return "Idle"
	case GrpcChannelStateConnecting:
		return "Connecting"
	case GrpcChannelStateReady:
		return "Ready"
	case GrpcChannelStateTransientFailure:
		return "TransientFailure"
	case GrpcChannelStateShutdown:
		return "Shutdown"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Records — descriptor, retry policy, call summary
// ---------------------------------------------------------------------------

// GrpcChannelDescriptor describes a gRPC channel's configuration. Ports the C#
// `sealed record GrpcChannelDescriptor(Target, UseTls, MaxReceiveBytes,
// MaxSendBytes, KeepAliveInterval)`.
type GrpcChannelDescriptor struct {
	Target            string
	UseTls            bool
	MaxReceiveBytes   int
	MaxSendBytes      int
	KeepAliveInterval time.Duration
}

// GrpcRetryPolicy describes retry behaviour for a gRPC call. Ports the C#
// `sealed record GrpcRetryPolicy(MaxAttempts, InitialBackoff, MaxBackoff,
// Multiplier, RetryableStatusCodes)`.
type GrpcRetryPolicy struct {
	MaxAttempts          int
	InitialBackoff       time.Duration
	MaxBackoff           time.Duration
	Multiplier           float64
	RetryableStatusCodes []string
}

// IsRetryable reports whether statusCode is in RetryableStatusCodes.
func (p GrpcRetryPolicy) IsRetryable(statusCode string) bool {
	for _, s := range p.RetryableStatusCodes {
		if s == statusCode {
			return true
		}
	}
	return false
}

// BackoffFor returns the backoff before attempt (1-based): InitialBackoff scaled
// by Multiplier^(attempt-1), capped at MaxBackoff. Attempt <= 1 yields
// InitialBackoff. This makes the retry-policy semantics executable (the C#
// record only carries the parameters).
func (p GrpcRetryPolicy) BackoffFor(attempt int) time.Duration {
	if attempt <= 1 {
		return p.InitialBackoff
	}
	d := float64(p.InitialBackoff)
	for i := 1; i < attempt; i++ {
		d *= p.Multiplier
		if p.MaxBackoff > 0 && d >= float64(p.MaxBackoff) {
			return p.MaxBackoff
		}
	}
	if p.MaxBackoff > 0 && d > float64(p.MaxBackoff) {
		return p.MaxBackoff
	}
	return time.Duration(d)
}

// GrpcCallSummary is a per-call accounting record. Ports the C#
// `sealed record GrpcCallSummary(Method, Attempts, Latency, StatusCode, AtUtc)`.
type GrpcCallSummary struct {
	Method     string
	Attempts   int
	Latency    time.Duration
	StatusCode string
	AtUtc      time.Time
}

// Well-known retry policies — ports the C# static GrpcRetryPolicies. Exposed as
// constructors so the returned slices are never shared across callers.

// GrpcRetryPolicyDefault is 3 attempts, 100ms→2s backoff (x2), retrying
// UNAVAILABLE / DEADLINE_EXCEEDED. Mirrors GrpcRetryPolicies.Default.
func GrpcRetryPolicyDefault() GrpcRetryPolicy {
	return GrpcRetryPolicy{
		MaxAttempts:          3,
		InitialBackoff:       100 * time.Millisecond,
		MaxBackoff:           2 * time.Second,
		Multiplier:           2.0,
		RetryableStatusCodes: []string{"UNAVAILABLE", "DEADLINE_EXCEEDED"},
	}
}

// GrpcRetryPolicyAggressive is 6 attempts, 50ms→5s backoff (x2), also retrying
// RESOURCE_EXHAUSTED. Mirrors GrpcRetryPolicies.Aggressive.
func GrpcRetryPolicyAggressive() GrpcRetryPolicy {
	return GrpcRetryPolicy{
		MaxAttempts:          6,
		InitialBackoff:       50 * time.Millisecond,
		MaxBackoff:           5 * time.Second,
		Multiplier:           2.0,
		RetryableStatusCodes: []string{"UNAVAILABLE", "DEADLINE_EXCEEDED", "RESOURCE_EXHAUSTED"},
	}
}

// GrpcRetryPolicyNoRetry is a single attempt with no backoff. Mirrors
// GrpcRetryPolicies.NoRetry.
func GrpcRetryPolicyNoRetry() GrpcRetryPolicy {
	return GrpcRetryPolicy{
		MaxAttempts:          1,
		InitialBackoff:       0,
		MaxBackoff:           0,
		Multiplier:           1.0,
		RetryableStatusCodes: []string{},
	}
}

// ---------------------------------------------------------------------------
// InMemoryGrpcCallMetrics — GrpcTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryGrpcCallMetrics tracks channel descriptors, per-channel state, and a
// call log. Ports the C# `InMemoryGrpcCallMetrics`. Safe for concurrent use.
type InMemoryGrpcCallMetrics struct {
	mu       sync.Mutex
	channels map[string]GrpcChannelDescriptor
	states   map[string]GrpcChannelState
	calls    []GrpcCallSummary
	seq      atomic.Int64
}

// NewInMemoryGrpcCallMetrics constructs an empty metrics store.
func NewInMemoryGrpcCallMetrics() *InMemoryGrpcCallMetrics {
	return &InMemoryGrpcCallMetrics{
		channels: make(map[string]GrpcChannelDescriptor),
		states:   make(map[string]GrpcChannelState),
	}
}

// RegisterChannel records a channel descriptor under id.
func (m *InMemoryGrpcCallMetrics) RegisterChannel(id string, d GrpcChannelDescriptor) {
	m.mu.Lock()
	m.channels[id] = d
	m.mu.Unlock()
}

// GetChannel returns the descriptor for id and true, or a zero value and false.
func (m *InMemoryGrpcCallMetrics) GetChannel(id string) (GrpcChannelDescriptor, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	d, ok := m.channels[id]
	return d, ok
}

// SetState records the state for channel id.
func (m *InMemoryGrpcCallMetrics) SetState(id string, s GrpcChannelState) {
	m.mu.Lock()
	m.states[id] = s
	m.mu.Unlock()
}

// State returns channel id's state, defaulting to Idle.
func (m *InMemoryGrpcCallMetrics) State(id string) GrpcChannelState {
	m.mu.Lock()
	defer m.mu.Unlock()
	if s, ok := m.states[id]; ok {
		return s
	}
	return GrpcChannelStateIdle
}

// LogCall appends a call summary and returns a monotonic "grpc-N" call id
// (mirrors Interlocked.Increment).
func (m *InMemoryGrpcCallMetrics) LogCall(c GrpcCallSummary) string {
	m.mu.Lock()
	m.calls = append(m.calls, c)
	m.mu.Unlock()
	n := m.seq.Add(1)
	return "grpc-" + strconv.FormatInt(n, 10)
}

// RecentCalls returns up to limit calls, most recent first (ordered by AtUtc
// descending). Mirrors OrderByDescending(c => c.AtUtc).Take(limit).
func (m *InMemoryGrpcCallMetrics) RecentCalls(limit int) []GrpcCallSummary {
	if limit <= 0 {
		limit = 50
	}
	m.mu.Lock()
	snapshot := make([]GrpcCallSummary, len(m.calls))
	copy(snapshot, m.calls)
	m.mu.Unlock()
	sort.SliceStable(snapshot, func(i, j int) bool { return snapshot[i].AtUtc.After(snapshot[j].AtUtc) })
	if len(snapshot) > limit {
		snapshot = snapshot[:limit]
	}
	return snapshot
}

// ---------------------------------------------------------------------------
// GrpcFabric — the injected in-memory gRPC channel medium
// ---------------------------------------------------------------------------

// GrpcFabric is the in-process substitute for the gRPC transport. Transports
// built against the same fabric AND the same channel Target share a broadcast
// domain: a Send on one is delivered to every OTHER started transport with a
// matching Target (loopback excluded), modelling a gRPC service fanning a
// streamed message to connected peers. Carries the shared metrics so channel
// state / call logs stay coherent.
type GrpcFabric struct {
	// Metrics is the shared channel/state/call store.
	Metrics *InMemoryGrpcCallMetrics

	mu      sync.Mutex
	members map[*GrpcNetworkTransport]struct{}
}

// NewGrpcFabric constructs a fabric with fresh metrics (or m when non-nil).
func NewGrpcFabric(m *InMemoryGrpcCallMetrics) *GrpcFabric {
	if m == nil {
		m = NewInMemoryGrpcCallMetrics()
	}
	return &GrpcFabric{
		Metrics: m,
		members: make(map[*GrpcNetworkTransport]struct{}),
	}
}

func (f *GrpcFabric) join(t *GrpcNetworkTransport) {
	f.mu.Lock()
	f.members[t] = struct{}{}
	f.mu.Unlock()
}

func (f *GrpcFabric) leave(t *GrpcNetworkTransport) {
	f.mu.Lock()
	delete(f.members, t)
	f.mu.Unlock()
}

// peersOf snapshots the other started transports on the same Target under the
// lock; delivery happens off-lock.
func (f *GrpcFabric) peersOf(sender *GrpcNetworkTransport) []*GrpcNetworkTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]*GrpcNetworkTransport, 0, len(f.members))
	for m := range f.members {
		if m != sender && m.descriptor.Target == sender.descriptor.Target {
			out = append(out, m)
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// GrpcNetworkTransport — GrpcNetworkTransport.cs
// ---------------------------------------------------------------------------

// GrpcNetworkTransport is an INetworkTransport backed by a gRPC channel
// descriptor + shared GrpcFabric. Kind() is TransportKindGrpc; IsAvailable()
// tracks the _running flag (a faithful port of the C# lifecycle). Send delivers
// the payload to same-Target peers, applying the retry policy and logging a
// GrpcCallSummary; Receive streams inbound payloads. Where the C# throws
// NotSupportedException, the Go port instead performs the working in-memory
// delivery the rules require. Safe for concurrent use.
type GrpcNetworkTransport struct {
	descriptor GrpcChannelDescriptor
	fabric     *GrpcFabric
	retry      GrpcRetryPolicy

	mu      sync.Mutex
	running bool
	inbound *unboundedChannel[NetworkPayload]
}

// NewGrpcNetworkTransport builds a transport for descriptor on fabric using
// retry. fabric is required (the injected channel medium). Pass a zero
// GrpcRetryPolicy to default to GrpcRetryPolicyDefault. The descriptor.Target
// scopes the broadcast domain — transports with the same Target on the same
// fabric are connected.
func NewGrpcNetworkTransport(descriptor GrpcChannelDescriptor, fabric *GrpcFabric, retry GrpcRetryPolicy) (*GrpcNetworkTransport, error) {
	if fabric == nil {
		return nil, errors.New("grpc fabric required")
	}
	if descriptor.Target == "" {
		return nil, errors.New("grpc channel target required")
	}
	if retry.MaxAttempts <= 0 {
		retry = GrpcRetryPolicyDefault()
	}
	fabric.Metrics.RegisterChannel(descriptor.Target, descriptor)
	fabric.Metrics.SetState(descriptor.Target, GrpcChannelStateIdle)
	return &GrpcNetworkTransport{
		descriptor: descriptor,
		fabric:     fabric,
		retry:      retry,
		inbound:    newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindGrpc.
func (t *GrpcNetworkTransport) Kind() TransportKind { return TransportKindGrpc }

// IsAvailable reports the running flag (matches the C# `_running`).
func (t *GrpcNetworkTransport) IsAvailable() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.running
}

// Descriptor returns the channel descriptor this transport speaks over — the Go
// analogue of the C# `Channel` accessor.
func (t *GrpcNetworkTransport) Descriptor() GrpcChannelDescriptor { return t.descriptor }

// Start marks the channel Ready, joins the fabric, and sets running. Idempotent.
func (t *GrpcNetworkTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.running {
		t.mu.Unlock()
		return nil
	}
	t.inbound = newUnboundedChannel[NetworkPayload]()
	t.running = true
	t.mu.Unlock()
	t.fabric.join(t)
	t.fabric.Metrics.SetState(t.descriptor.Target, GrpcChannelStateReady)
	return nil
}

// Stop marks the channel Shutdown, leaves the fabric, clears running, and
// completes the inbound stream. Idempotent.
func (t *GrpcNetworkTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.running {
		t.mu.Unlock()
		return nil
	}
	t.running = false
	inbound := t.inbound
	t.mu.Unlock()
	t.fabric.leave(t)
	t.fabric.Metrics.SetState(t.descriptor.Target, GrpcChannelStateShutdown)
	inbound.Complete()
	return nil
}

// Send delivers payload to same-Target peers on the fabric. It respects the
// payload's MaxSendBytes budget (over-budget → RESOURCE_EXHAUSTED, retried per
// policy but ultimately failing since the in-memory link is deterministic) and
// logs a GrpcCallSummary. Returns an error if the transport is not started,
// ctx is cancelled, or the payload exceeds MaxSendBytes.
func (t *GrpcNetworkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	running := t.running
	t.mu.Unlock()
	if !running {
		return errors.New("grpc transport not started")
	}

	start := time.Now()
	oversize := t.descriptor.MaxSendBytes > 0 && len(payload.Data) > t.descriptor.MaxSendBytes

	status := "OK"
	attempts := 1
	var sendErr error
	if oversize {
		// Deterministic hard failure: exhaust attempts (retryable per Aggressive
		// policy but the condition never clears in-memory) then surface the error.
		status = "RESOURCE_EXHAUSTED"
		if t.retry.IsRetryable(status) {
			attempts = t.retry.MaxAttempts
		}
		sendErr = errors.New("grpc payload exceeds MaxSendBytes")
	} else {
		for _, peer := range t.fabric.peersOf(t) {
			peer.inbound.Write(payload)
		}
	}

	t.fabric.Metrics.LogCall(GrpcCallSummary{
		Method:     "Send",
		Attempts:   attempts,
		Latency:    time.Since(start),
		StatusCode: status,
		AtUtc:      time.Now().UTC(),
	})
	return sendErr
}

// Receive returns a stream of inbound payloads. Frames delivered before this
// call are replayed first (unbounded buffering). The stream closes on ctx
// cancellation or Stop.
func (t *GrpcNetworkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbound := t.inbound
	t.mu.Unlock()
	return inbound.ReadAll(ctx)
}

var _ INetworkTransport = (*GrpcNetworkTransport)(nil)
