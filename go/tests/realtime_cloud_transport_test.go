// realtime_cloud_transport_test.go
//
// Verifies the CircleAI.Realtime.Cloud transport port (realtime_cloud_transport.go):
// the NullRealtimeTransportFactory error, and the deterministic in-memory
// transport pair — duplex text/binary delivery, buffered-before-receive, binary
// copy isolation, IsOpen/Close semantics, send-after-close error, and the
// in-memory factory Connect/LastPeer.

package circleai_test

import (
	"context"
	"net/url"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func rtRecvString(t *testing.T, ch <-chan string) (string, bool) {
	t.Helper()
	select {
	case s, ok := <-ch:
		return s, ok
	case <-time.After(2 * time.Second):
		t.Fatalf("timed out waiting for text frame")
		return "", false
	}
}

func rtRecvBytes(t *testing.T, ch <-chan []byte) ([]byte, bool) {
	t.Helper()
	select {
	case b, ok := <-ch:
		return b, ok
	case <-time.After(2 * time.Second):
		t.Fatalf("timed out waiting for binary frame")
		return nil, false
	}
}

func TestNullRealtimeTransportFactory_Errors(t *testing.T) {
	f := circleai.NullRealtimeTransportFactoryInstance
	u, _ := url.Parse("wss://example.test/rt")
	if _, err := f.Connect(context.Background(), u, nil); err == nil {
		t.Fatalf("null factory Connect must error")
	}
}

func TestInMemoryTransportPair_Duplex(t *testing.T) {
	ctx := context.Background()
	a, b := circleai.NewInMemoryRealtimeTransportPair()
	defer a.Close(ctx)
	defer b.Close(ctx)

	if !a.IsOpen() || !b.IsOpen() {
		t.Fatalf("pair should start open")
	}

	aRecv := a.ReceiveText(ctx)
	bRecv := b.ReceiveText(ctx)

	if err := a.SendText(ctx, "a->b"); err != nil {
		t.Fatalf("a send: %v", err)
	}
	if err := b.SendText(ctx, "b->a"); err != nil {
		t.Fatalf("b send: %v", err)
	}

	if s, ok := rtRecvString(t, bRecv); !ok || s != "a->b" {
		t.Fatalf("b received %q ok=%v", s, ok)
	}
	if s, ok := rtRecvString(t, aRecv); !ok || s != "b->a" {
		t.Fatalf("a received %q ok=%v", s, ok)
	}
}

func TestInMemoryTransport_BufferBeforeReceive(t *testing.T) {
	// Frames sent before the peer starts receiving must be buffered, not lost.
	ctx := context.Background()
	a, b := circleai.NewInMemoryRealtimeTransportPair()
	defer a.Close(ctx)
	defer b.Close(ctx)

	_ = a.SendText(ctx, "first")
	_ = a.SendText(ctx, "second")

	bRecv := b.ReceiveText(ctx)
	if s, _ := rtRecvString(t, bRecv); s != "first" {
		t.Fatalf("frame 1 = %q", s)
	}
	if s, _ := rtRecvString(t, bRecv); s != "second" {
		t.Fatalf("frame 2 = %q", s)
	}
}

func TestInMemoryTransport_BinaryCopyIsolation(t *testing.T) {
	ctx := context.Background()
	a, b := circleai.NewInMemoryRealtimeTransportPair()
	defer a.Close(ctx)
	defer b.Close(ctx)

	bRecv := b.ReceiveBinary(ctx)
	payload := []byte{1, 2, 3}
	if err := a.SendBinary(ctx, payload); err != nil {
		t.Fatalf("send binary: %v", err)
	}
	// Mutate the caller's buffer after send; the queued frame must be unaffected.
	payload[0] = 99

	got, ok := rtRecvBytes(t, bRecv)
	if !ok || len(got) != 3 || got[0] != 1 || got[1] != 2 || got[2] != 3 {
		t.Fatalf("binary frame corrupted: %v (ok=%v)", got, ok)
	}
}

func TestInMemoryTransport_CloseSemantics(t *testing.T) {
	ctx := context.Background()
	a, b := circleai.NewInMemoryRealtimeTransportPair()

	bRecv := b.ReceiveText(ctx)

	if err := a.Close(ctx); err != nil {
		t.Fatalf("close: %v", err)
	}
	_ = a.Close(ctx) // idempotent

	// Both endpoints observe closed via the shared flag.
	if a.IsOpen() || b.IsOpen() {
		t.Fatalf("both endpoints should read closed after either closes")
	}
	// Send after close errors.
	if err := a.SendText(ctx, "late"); err == nil {
		t.Fatalf("send after close must error")
	}
	// b's receive stream drains and closes.
	select {
	case _, ok := <-bRecv:
		if ok {
			t.Fatalf("peer receive should be closed after close")
		}
	case <-time.After(2 * time.Second):
		t.Fatalf("peer receive did not close")
	}
}

func TestInMemoryTransport_CloseConnAlias(t *testing.T) {
	ctx := context.Background()
	a, _ := circleai.NewInMemoryRealtimeTransportPair()
	if err := a.CloseConn(ctx); err != nil {
		t.Fatalf("closeconn: %v", err)
	}
	if a.IsOpen() {
		t.Fatalf("CloseConn should close the transport")
	}
}

func TestInMemoryTransportFactory_ConnectAndPeer(t *testing.T) {
	ctx := context.Background()
	f := circleai.NewInMemoryRealtimeTransportFactory()

	if _, ok := f.LastPeer(); ok {
		t.Fatalf("no peer before Connect")
	}
	if _, err := f.Connect(ctx, nil, nil); err == nil {
		t.Fatalf("nil endpoint must error")
	}

	u, _ := url.Parse("wss://vendor.test/rt")
	client, err := f.Connect(ctx, u, map[string]string{"Authorization": "Bearer x"})
	if err != nil {
		t.Fatalf("connect: %v", err)
	}
	server, ok := f.LastPeer()
	if !ok {
		t.Fatalf("peer should exist after Connect")
	}
	defer client.Close(ctx)
	defer server.Close(ctx)

	// Client<->server are wired: server injects a "vendor" frame the client reads.
	cRecv := client.ReceiveText(ctx)
	if err := server.SendText(ctx, "vendor-hello"); err != nil {
		t.Fatalf("server send: %v", err)
	}
	if s, _ := rtRecvString(t, cRecv); s != "vendor-hello" {
		t.Fatalf("client received %q", s)
	}
}

func TestRealtimeTransport_InterfacesSatisfied(t *testing.T) {
	var _ circleai.IRealtimeTransportFactory = circleai.NullRealtimeTransportFactoryInstance
	var _ circleai.IRealtimeTransportFactory = circleai.NewInMemoryRealtimeTransportFactory()
	a, _ := circleai.NewInMemoryRealtimeTransportPair()
	var _ circleai.IRealtimeTransport = a
}
