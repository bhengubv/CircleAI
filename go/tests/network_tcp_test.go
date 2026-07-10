// network_tcp_test.go
//
// Verifies network_tcp.go:
//   - TcpConnectionState ordinals + String
//   - TcpKnownPorts constants
//   - FrameTcpPayload / ParseTcpFrame wire format (4-byte LE length prefix)
//     round-trip + truncation errors
//   - InMemoryTcpConnectionRegistry: register/get, state default+set,
//     TotalBytesSent
//   - TcpNetworkTransport: lifecycle + connection-state transitions,
//     same-endpoint fan-out (loopback + cross-endpoint excluded),
//     buffered-before-subscribe, Send-before-Start error, throughput accounting

package circleai_test

import (
	"context"
	"encoding/binary"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestTcpConnectionState_Ordinals(t *testing.T) {
	cases := []struct {
		s    circleai.TcpConnectionState
		ord  int
		name string
	}{
		{circleai.TcpConnectionStateDisconnected, 0, "Disconnected"},
		{circleai.TcpConnectionStateConnecting, 1, "Connecting"},
		{circleai.TcpConnectionStateConnected, 2, "Connected"},
		{circleai.TcpConnectionStateClosing, 3, "Closing"},
		{circleai.TcpConnectionStateFailed, 4, "Failed"},
	}
	for _, c := range cases {
		if int(c.s) != c.ord || c.s.String() != c.name {
			t.Errorf("%s: ord=%d str=%q", c.name, int(c.s), c.s.String())
		}
	}
}

func TestTcpKnownPorts(t *testing.T) {
	if circleai.TcpPortHttp != 80 || circleai.TcpPortHttps != 443 || circleai.TcpPortSsh != 22 ||
		circleai.TcpPortMqtt != 1883 || circleai.TcpPortMqttSsl != 8883 || circleai.TcpPortImapSsl != 993 {
		t.Error("TcpKnownPorts constants wrong")
	}
}

func TestTcpFraming_RoundTrip(t *testing.T) {
	payload := []byte("hello world")
	frame := circleai.FrameTcpPayload(payload)
	// Prefix must be the little-endian length.
	if got := binary.LittleEndian.Uint32(frame[:4]); int(got) != len(payload) {
		t.Errorf("length prefix = %d want %d", got, len(payload))
	}
	if len(frame) != 4+len(payload) {
		t.Errorf("frame len = %d want %d", len(frame), 4+len(payload))
	}
	data, consumed, err := circleai.ParseTcpFrame(frame)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "hello world" {
		t.Errorf("parsed data = %q", string(data))
	}
	if consumed != len(frame) {
		t.Errorf("consumed = %d want %d", consumed, len(frame))
	}
}

func TestTcpFraming_Empty(t *testing.T) {
	frame := circleai.FrameTcpPayload(nil)
	if len(frame) != 4 {
		t.Errorf("empty frame len = %d want 4", len(frame))
	}
	data, consumed, err := circleai.ParseTcpFrame(frame)
	if err != nil || len(data) != 0 || consumed != 4 {
		t.Errorf("empty round-trip: data=%v consumed=%d err=%v", data, consumed, err)
	}
}

func TestTcpFraming_Truncated(t *testing.T) {
	if _, _, err := circleai.ParseTcpFrame([]byte{1, 2}); err == nil {
		t.Error("truncated prefix should error")
	}
	// Declares length 10 but only 3 body bytes present.
	bad := []byte{10, 0, 0, 0, 1, 2, 3}
	if _, _, err := circleai.ParseTcpFrame(bad); err == nil {
		t.Error("truncated body should error")
	}
}

func TestTcpRegistry(t *testing.T) {
	r := circleai.NewInMemoryTcpConnectionRegistry()
	desc := circleai.TcpEndpointDescriptor{Host: "h", Port: 443, NoDelay: true}
	r.Register("e1", desc)
	if got, ok := r.Get("e1"); !ok || got.Port != 443 {
		t.Errorf("Get = %+v ok=%v", got, ok)
	}
	if r.State("e1") != circleai.TcpConnectionStateDisconnected {
		t.Error("default state should be Disconnected")
	}
	r.SetState("e1", circleai.TcpConnectionStateConnected)
	if r.State("e1") != circleai.TcpConnectionStateConnected {
		t.Error("state after set")
	}
	r.RecordSample(circleai.TcpThroughputSample{EndpointId: "e1", BytesSent: 100})
	r.RecordSample(circleai.TcpThroughputSample{EndpointId: "e1", BytesSent: 50})
	r.RecordSample(circleai.TcpThroughputSample{EndpointId: "other", BytesSent: 999})
	if got := r.TotalBytesSent("e1"); got != 150 {
		t.Errorf("TotalBytesSent = %d want 150", got)
	}
}

func TestTcpTransport_LifecycleAndState(t *testing.T) {
	fab := circleai.NewTcpFabric(nil)
	desc := circleai.TcpEndpointDescriptor{Host: "127.0.0.1", Port: 9000}
	tr, err := circleai.NewTcpClientTransport(desc, fab)
	if err != nil {
		t.Fatal(err)
	}
	if tr.Kind() != circleai.TransportKindTcp {
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
	if fab.Registry.State(tr.EndpointKey()) != circleai.TcpConnectionStateConnected {
		t.Error("endpoint should be Connected after Start")
	}
	_ = tr.Stop(context.Background())
	if fab.Registry.State(tr.EndpointKey()) != circleai.TcpConnectionStateDisconnected {
		t.Error("endpoint should be Disconnected after Stop")
	}
}

func TestTcpTransport_EndpointScopedFanOut(t *testing.T) {
	fab := circleai.NewTcpFabric(nil)
	descX := circleai.TcpEndpointDescriptor{Host: "10.0.0.1", Port: 5000}
	descY := circleai.TcpEndpointDescriptor{Host: "10.0.0.2", Port: 5000}
	a, _ := circleai.NewTcpClientTransport(descX, fab)
	b, _ := circleai.NewTcpListenerTransport(descX, fab) // same endpoint key -> connected pair
	other, _ := circleai.NewTcpClientTransport(descY, fab)
	for _, tr := range []*circleai.TcpNetworkTransport{a, b, other} {
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
		t.Errorf("same-endpoint peer got %q", got)
	}
	expectNoPayload(t, aStream)     // loopback excluded
	expectNoPayload(t, otherStream) // different endpoint excluded

	// Sender's framed bytes accounted: 4-byte prefix + 5-byte body = 9.
	if got := fab.Registry.TotalBytesSent(a.EndpointKey()); got != 9 {
		t.Errorf("TotalBytesSent = %d want 9", got)
	}
}

func TestTcpTransport_BufferedBeforeSubscribe(t *testing.T) {
	fab := circleai.NewTcpFabric(nil)
	desc := circleai.TcpEndpointDescriptor{Host: "h", Port: 7}
	a, _ := circleai.NewTcpClientTransport(desc, fab)
	b, _ := circleai.NewTcpListenerTransport(desc, fab)
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

func TestTcpTransport_Guards(t *testing.T) {
	if _, err := circleai.NewTcpClientTransport(circleai.TcpEndpointDescriptor{Port: 1}, nil); err == nil {
		t.Error("nil fabric should be rejected")
	}
	fab := circleai.NewTcpFabric(nil)
	if _, err := circleai.NewTcpClientTransport(circleai.TcpEndpointDescriptor{Host: "h", Port: 0}, fab); err == nil {
		t.Error("non-positive port should be rejected")
	}
}
