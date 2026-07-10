// network_bluetooth.go
//
// Ports CircleAI.Networking.Bluetooth:
//   BluetoothTransportCommons.cs -> BluetoothConnectionState,
//                                   BluetoothEndpointDescriptor,
//                                   BluetoothCapabilityProfile,
//                                   BluetoothThroughputSample,
//                                   BluetoothCapabilityProfiles,
//                                   InMemoryBluetoothTransportRegistry
//   BluetoothNetworkTransport.cs -> IBleGattAdapter, BluetoothNetworkTransport
//                                   (INetworkTransport)
//
// The C# BluetoothNetworkTransport wires an injected IBleGattAdapter (the
// platform BLE seam — Windows.Devices.Bluetooth / CoreBluetooth / Android
// BluetoothGatt / BlueZ) to an unbounded inbound channel. Per the porting rules
// (NO stubs — every contract gets a working deterministic implementation), the
// Go port supplies a fully working in-memory GATT adapter
// (InMemoryBleGattAdapter) that delivers writes to peer transports sharing a
// BluetoothFabric and records throughput into the registry. The transport itself
// is a faithful port of the C# lifecycle: StartAsync arms the adapter's inbound
// writer, StopAsync stops the adapter and completes the inbound stream.
//
// Concurrency (Wave-1 lessons): the inbound stream is an unbounded channel — a
// frame delivered before any Receive consumer attaches is BUFFERED, never lost;
// fabric membership is snapshotted under the lock and the enqueue happens
// off-lock so a slow/(dis)connecting peer cannot deadlock the writer.

package circleai

import (
	"context"
	"errors"
	"sort"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// BluetoothConnectionState — BluetoothTransportCommons.cs
// ---------------------------------------------------------------------------

// BluetoothConnectionState is the lifecycle state of a BLE link. Ordinals match
// the C# declaration order exactly.
type BluetoothConnectionState int

const (
	// BluetoothConnectionStateDisconnected — no link.
	BluetoothConnectionStateDisconnected BluetoothConnectionState = iota
	// BluetoothConnectionStateDiscovering — scanning for the device.
	BluetoothConnectionStateDiscovering
	// BluetoothConnectionStateConnecting — establishing the GATT link.
	BluetoothConnectionStateConnecting
	// BluetoothConnectionStateConnected — link is up.
	BluetoothConnectionStateConnected
	// BluetoothConnectionStateFailed — the link failed.
	BluetoothConnectionStateFailed
)

// String renders the C# enum member name for a BluetoothConnectionState.
func (s BluetoothConnectionState) String() string {
	switch s {
	case BluetoothConnectionStateDisconnected:
		return "Disconnected"
	case BluetoothConnectionStateDiscovering:
		return "Discovering"
	case BluetoothConnectionStateConnecting:
		return "Connecting"
	case BluetoothConnectionStateConnected:
		return "Connected"
	case BluetoothConnectionStateFailed:
		return "Failed"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Records — descriptor, capability profile, throughput sample
// ---------------------------------------------------------------------------

// BluetoothEndpointDescriptor describes a discoverable BLE endpoint. Ports the
// C# `sealed record BluetoothEndpointDescriptor(DeviceId, Name, MacAddress, AdvertisedServices)`.
type BluetoothEndpointDescriptor struct {
	DeviceId           string
	Name               string
	MacAddress         string
	AdvertisedServices []string
}

// BluetoothCapabilityProfile describes the link capabilities of a BLE variant.
// Ports the C# `sealed record BluetoothCapabilityProfile(MaxMtuBytes,
// SupportsSecureConnections, SupportsHighSpeed, CompatibleProfiles)`.
type BluetoothCapabilityProfile struct {
	MaxMtuBytes               int
	SupportsSecureConnections bool
	SupportsHighSpeed         bool
	CompatibleProfiles        []string
}

// BluetoothThroughputSample is a per-device throughput measurement. Ports the C#
// `sealed record BluetoothThroughputSample(DeviceId, KbpsRead, KbpsWrite, AtUtc)`.
type BluetoothThroughputSample struct {
	DeviceId  string
	KbpsRead  float64
	KbpsWrite float64
	AtUtc     time.Time
}

// The C# static BluetoothCapabilityProfiles well-known profiles (Le5 / Le4 /
// Classic) are exposed via package-level constructors so the returned slices are
// never shared/mutated across callers.

// BluetoothCapabilityProfileLe5 is the BLE 5.x profile (247-byte MTU, secure,
// high-speed; GATT + L2CAP). Mirrors BluetoothCapabilityProfiles.Le5.
func BluetoothCapabilityProfileLe5() BluetoothCapabilityProfile {
	return BluetoothCapabilityProfile{
		MaxMtuBytes:               247,
		SupportsSecureConnections: true,
		SupportsHighSpeed:         true,
		CompatibleProfiles:        []string{"GATT", "L2CAP"},
	}
}

// BluetoothCapabilityProfileLe4 is the BLE 4.x profile (23-byte MTU, secure, no
// high-speed; GATT). Mirrors BluetoothCapabilityProfiles.Le4.
func BluetoothCapabilityProfileLe4() BluetoothCapabilityProfile {
	return BluetoothCapabilityProfile{
		MaxMtuBytes:               23,
		SupportsSecureConnections: true,
		SupportsHighSpeed:         false,
		CompatibleProfiles:        []string{"GATT"},
	}
}

// BluetoothCapabilityProfileClassic is the Bluetooth Classic profile (1024-byte
// MTU, secure, no high-speed; SPP + RFCOMM). Mirrors BluetoothCapabilityProfiles.Classic.
func BluetoothCapabilityProfileClassic() BluetoothCapabilityProfile {
	return BluetoothCapabilityProfile{
		MaxMtuBytes:               1024,
		SupportsSecureConnections: true,
		SupportsHighSpeed:         false,
		CompatibleProfiles:        []string{"SPP", "RFCOMM"},
	}
}

// ---------------------------------------------------------------------------
// InMemoryBluetoothTransportRegistry — BluetoothTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryBluetoothTransportRegistry holds discovered endpoints, per-device
// connection state, and throughput samples. Ports the C#
// `InMemoryBluetoothTransportRegistry`. Safe for concurrent use.
type InMemoryBluetoothTransportRegistry struct {
	mu         sync.Mutex
	endpoints  map[string]BluetoothEndpointDescriptor
	states     map[string]BluetoothConnectionState
	throughput []BluetoothThroughputSample
}

// NewInMemoryBluetoothTransportRegistry constructs an empty registry.
func NewInMemoryBluetoothTransportRegistry() *InMemoryBluetoothTransportRegistry {
	return &InMemoryBluetoothTransportRegistry{
		endpoints: make(map[string]BluetoothEndpointDescriptor),
		states:    make(map[string]BluetoothConnectionState),
	}
}

// Register inserts or updates an endpoint keyed by DeviceId. Panics on empty
// DeviceId (mirrors the C# ArgumentNullException guard).
func (r *InMemoryBluetoothTransportRegistry) Register(e BluetoothEndpointDescriptor) {
	if e.DeviceId == "" {
		panic("bluetooth endpoint requires DeviceId")
	}
	r.mu.Lock()
	r.endpoints[e.DeviceId] = e
	r.mu.Unlock()
}

// GetEndpoint returns the endpoint for deviceId and true, or a zero value and
// false when absent.
func (r *InMemoryBluetoothTransportRegistry) GetEndpoint(deviceId string) (BluetoothEndpointDescriptor, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	e, ok := r.endpoints[deviceId]
	return e, ok
}

// AllEndpoints returns every endpoint ordered by Name (matches OrderBy(e => e.Name)).
func (r *InMemoryBluetoothTransportRegistry) AllEndpoints() []BluetoothEndpointDescriptor {
	r.mu.Lock()
	out := make([]BluetoothEndpointDescriptor, 0, len(r.endpoints))
	for _, e := range r.endpoints {
		out = append(out, e)
	}
	r.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// SetState records the connection state for deviceId.
func (r *InMemoryBluetoothTransportRegistry) SetState(deviceId string, s BluetoothConnectionState) {
	r.mu.Lock()
	r.states[deviceId] = s
	r.mu.Unlock()
}

// State returns deviceId's connection state, defaulting to Disconnected.
func (r *InMemoryBluetoothTransportRegistry) State(deviceId string) BluetoothConnectionState {
	r.mu.Lock()
	defer r.mu.Unlock()
	if s, ok := r.states[deviceId]; ok {
		return s
	}
	return BluetoothConnectionStateDisconnected
}

// RecordThroughput appends a throughput sample.
func (r *InMemoryBluetoothTransportRegistry) RecordThroughput(s BluetoothThroughputSample) {
	r.mu.Lock()
	r.throughput = append(r.throughput, s)
	r.mu.Unlock()
}

// AvgKbpsRead returns the mean KbpsRead of deviceId's samples, or 0 when none
// (mirrors DefaultIfEmpty(0.0).Average()).
func (r *InMemoryBluetoothTransportRegistry) AvgKbpsRead(deviceId string) float64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	var sum float64
	var n int
	for _, t := range r.throughput {
		if t.DeviceId == deviceId {
			sum += t.KbpsRead
			n++
		}
	}
	if n == 0 {
		return 0
	}
	return sum / float64(n)
}

// ---------------------------------------------------------------------------
// IBleGattAdapter — BluetoothNetworkTransport.cs interface IBleGattAdapter
// ---------------------------------------------------------------------------

// IBleGattAdapter is the platform-specific BLE GATT seam the transport wires to.
// Ports the C# IBleGattAdapter:
//
//	bool IsAvailable
//	Task StartAsync(ChannelWriter<NetworkPayload> inbound, ct) -> Start(ctx, inbound)
//	Task StopAsync(ct)                                         -> Stop(ctx)
//	Task WriteAsync(NetworkPayload, ct)                        -> Write(ctx, payload)
//
// The C# ChannelWriter<NetworkPayload> becomes an inboundSink the adapter pushes
// received frames into.
type IBleGattAdapter interface {
	// IsAvailable reports whether the BLE radio/adapter is usable.
	IsAvailable() bool
	// Start arms the adapter, giving it the sink to deliver inbound frames into.
	Start(ctx context.Context, inbound inboundSink) error
	// Stop tears the adapter down.
	Stop(ctx context.Context) error
	// Write transmits payload over the GATT link.
	Write(ctx context.Context, payload NetworkPayload) error
}

// inboundSink is the write-half the adapter uses to hand received frames to the
// transport — the Go analogue of ChannelWriter<NetworkPayload>. It is satisfied
// by *unboundedChannel[NetworkPayload].
type inboundSink interface {
	Write(item NetworkPayload) bool
}

// ---------------------------------------------------------------------------
// BluetoothFabric — the shared in-memory BLE medium
// ---------------------------------------------------------------------------

// BluetoothFabric is the in-process substitute for the BLE air interface. Every
// InMemoryBleGattAdapter armed against the same fabric shares a broadcast
// domain: a Write on one is delivered to every OTHER armed adapter's sink
// (loopback excluded). Carries the shared registry so throughput/state stay
// coherent.
type BluetoothFabric struct {
	// Registry is the shared endpoint/state/throughput store.
	Registry *InMemoryBluetoothTransportRegistry

	mu      sync.Mutex
	members map[*InMemoryBleGattAdapter]struct{}
}

// NewBluetoothFabric constructs a fabric with a fresh registry (or reg when
// non-nil).
func NewBluetoothFabric(reg *InMemoryBluetoothTransportRegistry) *BluetoothFabric {
	if reg == nil {
		reg = NewInMemoryBluetoothTransportRegistry()
	}
	return &BluetoothFabric{
		Registry: reg,
		members:  make(map[*InMemoryBleGattAdapter]struct{}),
	}
}

func (f *BluetoothFabric) join(a *InMemoryBleGattAdapter) {
	f.mu.Lock()
	f.members[a] = struct{}{}
	f.mu.Unlock()
}

func (f *BluetoothFabric) leave(a *InMemoryBleGattAdapter) {
	f.mu.Lock()
	delete(f.members, a)
	f.mu.Unlock()
}

// peersOf snapshots the other armed adapters under the lock; delivery off-lock.
func (f *BluetoothFabric) peersOf(sender *InMemoryBleGattAdapter) []*InMemoryBleGattAdapter {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]*InMemoryBleGattAdapter, 0, len(f.members))
	for a := range f.members {
		if a != sender {
			out = append(out, a)
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// InMemoryBleGattAdapter — working IBleGattAdapter
// ---------------------------------------------------------------------------

// InMemoryBleGattAdapter is a deterministic IBleGattAdapter backed by a
// BluetoothFabric. Write fans the payload to peer adapters' inbound sinks and
// records a throughput sample; Start/Stop join/leave the fabric. Availability is
// a settable flag (defaults on). Safe for concurrent use.
type InMemoryBleGattAdapter struct {
	fabric   *BluetoothFabric
	deviceId string

	mu        sync.Mutex
	available bool
	armed     bool
	sink      inboundSink
}

// NewInMemoryBleGattAdapter builds an adapter on fabric identified by deviceId.
// fabric is required; deviceId may be "" (used only for throughput accounting).
func NewInMemoryBleGattAdapter(fabric *BluetoothFabric, deviceId string) (*InMemoryBleGattAdapter, error) {
	if fabric == nil {
		return nil, errors.New("bluetooth fabric required")
	}
	return &InMemoryBleGattAdapter{
		fabric:    fabric,
		deviceId:  deviceId,
		available: true,
	}, nil
}

// IsAvailable reports the adapter availability flag.
func (a *InMemoryBleGattAdapter) IsAvailable() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.available
}

// SetAvailable toggles the availability flag (e.g. to simulate radio off).
func (a *InMemoryBleGattAdapter) SetAvailable(v bool) {
	a.mu.Lock()
	a.available = v
	a.mu.Unlock()
}

// Start arms the adapter with inbound and joins the fabric. Idempotent.
func (a *InMemoryBleGattAdapter) Start(ctx context.Context, inbound inboundSink) error {
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
		a.fabric.Registry.SetState(a.deviceId, BluetoothConnectionStateConnected)
	}
	return nil
}

// Stop leaves the fabric and disarms the adapter. Idempotent.
func (a *InMemoryBleGattAdapter) Stop(ctx context.Context) error {
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
		a.fabric.Registry.SetState(a.deviceId, BluetoothConnectionStateDisconnected)
	}
	return nil
}

// Write transmits payload to every peer adapter armed on the fabric and records
// a throughput sample sized by the payload. Returns an error if the adapter is
// unavailable, not armed, or ctx is cancelled.
func (a *InMemoryBleGattAdapter) Write(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	a.mu.Lock()
	armed := a.armed
	available := a.available
	a.mu.Unlock()
	if !available {
		return errors.New("bluetooth adapter unavailable")
	}
	if !armed {
		return errors.New("bluetooth adapter not started")
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
		a.fabric.Registry.RecordThroughput(BluetoothThroughputSample{
			DeviceId:  a.deviceId,
			KbpsRead:  0,
			KbpsWrite: kb,
			AtUtc:     time.Now().UTC(),
		})
	}
	return nil
}

var _ IBleGattAdapter = (*InMemoryBleGattAdapter)(nil)

// ---------------------------------------------------------------------------
// BluetoothNetworkTransport — BluetoothNetworkTransport.cs
// ---------------------------------------------------------------------------

// BluetoothNetworkTransport is an INetworkTransport over BLE GATT. Kind() is
// TransportKindBluetooth; IsAvailable() reflects the injected adapter. Start
// arms the adapter with the inbound stream's writer; Send delegates to the
// adapter's Write; Stop stops the adapter and completes the inbound stream.
// Faithful to the C# lifecycle. Safe for concurrent use.
type BluetoothNetworkTransport struct {
	adapter IBleGattAdapter

	mu      sync.Mutex
	started bool
	inbound *unboundedChannel[NetworkPayload]
}

// NewBluetoothNetworkTransport builds a transport over adapter (required,
// mirrors the C# non-null adapter guard).
func NewBluetoothNetworkTransport(adapter IBleGattAdapter) (*BluetoothNetworkTransport, error) {
	if adapter == nil {
		return nil, errors.New("ble gatt adapter required")
	}
	return &BluetoothNetworkTransport{
		adapter: adapter,
		inbound: newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindBluetooth.
func (t *BluetoothNetworkTransport) Kind() TransportKind { return TransportKindBluetooth }

// IsAvailable reports the adapter's availability (matches the C#
// `_adapter.IsAvailable`).
func (t *BluetoothNetworkTransport) IsAvailable() bool { return t.adapter.IsAvailable() }

// Start creates a fresh inbound stream and arms the adapter with its writer.
// Idempotent.
func (t *BluetoothNetworkTransport) Start(ctx context.Context) error {
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
func (t *BluetoothNetworkTransport) Stop(ctx context.Context) error {
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

// Send delegates to the adapter's Write (matches the C# `_adapter.WriteAsync`).
func (t *BluetoothNetworkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	return t.adapter.Write(ctx, payload)
}

// Receive returns a stream of inbound payloads. Frames delivered before this
// call are replayed first (unbounded buffering). The stream closes on ctx
// cancellation or Stop.
func (t *BluetoothNetworkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbound := t.inbound
	t.mu.Unlock()
	return inbound.ReadAll(ctx)
}

var _ INetworkTransport = (*BluetoothNetworkTransport)(nil)
