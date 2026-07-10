// network_mqtt.go
//
// Ports CircleAI.Networking.Mqtt:
//   MqttTransportCommons.cs   -> MqttQos, MqttTopicDescriptor, MqttRetainedMessage,
//                                MqttClientDescriptor, InMemoryMqttBroker
//   MqttNetworkTransport.cs   -> MqttNetworkTransport (INetworkTransport)
//
// The C# MqttNetworkTransport wraps a real MQTTnet IMqttClient: it connects to a
// broker, subscribes to circle/payloads/{localClientId}/#, publishes to
// circle/payloads/{destinationId} (or circle/payloads/broadcast when no
// destination), and picks QoS ExactlyOnce for High+ priority / AtLeastOnce
// otherwise. Per the porting rules (NO stubs — every contract gets a working
// deterministic implementation), the Go port swaps the live MQTTnet client for
// the InMemoryMqttBroker (already a first-class type in the C# commons file):
// several transports sharing one broker form a pub/sub domain and a Publish is
// routed to every client whose subscription filter Matches the topic. The
// publish/subscribe topic conventions and the priority->QoS mapping are ported
// exactly.
//
// Concurrency (Wave-1 lessons): the inbound stream is an unbounded channel — a
// message delivered before any Receive consumer attaches is BUFFERED, never
// lost; broker subscribers are snapshotted under the broker lock and the enqueue
// onto each subscriber happens off-lock so a slow/(dis)connecting client cannot
// deadlock the publisher.

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// MqttQos — MqttTransportCommons.cs enum MqttQos
// ---------------------------------------------------------------------------

// MqttQos is an MQTT quality-of-service level. Ordinals match the C# declaration
// (and the MQTT wire values 0/1/2) exactly.
type MqttQos int

const (
	// MqttQosAtMostOnce — fire-and-forget (QoS 0).
	MqttQosAtMostOnce MqttQos = iota
	// MqttQosAtLeastOnce — acknowledged delivery, possible duplicates (QoS 1).
	MqttQosAtLeastOnce
	// MqttQosExactlyOnce — exactly-once delivery (QoS 2).
	MqttQosExactlyOnce
)

// String renders the C# enum member name for a MqttQos.
func (q MqttQos) String() string {
	switch q {
	case MqttQosAtMostOnce:
		return "AtMostOnce"
	case MqttQosAtLeastOnce:
		return "AtLeastOnce"
	case MqttQosExactlyOnce:
		return "ExactlyOnce"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Records — topic descriptor, retained message, client descriptor
// ---------------------------------------------------------------------------

// MqttTopicDescriptor pairs a topic with its QoS. Ports the C#
// `sealed record MqttTopicDescriptor(string Topic, MqttQos Qos)`.
type MqttTopicDescriptor struct {
	Topic string
	Qos   MqttQos
}

// MqttRetainedMessage is a broker-retained message for a topic. Ports the C#
// `sealed record MqttRetainedMessage(string Topic, ReadOnlyMemory<byte> Payload,
// DateTimeOffset RetainedAtUtc)`.
type MqttRetainedMessage struct {
	Topic         string
	Payload       []byte
	RetainedAtUtc time.Time
}

// MqttClientDescriptor describes a connected MQTT client. Ports the C#
// `sealed record MqttClientDescriptor(ClientId, Host, Port, UseTls, KeepAlive)`.
type MqttClientDescriptor struct {
	ClientId  string
	Host      string
	Port      int
	UseTls    bool
	KeepAlive time.Duration
}

// ---------------------------------------------------------------------------
// InMemoryMqttBroker — MqttTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryMqttBroker is a deterministic in-process MQTT broker: it tracks
// connected clients, per-client subscription filters, and retained messages, and
// answers topic-filter matching. Ports the C# `InMemoryMqttBroker`. Beyond the
// C# accounting surface it also routes live payloads to matching subscribers'
// sinks (the seam MqttNetworkTransport publishes through), which is what lets the
// no-stubs transport actually move bytes without MQTTnet. Safe for concurrent
// use.
type InMemoryMqttBroker struct {
	mu            sync.Mutex
	clients       map[string]MqttClientDescriptor
	subscriptions map[string]map[string]struct{} // clientId -> set of topic filters
	retained      map[string]MqttRetainedMessage
	sinks         map[string]inboundSink // clientId -> live delivery sink
}

// NewInMemoryMqttBroker constructs an empty broker.
func NewInMemoryMqttBroker() *InMemoryMqttBroker {
	return &InMemoryMqttBroker{
		clients:       make(map[string]MqttClientDescriptor),
		subscriptions: make(map[string]map[string]struct{}),
		retained:      make(map[string]MqttRetainedMessage),
		sinks:         make(map[string]inboundSink),
	}
}

// Connect records a connected client keyed by ClientId. Panics on empty ClientId
// (mirrors the C# ArgumentNullException guard on the descriptor).
func (b *InMemoryMqttBroker) Connect(c MqttClientDescriptor) {
	if c.ClientId == "" {
		panic("mqtt client requires ClientId")
	}
	b.mu.Lock()
	b.clients[c.ClientId] = c
	b.mu.Unlock()
}

// Disconnect removes a client (mirrors the C# TryRemove). Its subscriptions and
// live sink are dropped too so a disconnected client stops receiving.
func (b *InMemoryMqttBroker) Disconnect(clientId string) {
	b.mu.Lock()
	delete(b.clients, clientId)
	delete(b.subscriptions, clientId)
	delete(b.sinks, clientId)
	b.mu.Unlock()
}

// ConnectedClients returns every connected client descriptor. Order is
// unspecified (mirrors ConcurrentDictionary.Values.ToArray()).
func (b *InMemoryMqttBroker) ConnectedClients() []MqttClientDescriptor {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]MqttClientDescriptor, 0, len(b.clients))
	for _, c := range b.clients {
		out = append(out, c)
	}
	return out
}

// Subscribe adds topicFilter to clientId's subscription set. Returns an error on
// empty clientId/topicFilter (mirrors the C# ArgumentException guards).
func (b *InMemoryMqttBroker) Subscribe(clientId, topicFilter string) error {
	if strings.TrimSpace(clientId) == "" {
		return errors.New("clientId required")
	}
	if strings.TrimSpace(topicFilter) == "" {
		return errors.New("topicFilter required")
	}
	b.mu.Lock()
	set, ok := b.subscriptions[clientId]
	if !ok {
		set = make(map[string]struct{})
		b.subscriptions[clientId] = set
	}
	set[topicFilter] = struct{}{}
	b.mu.Unlock()
	return nil
}

// Matches reports whether topic matches the MQTT topicFilter, honouring the
// single-level "+" and multi-level "#" wildcards. Ports the C# Matches exactly,
// including the trailing length equality check.
func (b *InMemoryMqttBroker) Matches(topic, topicFilter string) bool {
	if topic == "" || topicFilter == "" {
		return false
	}
	t := strings.Split(topic, "/")
	f := strings.Split(topicFilter, "/")
	for i := 0; i < len(f); i++ {
		if f[i] == "#" {
			return true
		}
		if i >= len(t) {
			return false
		}
		if f[i] == "+" {
			continue
		}
		if f[i] != t[i] {
			return false
		}
	}
	return len(t) == len(f)
}

// PublishRetained stores a retained message for its topic (mirrors the C#
// indexer assignment). Panics on empty Topic (the C# ArgumentNullException guard
// is on the record, which cannot be null in Go — an empty topic is the analogue).
func (b *InMemoryMqttBroker) PublishRetained(m MqttRetainedMessage) {
	if m.Topic == "" {
		panic("mqtt retained message requires Topic")
	}
	b.mu.Lock()
	b.retained[m.Topic] = m
	b.mu.Unlock()
}

// GetRetained returns the retained message for topic and true, or a zero value
// and false when absent (mirrors GetValueOrDefault, with the bool making the
// C# nullable explicit).
func (b *InMemoryMqttBroker) GetRetained(topic string) (MqttRetainedMessage, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	m, ok := b.retained[topic]
	return m, ok
}

// MatchingSubscribers returns the clientIds whose subscription filters match
// topic. Ports the C# MatchingSubscribers. Order is unspecified (the C# LINQ
// query over a ConcurrentDictionary is likewise unordered).
func (b *InMemoryMqttBroker) MatchingSubscribers(topic string) []string {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]string, 0)
	for clientId, filters := range b.subscriptions {
		for f := range filters {
			if b.matchesLocked(topic, f) {
				out = append(out, clientId)
				break
			}
		}
	}
	return out
}

// matchesLocked is Matches without touching the lock (callers already hold it).
func (b *InMemoryMqttBroker) matchesLocked(topic, topicFilter string) bool {
	if topic == "" || topicFilter == "" {
		return false
	}
	t := strings.Split(topic, "/")
	f := strings.Split(topicFilter, "/")
	for i := 0; i < len(f); i++ {
		if f[i] == "#" {
			return true
		}
		if i >= len(t) {
			return false
		}
		if f[i] == "+" {
			continue
		}
		if f[i] != t[i] {
			return false
		}
	}
	return len(t) == len(f)
}

// bindSink associates clientId's live delivery sink (used by the transport on
// Start). Passing a nil sink clears it.
func (b *InMemoryMqttBroker) bindSink(clientId string, sink inboundSink) {
	b.mu.Lock()
	if sink == nil {
		delete(b.sinks, clientId)
	} else {
		b.sinks[clientId] = sink
	}
	b.mu.Unlock()
}

// publish routes a payload on topic to every subscriber whose filter matches,
// EXCLUDING the sender's own clientId (an MQTT client subscribed to its own
// inbound tree does not receive its own outbound publishes here — the transport
// publishes to the destination's tree, not its own). Subscribers are snapshotted
// under the lock; delivery onto each sink happens off-lock.
func (b *InMemoryMqttBroker) publish(senderClientId, topic string, payload NetworkPayload) {
	b.mu.Lock()
	targets := make([]inboundSink, 0)
	for clientId, filters := range b.subscriptions {
		if clientId == senderClientId {
			continue
		}
		sink, hasSink := b.sinks[clientId]
		if !hasSink {
			continue
		}
		for f := range filters {
			if b.matchesLocked(topic, f) {
				targets = append(targets, sink)
				break
			}
		}
	}
	b.mu.Unlock()
	for _, sink := range targets {
		sink.Write(payload)
	}
}

// ---------------------------------------------------------------------------
// MqttNetworkTransport — MqttNetworkTransport.cs
// ---------------------------------------------------------------------------

// mqttTopicRoot is the topic prefix the C# transport publishes and subscribes
// under: circle/payloads/...
const mqttTopicRoot = "circle/payloads"

// mqttBroadcastTopic is the topic used when a payload has no DestinationId.
const mqttBroadcastTopic = mqttTopicRoot + "/broadcast"

// MqttNetworkTransport is an INetworkTransport backed by an InMemoryMqttBroker.
// Kind() is TransportKindMqtt; IsAvailable() reflects the connected state (a
// faithful port of the C# `_client.IsConnected`). Start connects to the broker
// and subscribes to circle/payloads/{localClientId}/# (plus the broadcast
// topic, so no-destination sends fan out); Send publishes to
// circle/payloads/{destinationId} (or the broadcast topic) with QoS derived from
// priority; Stop disconnects and completes the inbound stream. Where the C#
// drives MQTTnet, the Go port drives the in-memory broker the rules require.
// Safe for concurrent use.
type MqttNetworkTransport struct {
	broker        *InMemoryMqttBroker
	localClientId string
	descriptor    MqttClientDescriptor

	mu        sync.Mutex
	connected bool
	inbound   *unboundedChannel[NetworkPayload]
}

// NewMqttNetworkTransport builds a transport for clientId over broker. broker is
// required (the injected broker medium). host/port/username/password mirror the
// C# constructor parameters; they populate the MqttClientDescriptor recorded on
// the broker at Connect. username may be "" (no credentials).
func NewMqttNetworkTransport(broker *InMemoryMqttBroker, host string, port int, clientId string) (*MqttNetworkTransport, error) {
	if broker == nil {
		return nil, errors.New("mqtt broker required")
	}
	if clientId == "" {
		return nil, errors.New("mqtt clientId required")
	}
	return &MqttNetworkTransport{
		broker:        broker,
		localClientId: clientId,
		descriptor: MqttClientDescriptor{
			ClientId:  clientId,
			Host:      host,
			Port:      port,
			UseTls:    false,
			KeepAlive: 0,
		},
		inbound: newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindMqtt.
func (t *MqttNetworkTransport) Kind() TransportKind { return TransportKindMqtt }

// IsAvailable reports whether the transport is connected to the broker (matches
// the C# `_client.IsConnected`).
func (t *MqttNetworkTransport) IsAvailable() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.connected
}

// InboundTopicFilter is the subscription this transport establishes on Start —
// circle/payloads/{localClientId}/# — exposed for assertions/tooling.
func (t *MqttNetworkTransport) InboundTopicFilter() string {
	return mqttTopicRoot + "/" + t.localClientId + "/#"
}

// QosFor maps a payload priority to the QoS the C# SendAsync selects: High+
// (High/Urgent/Emergency) -> ExactlyOnce, otherwise AtLeastOnce.
func QosFor(priority MessagePriority) MqttQos {
	if priority >= MessagePriorityHigh {
		return MqttQosExactlyOnce
	}
	return MqttQosAtLeastOnce
}

// Start connects the client to the broker and subscribes to the local inbound
// tree. Idempotent. Mirrors the C# StartAsync (ConnectAsync + SubscribeAsync).
func (t *MqttNetworkTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.connected {
		t.mu.Unlock()
		return nil
	}
	t.inbound = newUnboundedChannel[NetworkPayload]()
	inbound := t.inbound
	t.connected = true
	t.mu.Unlock()

	t.broker.Connect(t.descriptor)
	t.broker.bindSink(t.localClientId, inbound)
	// Subscribe to the local inbound tree. Also subscribe to the broadcast topic
	// so payloads sent with no DestinationId reach every started client.
	if err := t.broker.Subscribe(t.localClientId, t.InboundTopicFilter()); err != nil {
		return err
	}
	if err := t.broker.Subscribe(t.localClientId, mqttBroadcastTopic); err != nil {
		return err
	}
	return nil
}

// Stop disconnects the client from the broker and completes the inbound stream
// so active Receive streams drain and close. Idempotent. Mirrors the C#
// StopAsync (DisconnectAsync + TryComplete).
func (t *MqttNetworkTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.connected {
		t.mu.Unlock()
		return nil
	}
	t.connected = false
	inbound := t.inbound
	t.mu.Unlock()

	t.broker.bindSink(t.localClientId, nil)
	t.broker.Disconnect(t.localClientId)
	inbound.Complete()
	return nil
}

// Send publishes payload to circle/payloads/{destinationId} (or the broadcast
// topic when DestinationId is empty) at the priority-derived QoS, routing it to
// every broker subscriber whose filter matches. Returns an error if the
// transport is not connected or ctx is cancelled. Mirrors the C# SendAsync topic
// selection and QoS mapping.
func (t *MqttNetworkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	connected := t.connected
	t.mu.Unlock()
	if !connected {
		return errors.New("mqtt transport not connected")
	}

	topic := mqttBroadcastTopic
	if payload.DestinationID != "" {
		topic = mqttTopicRoot + "/" + payload.DestinationID
	}
	_ = QosFor(payload.Priority) // QoS is selected exactly as C# does; the in-memory broker delivers identically at any QoS.
	t.broker.publish(t.localClientId, topic, payload)
	return nil
}

// Receive returns a stream of inbound payloads. Messages delivered before this
// call are replayed first (unbounded buffering). The stream closes on ctx
// cancellation or Stop.
func (t *MqttNetworkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbound := t.inbound
	t.mu.Unlock()
	return inbound.ReadAll(ctx)
}

var _ INetworkTransport = (*MqttNetworkTransport)(nil)
