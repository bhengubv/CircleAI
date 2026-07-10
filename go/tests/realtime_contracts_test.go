// realtime_contracts_test.go
//
// Verifies the CircleAI.Realtime port (realtime_contracts.go): enum ordinals/
// names, RealtimeSessionConfig default, the silence synthesiser sizing, the
// LoopbackRealtimeService/Session end-to-end (audio echo, speech-started/ended
// events, text-turn delta/final/turn-complete + offset advance, tool-result
// truncation, cancel, unbounded send-before-receive buffering, close), and the
// null service/session.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// rtCollect drains up to n events from ch, or fails after a timeout.
func rtCollect(t *testing.T, ch <-chan circleai.RealtimeEvent, n int) []circleai.RealtimeEvent {
	t.Helper()
	out := make([]circleai.RealtimeEvent, 0, n)
	deadline := time.After(2 * time.Second)
	for len(out) < n {
		select {
		case e, ok := <-ch:
			if !ok {
				t.Fatalf("event channel closed after %d/%d events", len(out), n)
			}
			out = append(out, e)
		case <-deadline:
			t.Fatalf("timed out after %d/%d events", len(out), n)
		}
	}
	return out
}

func rtLoudFrame() circleai.RealtimeAudioFrame {
	// 128 samples of max-amplitude PCM-16 -> clearly non-silent (rms >> 250).
	pcm := make([]byte, 256)
	for i := 0; i+1 < len(pcm); i += 2 {
		pcm[i] = 0xFF
		pcm[i+1] = 0x7F // 0x7FFF = 32767
	}
	return circleai.RealtimeAudioFrame{Pcm: pcm, Format: circleai.RealtimeAudioFormatPcm16k}
}

func TestRealtimeEnums(t *testing.T) {
	if circleai.RealtimeAudioFormatPcm16k != 0 || circleai.RealtimeAudioFormatPcm24k != 1 || circleai.RealtimeAudioFormatMulaw8k != 2 {
		t.Fatalf("audio format ordinals wrong")
	}
	if circleai.RealtimeAudioFormatPcm24k.String() != "Pcm24k" || circleai.RealtimeAudioFormatMulaw8k.String() != "Mulaw8k" {
		t.Fatalf("audio format names wrong")
	}
	if circleai.RealtimeDirectionInbound != 0 || circleai.RealtimeDirectionOutbound != 1 {
		t.Fatalf("direction ordinals wrong")
	}
	if circleai.RealtimeDirectionInbound.String() != "Inbound" || circleai.RealtimeDirectionOutbound.String() != "Outbound" {
		t.Fatalf("direction names wrong")
	}
}

func TestRealtimeSessionConfig_Default(t *testing.T) {
	c := circleai.NewRealtimeSessionConfig("gpt-realtime")
	if c.Model != "gpt-realtime" {
		t.Fatalf("model = %q", c.Model)
	}
	if c.AudioFormat != circleai.RealtimeAudioFormatPcm24k {
		t.Fatalf("default audio format = %v, want Pcm24k", c.AudioFormat)
	}
	if c.VoiceId != "" || c.SystemPrompt != "" || c.LanguageHint != "" || c.Tools != nil {
		t.Fatalf("optionals should be empty: %+v", c)
	}
}

func TestSilenceTextToAudio_Sizing(t *testing.T) {
	// Empty -> min 50ms floor. 24kHz * 50 / 1000 = 1200 samples -> 2400 bytes.
	empty, err := circleai.SilenceTextToAudio(context.Background(), "", circleai.RealtimeAudioFormatPcm24k)
	if err != nil {
		t.Fatalf("silence: %v", err)
	}
	if len(empty) != 2400 {
		t.Fatalf("empty len = %d, want 2400", len(empty))
	}
	// "one two three" = 3 words -> 240ms. 16kHz*240/1000=3840 samples -> 7680 bytes.
	three, _ := circleai.SilenceTextToAudio(context.Background(), "one two three", circleai.RealtimeAudioFormatPcm16k)
	if len(three) != 7680 {
		t.Fatalf("3-word len = %d, want 7680", len(three))
	}
	// All zero amplitude.
	for _, b := range three {
		if b != 0 {
			t.Fatalf("silence must be zero bytes")
		}
	}
}

func TestLoopbackRealtimeService_Basics(t *testing.T) {
	svc := circleai.NewLoopbackRealtimeService()
	if svc.ProviderId() != "loopback" || !svc.IsConfigured() {
		t.Fatalf("provider=%q configured=%v", svc.ProviderId(), svc.IsConfigured())
	}
	sess, err := svc.StartSession(context.Background(), circleai.NewRealtimeSessionConfig("m"))
	if err != nil {
		t.Fatalf("start: %v", err)
	}
	if len(sess.SessionId()) == 0 || sess.SessionId()[:5] != "loop-" {
		t.Fatalf("session id = %q", sess.SessionId())
	}
	_ = sess.Close(context.Background())
}

func TestLoopbackSession_AudioEchoAndSpeechEvents(t *testing.T) {
	ctx := context.Background()
	sess := circleai.NewLoopbackRealtimeSession(circleai.NewRealtimeSessionConfig("m"))
	defer sess.Close(ctx)

	audioCh := sess.ReceiveAudio(ctx)
	eventCh := sess.ReceiveEvents(ctx)

	// Loud frame -> SpeechStarted event + echoed back as outbound audio.
	loud := rtLoudFrame()
	if err := sess.SendAudio(ctx, loud); err != nil {
		t.Fatalf("send loud: %v", err)
	}
	// Silent frame -> SpeechEnded event + echo.
	silent := circleai.RealtimeAudioFrame{Pcm: make([]byte, 256), Format: circleai.RealtimeAudioFormatPcm16k}
	if err := sess.SendAudio(ctx, silent); err != nil {
		t.Fatalf("send silent: %v", err)
	}

	// Two echoed frames.
	for i := 0; i < 2; i++ {
		select {
		case f, ok := <-audioCh:
			if !ok {
				t.Fatalf("audio channel closed early")
			}
			if len(f.Pcm) != 256 {
				t.Fatalf("echoed frame len = %d", len(f.Pcm))
			}
		case <-time.After(2 * time.Second):
			t.Fatalf("did not receive echoed frame %d", i)
		}
	}

	// SpeechStarted then SpeechEnded.
	evs := rtCollect(t, eventCh, 2)
	if _, ok := evs[0].(circleai.SpeechStartedEvent); !ok {
		t.Fatalf("event[0] = %T, want SpeechStartedEvent", evs[0])
	}
	if _, ok := evs[1].(circleai.SpeechEndedEvent); !ok {
		t.Fatalf("event[1] = %T, want SpeechEndedEvent", evs[1])
	}
}

func TestLoopbackSession_TextTurn(t *testing.T) {
	ctx := context.Background()
	sess := circleai.NewLoopbackRealtimeSession(circleai.NewRealtimeSessionConfig("m"))
	defer sess.Close(ctx)

	eventCh := sess.ReceiveEvents(ctx)
	audioCh := sess.ReceiveAudio(ctx)

	if err := sess.SendText(ctx, "hello there friend"); err != nil {
		t.Fatalf("send text: %v", err)
	}

	// Delta(Outbound) -> Final(Outbound) -> TurnComplete.
	evs := rtCollect(t, eventCh, 3)
	delta, ok := evs[0].(circleai.TranscriptDeltaEvent)
	if !ok || delta.Delta != "hello there friend" || delta.Direction != circleai.RealtimeDirectionOutbound {
		t.Fatalf("event[0] = %#v", evs[0])
	}
	final, ok := evs[1].(circleai.TranscriptFinalEvent)
	if !ok || final.Text != "hello there friend" || final.Direction != circleai.RealtimeDirectionOutbound {
		t.Fatalf("event[1] = %#v", evs[1])
	}
	if _, ok := evs[2].(circleai.TurnCompleteEvent); !ok {
		t.Fatalf("event[2] = %T, want TurnCompleteEvent", evs[2])
	}

	// One synthesised outbound audio frame (3 words -> 240ms of 24kHz silence).
	select {
	case f, ok := <-audioCh:
		if !ok {
			t.Fatalf("audio channel closed early")
		}
		if f.Format != circleai.RealtimeAudioFormatPcm24k {
			t.Fatalf("frame format = %v", f.Format)
		}
		// 24000 * 240/1000 = 5760 samples -> 11520 bytes.
		if len(f.Pcm) != 11520 {
			t.Fatalf("synth frame len = %d, want 11520", len(f.Pcm))
		}
		if f.Offset != 0 {
			t.Fatalf("first frame offset = %v, want 0", f.Offset)
		}
	case <-time.After(2 * time.Second):
		t.Fatalf("no synthesised audio frame")
	}
}

func TestLoopbackSession_UnboundedBufferBeforeReceive(t *testing.T) {
	// Events written before ReceiveEvents is first called must be buffered
	// (unbounded channel semantics), not lost.
	ctx := context.Background()
	sess := circleai.NewLoopbackRealtimeSession(circleai.NewRealtimeSessionConfig("m"))
	defer sess.Close(ctx)

	if err := sess.SendText(ctx, "word"); err != nil { // enqueues 3 events before any reader
		t.Fatalf("send: %v", err)
	}
	// Now attach the reader — it must still see all three buffered events.
	eventCh := sess.ReceiveEvents(ctx)
	evs := rtCollect(t, eventCh, 3)
	if _, ok := evs[0].(circleai.TranscriptDeltaEvent); !ok {
		t.Fatalf("buffered event[0] = %T", evs[0])
	}
	if _, ok := evs[2].(circleai.TurnCompleteEvent); !ok {
		t.Fatalf("buffered event[2] = %T", evs[2])
	}
}

func TestLoopbackSession_ToolResultAndCancel(t *testing.T) {
	ctx := context.Background()
	sess := circleai.NewLoopbackRealtimeSession(circleai.NewRealtimeSessionConfig("m"))
	defer sess.Close(ctx)
	eventCh := sess.ReceiveEvents(ctx)

	long := ""
	for i := 0; i < 100; i++ {
		long += "x"
	}
	if err := sess.SendToolResult(ctx, "call-1", long); err != nil {
		t.Fatalf("tool result: %v", err)
	}
	ev := rtCollect(t, eventCh, 1)[0]
	delta, ok := ev.(circleai.TranscriptDeltaEvent)
	if !ok {
		t.Fatalf("tool event = %T", ev)
	}
	// Prefix "[tool call-1: " + 60 runes + "…" + "]".
	if len([]rune(delta.Delta)) != len("[tool call-1: ]")+61 {
		t.Fatalf("truncated delta = %q (len %d)", delta.Delta, len([]rune(delta.Delta)))
	}

	if err := sess.SendToolResult(ctx, "  ", "x"); err == nil {
		t.Fatalf("blank callId must error")
	}

	if err := sess.CancelResponse(ctx); err != nil {
		t.Fatalf("cancel: %v", err)
	}
	if _, ok := rtCollect(t, eventCh, 1)[0].(circleai.TurnCompleteEvent); !ok {
		t.Fatalf("cancel should emit TurnCompleteEvent")
	}
}

func TestLoopbackSession_CloseClosesStreams(t *testing.T) {
	ctx := context.Background()
	sess := circleai.NewLoopbackRealtimeSession(circleai.NewRealtimeSessionConfig("m"))
	audioCh := sess.ReceiveAudio(ctx)
	eventCh := sess.ReceiveEvents(ctx)

	if err := sess.Close(ctx); err != nil {
		t.Fatalf("close: %v", err)
	}
	_ = sess.Close(ctx) // idempotent

	// Both channels must drain-and-close.
	assertRTChannelClosed(t, audioChanAdapter(audioCh))
	assertEventChannelClosed(t, eventCh)
}

func TestLoopbackServiceWith_CustomSynth(t *testing.T) {
	called := false
	svc, err := circleai.NewLoopbackRealtimeServiceWith(func(_ context.Context, text string, _ circleai.RealtimeAudioFormat) ([]byte, error) {
		called = true
		return []byte{1, 2, 3, 4}, nil
	})
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if _, err := circleai.NewLoopbackRealtimeServiceWith(nil); err == nil {
		t.Fatalf("nil synth must error")
	}
	ctx := context.Background()
	sess, _ := svc.StartSession(ctx, circleai.NewRealtimeSessionConfig("m"))
	defer sess.Close(ctx)
	audioCh := sess.ReceiveAudio(ctx)
	_ = sess.SendText(ctx, "hi")
	select {
	case f := <-audioCh:
		if len(f.Pcm) != 4 {
			t.Fatalf("custom synth frame len = %d", len(f.Pcm))
		}
	case <-time.After(2 * time.Second):
		t.Fatalf("no custom frame")
	}
	if !called {
		t.Fatalf("custom synth not invoked")
	}
}

func TestNullRealtimeService(t *testing.T) {
	svc := circleai.NullRealtimeServiceInstance
	if svc.ProviderId() != "null" || svc.IsConfigured() {
		t.Fatalf("null provider=%q configured=%v", svc.ProviderId(), svc.IsConfigured())
	}
	if _, err := svc.StartSession(context.Background(), circleai.NewRealtimeSessionConfig("m")); err == nil {
		t.Fatalf("null StartSession must error")
	}
}

func TestNullRealtimeSession(t *testing.T) {
	ctx := context.Background()
	var sess circleai.IRealtimeSession = circleai.NullRealtimeSession{}
	if sess.SessionId() != "null" {
		t.Fatalf("id = %q", sess.SessionId())
	}
	// No-ops.
	if err := sess.SendAudio(ctx, circleai.RealtimeAudioFrame{}); err != nil {
		t.Fatalf("send audio: %v", err)
	}
	if err := sess.SendText(ctx, "x"); err != nil {
		t.Fatalf("send text: %v", err)
	}
	if err := sess.SendToolResult(ctx, "c", "r"); err != nil {
		t.Fatalf("tool: %v", err)
	}
	if err := sess.CancelResponse(ctx); err != nil {
		t.Fatalf("cancel: %v", err)
	}
	// Both receive channels are already closed.
	assertRTChannelClosed(t, audioChanAdapter(sess.ReceiveAudio(ctx)))
	assertEventChannelClosed(t, sess.ReceiveEvents(ctx))
	if err := sess.Close(ctx); err != nil {
		t.Fatalf("close: %v", err)
	}
}

// --- small channel-closed helpers (audio channel adapted to a func) ---

func audioChanAdapter(ch <-chan circleai.RealtimeAudioFrame) func() bool {
	return func() bool {
		select {
		case _, ok := <-ch:
			return ok
		case <-time.After(2 * time.Second):
			return true // treat as "still open" -> test fails
		}
	}
}

func assertRTChannelClosed(t *testing.T, recv func() bool) {
	t.Helper()
	if recv() {
		t.Fatalf("expected channel closed (recv ok=true means it delivered or blocked)")
	}
}

func assertEventChannelClosed(t *testing.T, ch <-chan circleai.RealtimeEvent) {
	t.Helper()
	select {
	case _, ok := <-ch:
		if ok {
			t.Fatalf("expected event channel closed")
		}
	case <-time.After(2 * time.Second):
		t.Fatalf("event channel neither delivered nor closed")
	}
}
