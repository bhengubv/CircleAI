// network_transport_test.go
//
// Verifies network_transport.go InMemoryNetworkTransport + fabric:
//   - Start/Stop/IsAvailable lifecycle
//   - Send fails before Start
//   - Fabric fan-out: same-kind peers receive, sender does not loop back,
//     cross-kind peers are isolated
//   - A payload published BEFORE a Receive consumer attaches is buffered, not
//     lost (unbounded-buffer guarantee)
//   - Stop completes the Receive stream

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// recvOne reads one payload from ch or fails after a timeout.
func recvOne(t *testing.T, ch <-chan circleai.NetworkPayload) circleai.NetworkPayload {
	t.Helper()
	select {
	case p, ok := <-ch:
		if !ok {
			t.Fatal("channel closed before a payload arrived")
		}
		return p
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for a payload")
		return circleai.NetworkPayload{}
	}
}

// expectNoPayload asserts nothing arrives within a short window.
func expectNoPayload(t *testing.T, ch <-chan circleai.NetworkPayload) {
	t.Helper()
	select {
	case p, ok := <-ch:
		if ok {
			t.Fatalf("expected no payload, got id=%s data=%q", p.ID, string(p.Data))
		}
	case <-time.After(150 * time.Millisecond):
	}
}

func TestTransport_Lifecycle(t *testing.T) {
	fab := circleai.NewInMemoryTransportFabric()
	tr, err := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	if err != nil {
		t.Fatal(err)
	}
	if tr.Kind() != circleai.TransportKindWiFi {
		t.Errorf("Kind got %v", tr.Kind())
	}
	if tr.IsAvailable() {
		t.Error("transport should not be available before Start")
	}
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload(nil, "")); err == nil {
		t.Error("Send before Start should error")
	}
	if err := tr.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	if !tr.IsAvailable() {
		t.Error("transport should be available after Start")
	}
	// Start is idempotent.
	if err := tr.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := tr.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	if tr.IsAvailable() {
		t.Error("transport should not be available after Stop")
	}
	// Stop is idempotent.
	if err := tr.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestTransport_NilFabricRejected(t *testing.T) {
	if _, err := circleai.NewInMemoryNetworkTransport(circleai.TransportKindTcp, nil); err == nil {
		t.Error("nil fabric should be rejected")
	}
}

func TestTransport_FabricFanOut(t *testing.T) {
	fab := circleai.NewInMemoryTransportFabric()
	a, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	b, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	c, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	for _, tr := range []*circleai.InMemoryNetworkTransport{a, b, c} {
		if err := tr.Start(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Subscribe b and c BEFORE a sends (synchronous subscription).
	bStream := b.Receive(ctx)
	cStream := c.Receive(ctx)
	aStream := a.Receive(ctx)

	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("ping"), "")); err != nil {
		t.Fatal(err)
	}

	if got := string(recvOne(t, bStream).Data); got != "ping" {
		t.Errorf("b got %q want ping", got)
	}
	if got := string(recvOne(t, cStream).Data); got != "ping" {
		t.Errorf("c got %q want ping", got)
	}
	// Sender must NOT receive its own broadcast (loopback excluded).
	expectNoPayload(t, aStream)
}

func TestTransport_CrossKindIsolation(t *testing.T) {
	fab := circleai.NewInMemoryTransportFabric()
	wifi, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	blue, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindBluetooth, fab)
	_ = wifi.Start(context.Background())
	_ = blue.Start(context.Background())
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	blueStream := blue.Receive(ctx)
	if err := wifi.Send(context.Background(), circleai.NewNetworkPayload([]byte("x"), "")); err != nil {
		t.Fatal(err)
	}
	// Different kind => must not receive.
	expectNoPayload(t, blueStream)
}

func TestTransport_BufferedBeforeSubscribe(t *testing.T) {
	// The unbounded-buffer guarantee: a payload delivered before any Receive
	// consumer attaches must be replayed, not dropped.
	fab := circleai.NewInMemoryTransportFabric()
	a, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindNearLink, fab)
	b, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindNearLink, fab)
	_ = a.Start(context.Background())
	_ = b.Start(context.Background())

	// Send to b before b has any Receive stream.
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("early"), "")); err != nil {
		t.Fatal(err)
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	// Now attach — the early payload must still be there.
	if got := string(recvOne(t, b.Receive(ctx)).Data); got != "early" {
		t.Errorf("buffered payload lost: got %q want early", got)
	}
}

func TestTransport_StopCompletesReceiveStream(t *testing.T) {
	fab := circleai.NewInMemoryTransportFabric()
	tr, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindTcp, fab)
	_ = tr.Start(context.Background())
	stream := tr.Receive(context.Background())
	_ = tr.Stop(context.Background())
	select {
	case _, ok := <-stream:
		if ok {
			t.Error("expected stream to close after Stop")
		}
	case <-time.After(2 * time.Second):
		t.Error("stream did not close after Stop")
	}
}

func TestTransport_ContextCancelClosesReceive(t *testing.T) {
	fab := circleai.NewInMemoryTransportFabric()
	tr, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindUdp, fab)
	_ = tr.Start(context.Background())
	ctx, cancel := context.WithCancel(context.Background())
	stream := tr.Receive(ctx)
	cancel()
	select {
	case _, ok := <-stream:
		if ok {
			t.Error("expected stream to close on ctx cancel")
		}
	case <-time.After(2 * time.Second):
		t.Error("stream did not close on ctx cancel")
	}
}
