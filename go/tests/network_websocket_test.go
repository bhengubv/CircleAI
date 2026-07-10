// network_websocket_test.go
//
// Verifies network_websocket.go:
//   - WebSocketLinkState ordinals + String (incl. Closed_Error member name)
//   - WebSocketMessageType ordinals + String
//   - InMemoryWebSocketSessionRegistry: register/get, state default+set,
//     TotalBytes, FrameCount by type
//   - WebSocketTransport: lifecycle + link-state transitions,
//     same-Uri fan-out (loopback + cross-Uri excluded),
//     buffered-before-subscribe, Send-before-Start error,
//     Binary frame accounting + Close frame on Stop, constructor guards

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestWebSocketLinkState_Ordinals(t *testing.T) {
	cases := []struct {
		s    circleai.WebSocketLinkState
		ord  int
		name string
	}{
		{circleai.WebSocketLinkStateClosed, 0, "Closed"},
		{circleai.WebSocketLinkStateConnecting, 1, "Connecting"},
		{circleai.WebSocketLinkStateOpen, 2, "Open"},
		{circleai.WebSocketLinkStateCloseSent, 3, "CloseSent"},
		{circleai.WebSocketLinkStateCloseReceived, 4, "CloseReceived"},
		{circleai.WebSocketLinkStateClosedError, 5, "Closed_Error"},
	}
	for _, c := range cases {
		if int(c.s) != c.ord || c.s.String() != c.name {
			t.Errorf("%s: ord=%d str=%q", c.name, int(c.s), c.s.String())
		}
	}
}

func TestWebSocketMessageType_Ordinals(t *testing.T) {
	cases := []struct {
		m    circleai.WebSocketMessageType
		ord  int
		name string
	}{
		{circleai.WebSocketMessageTypeText, 0, "Text"},
		{circleai.WebSocketMessageTypeBinary, 1, "Binary"},
		{circleai.WebSocketMessageTypePing, 2, "Ping"},
		{circleai.WebSocketMessageTypePong, 3, "Pong"},
		{circleai.WebSocketMessageTypeClose, 4, "Close"},
	}
	for _, c := range cases {
		if int(c.m) != c.ord || c.m.String() != c.name {
			t.Errorf("%s: ord=%d str=%q", c.name, int(c.m), c.m.String())
		}
	}
}

func TestWebSocketRegistry(t *testing.T) {
	r := circleai.NewInMemoryWebSocketSessionRegistry()
	desc := circleai.WebSocketEndpointDescriptor{Uri: "wss://x"}
	r.Register("s1", desc)
	if got, ok := r.Get("s1"); !ok || got.Uri != "wss://x" {
		t.Errorf("Get = %+v ok=%v", got, ok)
	}
	if r.State("s1") != circleai.WebSocketLinkStateClosed {
		t.Error("default state should be Closed")
	}
	r.SetState("s1", circleai.WebSocketLinkStateOpen)
	if r.State("s1") != circleai.WebSocketLinkStateOpen {
		t.Error("state after set")
	}
	r.RecordFrame(circleai.WebSocketFrameSummary{SessionId: "s1", Type: circleai.WebSocketMessageTypeBinary, Bytes: 10})
	r.RecordFrame(circleai.WebSocketFrameSummary{SessionId: "s1", Type: circleai.WebSocketMessageTypeBinary, Bytes: 5})
	r.RecordFrame(circleai.WebSocketFrameSummary{SessionId: "s1", Type: circleai.WebSocketMessageTypeClose, Bytes: 0})
	if got := r.TotalBytes("s1"); got != 15 {
		t.Errorf("TotalBytes = %d want 15", got)
	}
	if got := r.FrameCount("s1", circleai.WebSocketMessageTypeBinary); got != 2 {
		t.Errorf("FrameCount Binary = %d want 2", got)
	}
	if got := r.FrameCount("s1", circleai.WebSocketMessageTypeClose); got != 1 {
		t.Errorf("FrameCount Close = %d want 1", got)
	}
}

func TestWebSocketTransport_LifecycleAndState(t *testing.T) {
	fab := circleai.NewWebSocketFabric(nil)
	tr, err := circleai.NewWebSocketTransport("wss://host/path", fab)
	if err != nil {
		t.Fatal(err)
	}
	if tr.Kind() != circleai.TransportKindWebSocket {
		t.Errorf("Kind = %v", tr.Kind())
	}
	if tr.IsAvailable() {
		t.Error("not available before Start")
	}
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload(nil, "")); err == nil {
		t.Error("Send before Start should error")
	}
	_ = tr.Start(context.Background())
	if !tr.IsAvailable() {
		t.Error("available after Start")
	}
	if fab.Registry.State(tr.SessionId()) != circleai.WebSocketLinkStateOpen {
		t.Error("link should be Open after Start")
	}
	_ = tr.Stop(context.Background())
	if fab.Registry.State(tr.SessionId()) != circleai.WebSocketLinkStateClosed {
		t.Error("link should be Closed after Stop")
	}
	// A Close frame should have been logged on Stop.
	if fab.Registry.FrameCount(tr.SessionId(), circleai.WebSocketMessageTypeClose) != 1 {
		t.Error("Stop should log a Close frame")
	}
}

func TestWebSocketTransport_UriScopedFanOut(t *testing.T) {
	fab := circleai.NewWebSocketFabric(nil)
	a, _ := circleai.NewWebSocketTransportWithSession("wss://hub", fab, "A")
	b, _ := circleai.NewWebSocketTransportWithSession("wss://hub", fab, "B")
	other, _ := circleai.NewWebSocketTransportWithSession("wss://elsewhere", fab, "O")
	for _, tr := range []*circleai.WebSocketTransport{a, b, other} {
		_ = tr.Start(context.Background())
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)
	aStream := a.Receive(rctx)
	otherStream := other.Receive(rctx)

	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("frame"), "")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "frame" {
		t.Errorf("same-Uri peer got %q", got)
	}
	expectNoPayload(t, aStream)     // loopback excluded
	expectNoPayload(t, otherStream) // different Uri excluded

	// The Binary frame should be accounted for the sender session.
	if got := fab.Registry.FrameCount("A", circleai.WebSocketMessageTypeBinary); got != 1 {
		t.Errorf("expected 1 Binary frame logged for A, got %d", got)
	}
	if got := fab.Registry.TotalBytes("A"); got != 5 {
		t.Errorf("TotalBytes(A) = %d want 5", got)
	}
}

func TestWebSocketTransport_BufferedBeforeSubscribe(t *testing.T) {
	fab := circleai.NewWebSocketFabric(nil)
	a, _ := circleai.NewWebSocketTransportWithSession("wss://h", fab, "A")
	b, _ := circleai.NewWebSocketTransportWithSession("wss://h", fab, "B")
	_ = a.Start(context.Background())
	_ = b.Start(context.Background())
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("early"), "")); err != nil {
		t.Fatal(err)
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if got := string(recvOne(t, b.Receive(rctx)).Data); got != "early" {
		t.Errorf("buffered frame lost: %q", got)
	}
}

func TestWebSocketTransport_Guards(t *testing.T) {
	if _, err := circleai.NewWebSocketTransport("wss://x", nil); err == nil {
		t.Error("nil fabric should be rejected")
	}
	fab := circleai.NewWebSocketFabric(nil)
	if _, err := circleai.NewWebSocketTransport("", fab); err == nil {
		t.Error("empty endpoint should be rejected")
	}
}
