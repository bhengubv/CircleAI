// network_nearlink.go
//
// Ports CircleAI.Networking.NearLink:
//   NearLinkTransportCommons.cs -> NearLinkPairingState, NearLinkPowerProfile,
//                                  NearLinkDevice, NearLinkSession,
//                                  NearLinkThroughputSample, InMemoryNearLinkRegistry
//   NearLinkTransport.cs        -> INearLinkAdapter, NearLinkTransport
//                                  (INetworkTransport)
//
// The C# NearLinkTransport wires an injected INearLinkAdapter (the Huawei SLE /
// NearLink platform seam — DevEco NearLink SDK on HarmonyOS / NearLink HAL on
// Android) to an unbounded inbound channel. Per the porting rules (NO stubs —
// every contract gets a working deterministic implementation), the Go port
// supplies a fully working in-memory adapter (InMemoryNearLinkAdapter) that
// delivers Sends to peer transports sharing a NearLinkFabric and records
// throughput/RSSI into the registry. The transport is a faithful port of the C#
// lifecycle: Start arms the adapter's inbound writer, Stop stops the adapter and
// completes the inbound stream.
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
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// NearLinkPairingState — NearLinkTransportCommons.cs enum NearLinkPairingState
// ---------------------------------------------------------------------------

// NearLinkPairingState is the pairing lifecycle of a NearLink device. Ordinals
// match the C# declaration order exactly.
type NearLinkPairingState int

const (
	// NearLinkPairingStateUnpaired — not paired (and the default).
	NearLinkPairingStateUnpaired NearLinkPairingState = iota
	// NearLinkPairingStatePairing — pairing in progress.
	NearLinkPairingStatePairing
	// NearLinkPairingStatePaired — successfully paired.
	NearLinkPairingStatePaired
	// NearLinkPairingStatePairingFailed — pairing failed.
	NearLinkPairingStatePairingFailed
)

// String renders the C# enum member name for a NearLinkPairingState.
func (s NearLinkPairingState) String() string {
	switch s {
	case NearLinkPairingStateUnpaired:
		return "Unpaired"
	case NearLinkPairingStatePairing:
		return "Pairing"
	case NearLinkPairingStatePaired:
		return "Paired"
	case NearLinkPairingStatePairingFailed:
		return "PairingFailed"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// NearLinkPowerProfile — NearLinkTransportCommons.cs enum NearLinkPowerProfile
// ---------------------------------------------------------------------------

// NearLinkPowerProfile is the power/throughput trade-off a NearLink session
// runs at. Ordinals match the C# declaration order exactly.
type NearLinkPowerProfile int

const (
	// NearLinkPowerProfileLowEnergy — minimal power, lowest throughput.
	NearLinkPowerProfileLowEnergy NearLinkPowerProfile = iota
	// NearLinkPowerProfileBalanced — balanced power/throughput.
	NearLinkPowerProfileBalanced
	// NearLinkPowerProfileHighThroughput — maximum throughput, highest power.
	NearLinkPowerProfileHighThroughput
)

// String renders the C# enum member name for a NearLinkPowerProfile.
func (p NearLinkPowerProfile) String() string {
	switch p {
	case NearLinkPowerProfileLowEnergy:
		return "LowEnergy"
	case NearLinkPowerProfileBalanced:
		return "Balanced"
	case NearLinkPowerProfileHighThroughput:
		return "HighThroughput"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Records — device, session, throughput sample
// ---------------------------------------------------------------------------

// NearLinkDevice describes a discoverable NearLink device. Ports the C#
// `sealed record NearLinkDevice(DeviceId, FriendlyName, ManufacturerId,
// FirmwareVersion)`.
type NearLinkDevice struct {
	DeviceId        string
	FriendlyName    string
	ManufacturerId  string
	FirmwareVersion string
}

// NearLinkSession describes an open NearLink session. Ports the C#
// `sealed record NearLinkSession(SessionId, DeviceId, PowerProfile, StartedUtc)`.
type NearLinkSession struct {
	SessionId    string
	DeviceId     string
	PowerProfile NearLinkPowerProfile
	StartedUtc   time.Time
}

// NearLinkThroughputSample is a per-device throughput+RSSI measurement. Ports
// the C# `sealed record NearLinkThroughputSample(DeviceId, KbpsRead, KbpsWrite,
// RssiDbm, AtUtc)`.
type NearLinkThroughputSample struct {
	DeviceId  string
	KbpsRead  float64
	KbpsWrite float64
	RssiDbm   int
	AtUtc     time.Time
}

// ---------------------------------------------------------------------------
// InMemoryNearLinkRegistry — NearLinkTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryNearLinkRegistry holds discovered devices, per-device pairing state,
// open sessions, and throughput samples. Ports the C#
// `InMemoryNearLinkRegistry`. Safe for concurrent use.
type InMemoryNearLinkRegistry struct {
	mu         sync.Mutex
	devices    map[string]NearLinkDevice
	states     map[string]NearLinkPairingState
	sessions   map[string]NearLinkSession
	throughput []NearLinkThroughputSample
}

// NewInMemoryNearLinkRegistry constructs an empty registry.
func NewInMemoryNearLinkRegistry() *InMemoryNearLinkRegistry {
	return &InMemoryNearLinkRegistry{
		devices:  make(map[string]NearLinkDevice),
		states:   make(map[string]NearLinkPairingState),
		sessions: make(map[string]NearLinkSession),
	}
}

// Register inserts or updates a device keyed by DeviceId. Panics on empty
// DeviceId (mirrors the C# ArgumentNullException guard).
func (r *InMemoryNearLinkRegistry) Register(d NearLinkDevice) {
	if d.DeviceId == "" {
		panic("nearlink device requires DeviceId")
	}
	r.mu.Lock()
	r.devices[d.DeviceId] = d
	r.mu.Unlock()
}

// GetDevice returns the device for id and true, or a zero value and false.
func (r *InMemoryNearLinkRegistry) GetDevice(id string) (NearLinkDevice, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	d, ok := r.devices[id]
	return d, ok
}

// Devices returns every device ordered by FriendlyName (matches
// OrderBy(d => d.FriendlyName)).
func (r *InMemoryNearLinkRegistry) Devices() []NearLinkDevice {
	r.mu.Lock()
	out := make([]NearLinkDevice, 0, len(r.devices))
	for _, d := range r.devices {
		out = append(out, d)
	}
	r.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].FriendlyName < out[j].FriendlyName })
	return out
}

// SetPairingState records the pairing state for deviceId.
func (r *InMemoryNearLinkRegistry) SetPairingState(deviceId string, s NearLinkPairingState) {
	r.mu.Lock()
	r.states[deviceId] = s
	r.mu.Unlock()
}

// PairingState returns deviceId's pairing state, defaulting to Unpaired.
func (r *InMemoryNearLinkRegistry) PairingState(deviceId string) NearLinkPairingState {
	r.mu.Lock()
	defer r.mu.Unlock()
	if s, ok := r.states[deviceId]; ok {
		return s
	}
	return NearLinkPairingStateUnpaired
}

// OpenSession records an open session keyed by SessionId. Panics on empty
// SessionId (mirrors the C# ArgumentNullException guard).
func (r *InMemoryNearLinkRegistry) OpenSession(s NearLinkSession) {
	if s.SessionId == "" {
		panic("nearlink session requires SessionId")
	}
	r.mu.Lock()
	r.sessions[s.SessionId] = s
	r.mu.Unlock()
}

// GetSession returns the session for id and true, or a zero value and false.
func (r *InMemoryNearLinkRegistry) GetSession(id string) (NearLinkSession, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	s, ok := r.sessions[id]
	return s, ok
}

// CloseSession removes a session (mirrors TryRemove).
func (r *InMemoryNearLinkRegistry) CloseSession(id string) {
	r.mu.Lock()
	delete(r.sessions, id)
	r.mu.Unlock()
}

// ActiveSessions returns every open session. Order is unspecified (mirrors
// ConcurrentDictionary.Values.ToArray()).
func (r *InMemoryNearLinkRegistry) ActiveSessions() []NearLinkSession {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]NearLinkSession, 0, len(r.sessions))
	for _, s := range r.sessions {
		out = append(out, s)
	}
	return out
}

// RecordThroughput appends a throughput sample.
func (r *InMemoryNearLinkRegistry) RecordThroughput(s NearLinkThroughputSample) {
	r.mu.Lock()
	r.throughput = append(r.throughput, s)
	r.mu.Unlock()
}

// AvgRssi returns the mean RssiDbm of deviceId's samples, or -127 when none
// (mirrors DefaultIfEmpty(-127).Average()).
func (r *InMemoryNearLinkRegistry) AvgRssi(deviceId string) float64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	var sum float64
	var n int
	for _, t := range r.throughput {
		if t.DeviceId == deviceId {
			sum += float64(t.RssiDbm)
			n++
		}
	}
	if n == 0 {
		return -127
	}
	return sum / float64(n)
}

// ---------------------------------------------------------------------------
// INearLinkAdapter — NearLinkTransport.cs interface INearLinkAdapter
// ---------------------------------------------------------------------------

// INearLinkAdapter is the platform-level NearLink / SLE seam the transport wires
// to. Ports the C# INearLinkAdapter:
//
//	bool IsAvailable
//	Task StartAsync(ChannelWriter<NetworkPayload> inbound, ct) -> Start(ctx, inbound)
//	Task StopAsync(ct)                                         -> Stop(ctx)
//	Task SendAsync(NetworkPayload, ct)                         -> Send(ctx, payload)
//
// The C# ChannelWriter<NetworkPayload> becomes an inboundSink (shared with the
// other transports) the adapter pushes received frames into.
type INearLinkAdapter interface {
	// IsAvailable reports whether the NearLink radio/adapter is usable.
	IsAvailable() bool
	// Start arms the adapter, giving it the sink to deliver inbound frames into.
	Start(ctx context.Context, inbound inboundSink) error
	// Stop tears the adapter down.
	Stop(ctx context.Context) error
	// Send transmits payload over the NearLink session.
	Send(ctx context.Context, payload NetworkPayload) error
}

// ---------------------------------------------------------------------------
// NearLinkFabric — the shared in-memory NearLink medium
// ---------------------------------------------------------------------------

// NearLinkFabric is the in-process substitute for the NearLink air interface.
// Every InMemoryNearLinkAdapter armed against the same fabric shares a broadcast
// domain: a Send on one is delivered to every OTHER armed adapter's sink
// (loopback excluded). Carries the shared registry so throughput/RSSI stay
// coherent.
type NearLinkFabric struct {
	// Registry is the shared device/state/session/throughput store.
	Registry *InMemoryNearLinkRegistry

	mu      sync.Mutex
	members map[*InMemoryNearLinkAdapter]struct{}
}

// NewNearLinkFabric constructs a fabric with a fresh registry (or reg when
// non-nil).
func NewNearLinkFabric(reg *InMemoryNearLinkRegistry) *NearLinkFabric {
	if reg == nil {
		reg = NewInMemoryNearLinkRegistry()
	}
	return &NearLinkFabric{
		Registry: reg,
		members:  make(map[*InMemoryNearLinkAdapter]struct{}),
	}
}

func (f *NearLinkFabric) join(a *InMemoryNearLinkAdapter) {
	f.mu.Lock()
	f.members[a] = struct{}{}
	f.mu.Unlock()
}

func (f *NearLinkFabric) leave(a *InMemoryNearLinkAdapter) {
	f.mu.Lock()
	delete(f.members, a)
	f.mu.Unlock()
}

// peersOf snapshots the other armed adapters under the lock; delivery off-lock.
func (f *NearLinkFabric) peersOf(sender *InMemoryNearLinkAdapter) []*InMemoryNearLinkAdapter {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]*InMemoryNearLinkAdapter, 0, len(f.members))
	for a := range f.members {
		if a != sender {
			out = append(out, a)
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// InMemoryNearLinkAdapter — working INearLinkAdapter
// ---------------------------------------------------------------------------

// InMemoryNearLinkAdapter is a deterministic INearLinkAdapter backed by a
// NearLinkFabric. Send fans the payload to peer adapters' inbound sinks and
// records a throughput sample; Start/Stop join/leave the fabric and update the
// device pairing state. Availability is a settable flag (defaults on). RSSI
// stamped on samples is settable (defaults to a plausible -50 dBm). Safe for
// concurrent use.
type InMemoryNearLinkAdapter struct {
	fabric   *NearLinkFabric
	deviceId string

	mu        sync.Mutex
	available bool
	armed     bool
	rssiDbm   int
	sink      inboundSink
}

// NewInMemoryNearLinkAdapter builds an adapter on fabric identified by deviceId.
// fabric is required; deviceId may be "" (used only for throughput/state
// accounting).
func NewInMemoryNearLinkAdapter(fabric *NearLinkFabric, deviceId string) (*InMemoryNearLinkAdapter, error) {
	if fabric == nil {
		return nil, errors.New("nearlink fabric required")
	}
	return &InMemoryNearLinkAdapter{
		fabric:    fabric,
		deviceId:  deviceId,
		available: true,
		rssiDbm:   -50,
	}, nil
}

// IsAvailable reports the adapter availability flag.
func (a *InMemoryNearLinkAdapter) IsAvailable() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.available
}

// SetAvailable toggles the availability flag (e.g. to simulate radio off).
func (a *InMemoryNearLinkAdapter) SetAvailable(v bool) {
	a.mu.Lock()
	a.available = v
	a.mu.Unlock()
}

// SetRssi sets the RSSI (dBm) stamped on throughput samples this adapter records.
func (a *InMemoryNearLinkAdapter) SetRssi(dbm int) {
	a.mu.Lock()
	a.rssiDbm = dbm
	a.mu.Unlock()
}

// Start arms the adapter with inbound and joins the fabric, marking the device
// Paired. Idempotent.
func (a *InMemoryNearLinkAdapter) Start(ctx context.Context, inbound inboundSink) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if inbound == nil {
		return errors.New("inbound sink required")
	}
	a.mu.Lock()
	if a.armed {
		a.sink = inbound
		a.mu.Unlock()
		return nil
	}
	a.sink = inbound
	a.armed = true
	a.mu.Unlock()
	a.fabric.join(a)
	if a.deviceId != "" {
		a.fabric.Registry.SetPairingState(a.deviceId, NearLinkPairingStatePaired)
	}
	return nil
}

// Stop leaves the fabric and disarms the adapter, marking the device Unpaired.
// Idempotent.
func (a *InMemoryNearLinkAdapter) Stop(ctx context.Context) error {
	a.mu.Lock()
	if !a.armed {
		a.mu.Unlock()
		return nil
	}
	a.armed = false
	a.sink = nil
	a.mu.Unlock()
	a.fabric.leave(a)
	if a.deviceId != "" {
		a.fabric.Registry.SetPairingState(a.deviceId, NearLinkPairingStateUnpaired)
	}
	return nil
}

// Send transmits payload to every peer adapter armed on the fabric and records a
// throughput sample sized by the payload (with this adapter's RSSI). Returns an
// error if the adapter is unavailable, not armed, or ctx is cancelled.
func (a *InMemoryNearLinkAdapter) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	a.mu.Lock()
	armed := a.armed
	available := a.available
	rssi := a.rssiDbm
	a.mu.Unlock()
	if !available {
		return errors.New("nearlink adapter unavailable")
	}
	if !armed {
		return errors.New("nearlink adapter not started")
	}

	for _, peer := range a.fabric.peersOf(a) {
		peer.mu.Lock()
		sink := peer.sink
		peer.mu.Unlock()
		if sink != nil {
			sink.Write(payload)
		}
	}
	if a.deviceId != "" {
		kb := float64(len(payload.Data)) / 1024.0
		a.fabric.Registry.RecordThroughput(NearLinkThroughputSample{
			DeviceId:  a.deviceId,
			KbpsRead:  0,
			KbpsWrite: kb,
			RssiDbm:   rssi,
			AtUtc:     time.Now().UTC(),
		})
	}
	return nil
}

var _ INearLinkAdapter = (*InMemoryNearLinkAdapter)(nil)

// ---------------------------------------------------------------------------
// NearLinkTransport — NearLinkTransport.cs
// ---------------------------------------------------------------------------

// NearLinkTransport is an INetworkTransport over Huawei SLE / NearLink. Kind() is
// TransportKindNearLink; IsAvailable() reflects the injected adapter. Start arms
// the adapter with the inbound stream's writer; Send delegates to the adapter's
// Send; Stop stops the adapter and completes the inbound stream. Faithful to the
// C# lifecycle. Safe for concurrent use.
type NearLinkTransport struct {
	adapter INearLinkAdapter

	mu      sync.Mutex
	started bool
	inbound *unboundedChannel[NetworkPayload]
}

// NewNearLinkTransport builds a transport over adapter (required, mirrors the C#
// non-null adapter guard).
func NewNearLinkTransport(adapter INearLinkAdapter) (*NearLinkTransport, error) {
	if adapter == nil {
		return nil, errors.New("nearlink adapter required")
	}
	return &NearLinkTransport{
		adapter: adapter,
		inbound: newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindNearLink.
func (t *NearLinkTransport) Kind() TransportKind { return TransportKindNearLink }

// IsAvailable reports the adapter's availability (matches the C#
// `_adapter.IsAvailable`).
func (t *NearLinkTransport) IsAvailable() bool { return t.adapter.IsAvailable() }

// Start creates a fresh inbound stream and arms the adapter with its writer.
// Idempotent.
func (t *NearLinkTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.started {
		inbound := t.inbound
		t.mu.Unlock()
		return t.adapter.Start(ctx, inbound)
	}
	t.inbound = newUnboundedChannel[NetworkPayload]()
	inbound := t.inbound
	t.started = true
	t.mu.Unlock()
	return t.adapter.Start(ctx, inbound)
}

// Stop stops the adapter then completes the inbound stream so active Receive
// streams drain and close. Idempotent.
func (t *NearLinkTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.started {
		t.mu.Unlock()
		return nil
	}
	t.started = false
	inbound := t.inbound
	t.mu.Unlock()

	err := t.adapter.Stop(ctx)
	inbound.Complete()
	return err
}

// Send delegates to the adapter's Send (matches the C# `_adapter.SendAsync`).
func (t *NearLinkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	return t.adapter.Send(ctx, payload)
}

// Receive returns a stream of inbound payloads. Frames delivered before this
// call are replayed first (unbounded buffering). The stream closes on ctx
// cancellation or Stop.
func (t *NearLinkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbound := t.inbound
	t.mu.Unlock()
	return inbound.ReadAll(ctx)
}

var _ INetworkTransport = (*NearLinkTransport)(nil)
