// network_mqtt_test.go
//
// Verifies network_mqtt.go:
//   - MqttQos ordinals + String
//   - InMemoryMqttBroker: connect/disconnect + ConnectedClients, subscribe,
//     Matches wildcard rules (+ / #), retained store, MatchingSubscribers
//   - MqttNetworkTransport: lifecycle + IsAvailable, topic-scoped delivery
//     (destination tree vs broadcast), buffered-before-subscribe, QosFor mapping,
//     Send-before-Start error, constructor guards

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestMqttQos_Ordinals(t *testing.T) {
	cases := []struct {
		q    circleai.MqttQos
		ord  int
		name string
	}{
		{circleai.MqttQosAtMostOnce, 0, "AtMostOnce"},
		{circleai.MqttQosAtLeastOnce, 1, "AtLeastOnce"},
		{circleai.MqttQosExactlyOnce, 2, "ExactlyOnce"},
	}
	for _, c := range cases {
		if int(c.q) != c.ord {
			t.Errorf("%s ordinal = %d want %d", c.name, int(c.q), c.ord)
		}
		if c.q.String() != c.name {
			t.Errorf("String = %q want %q", c.q.String(), c.name)
		}
	}
}

func TestMqttBroker_Matches(t *testing.T) {
	b := circleai.NewInMemoryMqttBroker()
	cases := []struct {
		topic, filter string
		want          bool
	}{
		{"a/b/c", "a/b/c", true},
		{"a/b/c", "a/+/c", true},
		{"a/b/c", "a/#", true},
		{"a/b/c", "a/b", false},    // filter shorter, no #
		{"a/b", "a/b/c", false},    // topic shorter
		{"a/b/c", "a/+/+", true},   // two single-level wildcards
		{"a/b/c", "+/+/+", true},   // all single-level
		{"a/b/c/d", "a/b/#", true}, // # matches remaining
		{"a/b/c", "a/x/c", false},  // literal mismatch
		{"", "a", false},           // empty topic
		{"a", "", false},           // empty filter
	}
	for _, c := range cases {
		if got := b.Matches(c.topic, c.filter); got != c.want {
			t.Errorf("Matches(%q,%q) = %v want %v", c.topic, c.filter, got, c.want)
		}
	}
}

func TestMqttBroker_ClientsAndSubscriptions(t *testing.T) {
	b := circleai.NewInMemoryMqttBroker()
	b.Connect(circleai.MqttClientDescriptor{ClientId: "c1", Host: "h", Port: 1883})
	b.Connect(circleai.MqttClientDescriptor{ClientId: "c2", Host: "h", Port: 1883})
	if len(b.ConnectedClients()) != 2 {
		t.Errorf("ConnectedClients = %d want 2", len(b.ConnectedClients()))
	}
	b.Disconnect("c1")
	if len(b.ConnectedClients()) != 1 {
		t.Errorf("ConnectedClients after disconnect = %d want 1", len(b.ConnectedClients()))
	}

	if err := b.Subscribe("", "topic"); err == nil {
		t.Error("empty clientId should error")
	}
	if err := b.Subscribe("c2", ""); err == nil {
		t.Error("empty filter should error")
	}
	_ = b.Subscribe("c2", "circle/payloads/c2/#")
	subs := b.MatchingSubscribers("circle/payloads/c2/data")
	if len(subs) != 1 || subs[0] != "c2" {
		t.Errorf("MatchingSubscribers = %v want [c2]", subs)
	}
	if got := b.MatchingSubscribers("other/topic"); len(got) != 0 {
		t.Errorf("MatchingSubscribers(other) = %v want []", got)
	}
}

func TestMqttBroker_Retained(t *testing.T) {
	b := circleai.NewInMemoryMqttBroker()
	if _, ok := b.GetRetained("t"); ok {
		t.Error("no retained message expected")
	}
	m := circleai.MqttRetainedMessage{Topic: "t", Payload: []byte("hi"), RetainedAtUtc: time.Now().UTC()}
	b.PublishRetained(m)
	got, ok := b.GetRetained("t")
	if !ok || string(got.Payload) != "hi" {
		t.Errorf("GetRetained = %+v ok=%v", got, ok)
	}
}

func TestMqttTransport_Lifecycle(t *testing.T) {
	broker := circleai.NewInMemoryMqttBroker()
	tr, err := circleai.NewMqttNetworkTransport(broker, "localhost", 1883, "node-a")
	if err != nil {
		t.Fatal(err)
	}
	if tr.Kind() != circleai.TransportKindMqtt {
		t.Errorf("Kind = %v", tr.Kind())
	}
	if tr.IsAvailable() {
		t.Error("not available before Start")
	}
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload(nil, "x")); err == nil {
		t.Error("Send before Start should error")
	}
	_ = tr.Start(context.Background())
	if !tr.IsAvailable() {
		t.Error("available after Start")
	}
	// The client should now be registered + subscribed to its inbound tree.
	if len(broker.ConnectedClients()) != 1 {
		t.Errorf("broker should have 1 client, got %d", len(broker.ConnectedClients()))
	}
	subs := broker.MatchingSubscribers("circle/payloads/node-a/data")
	if len(subs) != 1 || subs[0] != "node-a" {
		t.Errorf("expected node-a subscribed to inbound tree, got %v", subs)
	}
	_ = tr.Stop(context.Background())
	if tr.IsAvailable() {
		t.Error("not available after Stop")
	}
	if len(broker.ConnectedClients()) != 0 {
		t.Errorf("broker should have 0 clients after Stop, got %d", len(broker.ConnectedClients()))
	}
}

func TestMqttTransport_DestinationRouting(t *testing.T) {
	broker := circleai.NewInMemoryMqttBroker()
	a, _ := circleai.NewMqttNetworkTransport(broker, "h", 1883, "A")
	b, _ := circleai.NewMqttNetworkTransport(broker, "h", 1883, "B")
	c, _ := circleai.NewMqttNetworkTransport(broker, "h", 1883, "C")
	for _, tr := range []*circleai.MqttNetworkTransport{a, b, c} {
		_ = tr.Start(context.Background())
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)
	cStream := c.Receive(rctx)

	// A publishes addressed to B -> only B receives (topic circle/payloads/B).
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("toB"), "B")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "toB" {
		t.Errorf("B got %q want toB", got)
	}
	expectNoPayload(t, cStream) // C is not the destination
}

func TestMqttTransport_Broadcast(t *testing.T) {
	broker := circleai.NewInMemoryMqttBroker()
	a, _ := circleai.NewMqttNetworkTransport(broker, "h", 1883, "A")
	b, _ := circleai.NewMqttNetworkTransport(broker, "h", 1883, "B")
	c, _ := circleai.NewMqttNetworkTransport(broker, "h", 1883, "C")
	for _, tr := range []*circleai.MqttNetworkTransport{a, b, c} {
		_ = tr.Start(context.Background())
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)
	cStream := c.Receive(rctx)
	aStream := a.Receive(rctx)

	// No destination -> broadcast topic -> every OTHER started client receives.
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("all"), "")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "all" {
		t.Errorf("B broadcast got %q", got)
	}
	if got := string(recvOne(t, cStream).Data); got != "all" {
		t.Errorf("C broadcast got %q", got)
	}
	expectNoPayload(t, aStream) // sender excluded
}

func TestMqttTransport_BufferedBeforeSubscribe(t *testing.T) {
	broker := circleai.NewInMemoryMqttBroker()
	a, _ := circleai.NewMqttNetworkTransport(broker, "h", 1883, "A")
	b, _ := circleai.NewMqttNetworkTransport(broker, "h", 1883, "B")
	_ = a.Start(context.Background())
	_ = b.Start(context.Background())
	// Send to B before B subscribes to its Receive stream.
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("early"), "B")); err != nil {
		t.Fatal(err)
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if got := string(recvOne(t, b.Receive(rctx)).Data); got != "early" {
		t.Errorf("buffered frame lost: %q", got)
	}
}

func TestMqttTransport_QosFor(t *testing.T) {
	cases := []struct {
		p    circleai.MessagePriority
		want circleai.MqttQos
	}{
		{circleai.MessagePriorityLow, circleai.MqttQosAtLeastOnce},
		{circleai.MessagePriorityNormal, circleai.MqttQosAtLeastOnce},
		{circleai.MessagePriorityHigh, circleai.MqttQosExactlyOnce},
		{circleai.MessagePriorityUrgent, circleai.MqttQosExactlyOnce},
		{circleai.MessagePriorityEmergency, circleai.MqttQosExactlyOnce},
	}
	for _, c := range cases {
		if got := circleai.QosFor(c.p); got != c.want {
			t.Errorf("QosFor(%v) = %v want %v", c.p, got, c.want)
		}
	}
}

func TestMqttTransport_ConstructorGuards(t *testing.T) {
	if _, err := circleai.NewMqttNetworkTransport(nil, "h", 1883, "c"); err == nil {
		t.Error("nil broker should be rejected")
	}
	b := circleai.NewInMemoryMqttBroker()
	if _, err := circleai.NewMqttNetworkTransport(b, "h", 1883, ""); err == nil {
		t.Error("empty clientId should be rejected")
	}
}
