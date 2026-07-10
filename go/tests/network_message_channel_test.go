// network_message_channel_test.go
//
// Verifies network_message_channel.go: the JSON codec, TransportMessageChannel
// over an INetworkTransport, and the generic SendMessage[T]/ReceiveMessages[T]
// sugar that reproduces the C# SendAsync<T>/ReceiveAsync<T> surface — including
// typed round-trip, priority stamping, source stamping, and mixed-type skip.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

type chatMsg struct {
	From string `json:"from"`
	Text string `json:"text"`
	Seq  int    `json:"seq"`
}

// wireChannels builds two message channels (sender/receiver) over a shared
// WiFi fabric so messages sent on one arrive on the other.
func wireChannels(t *testing.T) (*circleai.TransportMessageChannel, *circleai.TransportMessageChannel, func()) {
	t.Helper()
	fab := circleai.NewInMemoryTransportFabric()
	txA, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	txB, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	if err := txA.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := txB.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	chA, err := circleai.NewTransportMessageChannel(txA, nil, "node-A")
	if err != nil {
		t.Fatal(err)
	}
	chB, err := circleai.NewTransportMessageChannel(txB, nil, "node-B")
	if err != nil {
		t.Fatal(err)
	}
	cleanup := func() {
		_ = txA.Stop(context.Background())
		_ = txB.Stop(context.Background())
	}
	return chA, chB, cleanup
}

func TestJSONMessageCodec_RoundTrip(t *testing.T) {
	c := circleai.JSONMessageCodec{}
	if c.ContentType() != "application/json" {
		t.Errorf("ContentType got %q", c.ContentType())
	}
	data, err := c.Encode(chatMsg{From: "a", Text: "hi", Seq: 7})
	if err != nil {
		t.Fatal(err)
	}
	var back chatMsg
	if err := c.Decode(data, &back); err != nil {
		t.Fatal(err)
	}
	if back != (chatMsg{From: "a", Text: "hi", Seq: 7}) {
		t.Errorf("round-trip got %+v", back)
	}
}

func TestMessageChannel_TypedRoundTrip(t *testing.T) {
	chA, chB, cleanup := wireChannels(t)
	defer cleanup()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Subscribe the receiver BEFORE sending.
	msgs, errs := circleai.ReceiveMessages[chatMsg](ctx, chB)

	if err := circleai.SendMessage(ctx, chA, "node-B", chatMsg{From: "node-A", Text: "hello", Seq: 1}); err != nil {
		t.Fatal(err)
	}

	select {
	case got := <-msgs:
		if got.From != "node-A" || got.Text != "hello" || got.Seq != 1 {
			t.Errorf("received %+v", got)
		}
	case err := <-errs:
		t.Fatalf("unexpected decode error: %v", err)
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for typed message")
	}
}

func TestMessageChannel_SendObjectStampsSourceAndPriority(t *testing.T) {
	fab := circleai.NewInMemoryTransportFabric()
	txA, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	txB, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	_ = txA.Start(context.Background())
	_ = txB.Start(context.Background())
	defer txA.Stop(context.Background())
	defer txB.Stop(context.Background())

	base, _ := circleai.NewTransportMessageChannel(txA, nil, "node-A")
	ch := base.WithPriority(circleai.MessagePriorityUrgent)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	raw := txB.Receive(ctx)

	if err := ch.SendObject(ctx, "node-B", chatMsg{From: "node-A", Text: "urgent"}); err != nil {
		t.Fatal(err)
	}

	select {
	case p := <-raw:
		if p.SourceID != "node-A" {
			t.Errorf("payload SourceID got %q want node-A", p.SourceID)
		}
		if p.DestinationID != "node-B" {
			t.Errorf("payload DestinationID got %q want node-B", p.DestinationID)
		}
		if p.Priority != circleai.MessagePriorityUrgent {
			t.Errorf("payload Priority got %v want Urgent", p.Priority)
		}
		if p.ContentType != "application/json" {
			t.Errorf("payload ContentType got %q want application/json", p.ContentType)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for raw payload")
	}
}

func TestMessageChannel_MixedContentTypeSkipped(t *testing.T) {
	// A JSON-typed reader must skip a frame carrying a different content type,
	// rather than fail to decode it. We inject a raw octet-stream payload onto
	// the same fabric and confirm the JSON-typed stream ignores it, then
	// delivers a following JSON message.
	fab := circleai.NewInMemoryTransportFabric()
	txSend, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	txRecv, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	_ = txSend.Start(context.Background())
	_ = txRecv.Start(context.Background())
	defer txSend.Stop(context.Background())
	defer txRecv.Stop(context.Background())

	recvCh, _ := circleai.NewTransportMessageChannel(txRecv, nil, "R")
	sendCh, _ := circleai.NewTransportMessageChannel(txSend, nil, "S")

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	msgs, errs := circleai.ReceiveMessages[chatMsg](ctx, recvCh)

	// 1) A non-JSON frame that should be skipped (not decoded, no error).
	octet := circleai.NewNetworkPayloadWith([]byte("not-json"), "R", circleai.MessagePriorityNormal, "application/octet-stream", nil)
	if err := txSend.Send(ctx, octet); err != nil {
		t.Fatal(err)
	}
	// 2) A proper JSON message that must arrive.
	if err := circleai.SendMessage(ctx, sendCh, "R", chatMsg{From: "S", Text: "ok", Seq: 9}); err != nil {
		t.Fatal(err)
	}

	select {
	case got := <-msgs:
		if got.Seq != 9 || got.Text != "ok" {
			t.Errorf("expected the JSON message after skipping the octet frame, got %+v", got)
		}
	case err := <-errs:
		t.Fatalf("mixed content type should be skipped, not error: %v", err)
	case <-time.After(2 * time.Second):
		t.Fatal("timed out; the octet frame may have blocked the JSON message")
	}
}

func TestMessageChannel_NilTransportRejected(t *testing.T) {
	if _, err := circleai.NewTransportMessageChannel(nil, nil, ""); err == nil {
		t.Error("nil transport should be rejected")
	}
}

func TestMessageChannel_SendNilMessageRejected(t *testing.T) {
	fab := circleai.NewInMemoryTransportFabric()
	tx, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	_ = tx.Start(context.Background())
	defer tx.Stop(context.Background())
	ch, _ := circleai.NewTransportMessageChannel(tx, nil, "")
	if err := ch.SendObject(context.Background(), "d", nil); err == nil {
		t.Error("SendObject(nil) should be rejected")
	}
}
