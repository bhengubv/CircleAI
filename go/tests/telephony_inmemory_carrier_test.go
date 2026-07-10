// telephony_inmemory_carrier_test.go
//
// Verifies the deterministic in-memory ITelephonyCarrier + ICallSession +
// IMediaStream + IInboundCallDispatcher (the hermetic fake fulfilling the
// carrier abstraction with no network). Covers provisioning, webhook config,
// outbound dial + duplex audio/DTMF, status events, cold transfer, hang-up, and
// inbound delivery — including the Wave-1 concurrency guarantees (buffer-before-
// subscribe, subscribe-before-publish).

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func telephonyClock() func() time.Time {
	ts := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	return func() time.Time { return ts }
}

func TestInMemoryCarrier_ProvisionAndWebhook(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryTelephonyCarrier("fake", circleai.WithCarrierClock(telephonyClock()))
	c.AddAvailableNumber("ZA", "21", "+27215550100", circleai.DecimalFromInt(5))

	if c.CarrierID() != "fake" || !c.IsConfigured() {
		t.Fatal("carrier id/configured wrong")
	}

	// Area-code match.
	pn, err := c.ProvisionNumber(ctx, "ZA", "21")
	if err != nil {
		t.Fatalf("provision: %v", err)
	}
	if pn.PhoneNumber != "+27215550100" || pn.CarrierID != "fake" || pn.MonthlyRecurringCost.String() != "5" {
		t.Errorf("provisioned = %+v", pn)
	}

	// No more numbers in that country -> error.
	if _, err := c.ProvisionNumber(ctx, "ZA", "21"); err == nil {
		t.Error("expected no-available-numbers error after consuming the only number")
	}

	// Configure webhook for the owned number.
	wh := mustURL(t, "wss://host.example/inbound")
	if err := c.ConfigureInboundWebhook(ctx, pn.PhoneNumber, wh); err != nil {
		t.Fatalf("configure webhook: %v", err)
	}
	if got, ok := c.WebhookFor(pn.PhoneNumber); !ok || got.String() != "wss://host.example/inbound" {
		t.Errorf("webhook not recorded: %v %v", got, ok)
	}

	// Webhook for a foreign number fails.
	if err := c.ConfigureInboundWebhook(ctx, "+15550000000", wh); err == nil {
		t.Error("configuring a non-owned number should error")
	}

	// ListNumbers reflects the owned number.
	nums, _ := c.ListNumbers(ctx)
	if len(nums) != 1 || nums[0].PhoneNumber != pn.PhoneNumber {
		t.Errorf("ListNumbers = %+v", nums)
	}
}

func TestInMemoryCarrier_DialDuplexAudio(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryTelephonyCarrier("fake", circleai.WithCarrierClock(telephonyClock()))
	stream := mustURL(t, "wss://host/stream")

	sess, err := c.Dial(ctx, "+27000000001", "+27000000002", stream, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer sess.Close(ctx)

	if sess.Info().Direction != circleai.CallDirectionOutbound || sess.Info().To != "+27000000002" {
		t.Errorf("info = %+v", sess.Info())
	}
	if sess.Status() != circleai.CallStatusActive {
		t.Errorf("status = %v, want Active", sess.Status())
	}

	media, ok := c.LastDial()
	if !ok {
		t.Fatal("no last dial")
	}

	// Far end pushes two frames BEFORE the AI subscribes — must be buffered.
	media.PushAudio(circleai.AudioFrame{Pcm: []byte{1, 2}, Format: circleai.CallMediaFormatMulaw8000})
	media.PushAudio(circleai.AudioFrame{Pcm: []byte{3, 4}, Format: circleai.CallMediaFormatMulaw8000})

	rx := sess.ReceiveAudio(ctx)
	f1 := <-rx
	f2 := <-rx
	if len(f1.Pcm) != 2 || f1.Pcm[0] != 1 || f2.Pcm[0] != 3 {
		t.Errorf("buffered frames lost/reordered: %v %v", f1.Pcm, f2.Pcm)
	}

	// AI sends audio to the caller; it is captured on the media stream.
	if err := sess.SendAudio(ctx, circleai.AudioFrame{Pcm: []byte{9, 9}, Format: circleai.CallMediaFormatMulaw8000}); err != nil {
		t.Fatalf("send: %v", err)
	}
	sent := media.DrainSentAudio()
	if len(sent) != 1 || sent[0].Pcm[0] != 9 {
		t.Errorf("sent audio = %+v", sent)
	}
}

func TestInMemoryCarrier_DtmfInBandFallback(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryTelephonyCarrier("fake")
	stream := mustURL(t, "wss://host/stream")
	sess, _ := c.Dial(ctx, "+1", "+2", stream, nil)
	defer sess.Close(ctx)
	media, _ := c.LastDial()

	// The in-memory stream does NOT implement IDtmfSendable, so SendDtmf falls
	// back to in-band tones delivered via SendAudio (captured on the stream).
	if err := sess.SendDtmf(ctx, "12"); err != nil {
		t.Fatalf("send dtmf: %v", err)
	}
	sent := media.DrainSentAudio()
	if len(sent) != 1 || len(sent[0].Pcm) == 0 {
		t.Fatalf("expected one in-band DTMF audio frame, got %+v", sent)
	}
	// Mulaw8000 => 8000 Hz => "12" = (1200+400+1200)*2 = 5600 bytes.
	if len(sent[0].Pcm) != 5600 {
		t.Errorf("DTMF frame size = %d, want 5600", len(sent[0].Pcm))
	}

	// Far end presses a digit; the AI receives it.
	media.PushDtmf(circleai.DtmfEvent{Digit: '7', Duration: 100 * time.Millisecond})
	ev := <-sess.ReceiveDtmf(ctx)
	if ev.Digit != '7' {
		t.Errorf("received DTMF = %c, want 7", ev.Digit)
	}
}

func TestInMemoryCarrier_StatusEventsAndHangUp(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryTelephonyCarrier("fake")
	stream := mustURL(t, "wss://host/stream")
	sess, _ := c.Dial(ctx, "+1", "+2", stream, nil)
	media, _ := c.LastDial()

	var events []circleai.CallStatus
	unsub := sess.OnStatusChanged(func(s circleai.CallStatus) { events = append(events, s) })
	defer unsub()

	// Driving the media status propagates through the session's notifier.
	media.SetStatus(circleai.CallStatusVoicemail)
	if sess.Status() != circleai.CallStatusVoicemail {
		t.Errorf("status = %v, want Voicemail", sess.Status())
	}

	// Hang up: EndedByAgent, media ended.
	if err := sess.HangUp(ctx); err != nil {
		t.Fatalf("hangup: %v", err)
	}
	if media.CurrentStatus() != circleai.CallStatusEndedByAgent {
		t.Errorf("media status after hangup = %v", media.CurrentStatus())
	}
	// We should have observed both Voicemail and EndedByAgent.
	sawVoicemail, sawEnded := false, false
	for _, e := range events {
		if e == circleai.CallStatusVoicemail {
			sawVoicemail = true
		}
		if e == circleai.CallStatusEndedByAgent {
			sawEnded = true
		}
	}
	if !sawVoicemail || !sawEnded {
		t.Errorf("missing status events: %v", events)
	}
}

func TestInMemoryCarrier_ColdTransfer(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryTelephonyCarrier("fake")
	stream := mustURL(t, "wss://host/stream")
	sess, _ := c.Dial(ctx, "+1", "+2", stream, nil)
	media, _ := c.LastDial()

	if err := sess.Transfer(ctx, "+15551230000", circleai.TransferModeCold, ""); err != nil {
		t.Fatalf("cold transfer: %v", err)
	}
	if media.CurrentStatus() != circleai.CallStatusTransferred {
		t.Errorf("status after cold transfer = %v, want Transferred", media.CurrentStatus())
	}
	if sess.Status() != circleai.CallStatusTransferred {
		t.Errorf("session status = %v, want Transferred", sess.Status())
	}
}

func TestInMemoryCarrier_InboundDelivery(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryTelephonyCarrier("fake")
	disp := c.Dispatcher()
	if disp.CarrierID() != "fake" {
		t.Errorf("dispatcher carrier id = %q", disp.CarrierID())
	}

	// Subscribe SYNCHRONOUSLY before any inbound call is delivered.
	var got circleai.ICallSession
	unsub := disp.Subscribe(func(_ context.Context, s circleai.ICallSession) error {
		got = s
		return nil
	})
	defer unsub()

	media, err := c.DeliverInbound(ctx, "+27820000000", "+27215550100")
	if err != nil {
		t.Fatalf("deliver inbound: %v", err)
	}
	if got == nil {
		t.Fatal("subscriber did not receive the inbound session")
	}
	if got.Info().Direction != circleai.CallDirectionInbound || got.Info().From != "+27820000000" {
		t.Errorf("inbound info = %+v", got.Info())
	}

	// The delivered session is live: caller audio flows to the AI.
	media.PushAudio(circleai.AudioFrame{Pcm: []byte{5}, Format: circleai.CallMediaFormatMulaw8000})
	f := <-got.ReceiveAudio(ctx)
	if f.Pcm[0] != 5 {
		t.Errorf("inbound audio = %v", f.Pcm)
	}

	// After unsubscribe, a new inbound call reaches nobody (no panic, no delivery).
	unsub()
	got = nil
	if _, err := c.DeliverInbound(ctx, "+1", "+2"); err != nil {
		t.Fatalf("deliver after unsub: %v", err)
	}
	if got != nil {
		t.Error("unsubscribed handler should not receive")
	}
}

func TestInMemoryCarrier_Unconfigured(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryTelephonyCarrier("fake", circleai.WithCarrierUnconfigured())
	if c.IsConfigured() {
		t.Fatal("should be unconfigured")
	}
	if _, err := c.ProvisionNumber(ctx, "ZA", ""); err == nil {
		t.Error("unconfigured provision should error")
	}
	stream := mustURL(t, "wss://h/s")
	if _, err := c.Dial(ctx, "+1", "+2", stream, nil); err == nil {
		t.Error("unconfigured dial should error")
	}
	nums, _ := c.ListNumbers(ctx)
	if len(nums) != 0 {
		t.Error("unconfigured ListNumbers should be empty")
	}
}
