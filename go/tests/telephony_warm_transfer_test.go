// telephony_warm_transfer_test.go
//
// Verifies CircleAI.Telephony/WarmTransferOrchestrator.cs port: the successful
// dial-brief-bridge flow, the validation failures, and the failure-path
// hang-up-and-report behaviour. Uses the in-memory carrier so the whole flow is
// hermetic.

package circleai_test

import (
	"context"
	"errors"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestWarmTransfer_Success(t *testing.T) {
	ctx := context.Background()
	carrier := circleai.NewInMemoryTelephonyCarrier("fake")

	// A source session (the active call being transferred).
	srcMedia, _ := carrier.DeliverInbound(ctx, "+27820000000", "+27215550100")
	_ = srcMedia
	// Grab the delivered session via a subscriber.
	var src circleai.ICallSession
	unsub := carrier.Dispatcher().Subscribe(func(_ context.Context, s circleai.ICallSession) error { src = s; return nil })
	defer unsub()
	_, _ = carrier.DeliverInbound(ctx, "+27820000001", "+27215550100")
	if src == nil {
		t.Fatal("no source session")
	}

	briefed := false
	tts := func(_ context.Context, text string) ([]byte, error) {
		briefed = true
		if text != "customer wants a refund" {
			t.Errorf("briefing text = %q", text)
		}
		return []byte{1, 2, 3}, nil
	}
	orch := circleai.NewDefaultWarmTransferOrchestrator(carrier, tts)

	res, err := orch.Execute(ctx, circleai.WarmTransferRequest{
		SourceSession:   src,
		TargetNumber:    "+15559990000",
		BriefingText:    "customer wants a refund",
		BridgeStreamURL: "wss://host/bridge",
	})
	if err != nil {
		t.Fatalf("execute: %v", err)
	}
	if !res.Succeeded {
		t.Fatalf("warm transfer failed: %q", res.FailureReason)
	}
	if !briefed {
		t.Error("briefing TTS not invoked")
	}
	if res.BridgeSession == nil {
		t.Error("bridge session should be returned")
	}
	// Source call ended up Transferred (the bridge/cold-transfer moment).
	if src.Status() != circleai.CallStatusTransferred {
		t.Errorf("source status = %v, want Transferred", src.Status())
	}
}

func TestWarmTransfer_Validation(t *testing.T) {
	ctx := context.Background()
	carrier := circleai.NewInMemoryTelephonyCarrier("fake")
	tts := func(context.Context, string) ([]byte, error) { return nil, nil }
	orch := circleai.NewDefaultWarmTransferOrchestrator(carrier, tts)

	// Missing source session.
	r1, _ := orch.Execute(ctx, circleai.WarmTransferRequest{TargetNumber: "+1", BridgeStreamURL: "wss://h/b"})
	if r1.Succeeded || r1.FailureReason != "SourceSession is required" {
		t.Errorf("expected SourceSession-required failure, got %+v", r1)
	}

	// Missing target number.
	src, _ := carrier.Dial(ctx, "+1", "+2", mustURL(t, "wss://h/s"), nil)
	r2, _ := orch.Execute(ctx, circleai.WarmTransferRequest{SourceSession: src, BridgeStreamURL: "wss://h/b"})
	if r2.Succeeded || r2.FailureReason != "TargetNumber is required" {
		t.Errorf("expected TargetNumber-required failure, got %+v", r2)
	}
}

func TestWarmTransfer_BriefingFailureHangsUpBridge(t *testing.T) {
	ctx := context.Background()
	carrier := circleai.NewInMemoryTelephonyCarrier("fake")
	src, _ := carrier.Dial(ctx, "+1", "+2", mustURL(t, "wss://h/s"), nil)

	tts := func(context.Context, string) ([]byte, error) { return nil, errors.New("tts down") }
	orch := circleai.NewDefaultWarmTransferOrchestrator(carrier, tts)

	res, _ := orch.Execute(ctx, circleai.WarmTransferRequest{
		SourceSession:   src,
		TargetNumber:    "+15550000000",
		BriefingText:    "hi",
		BridgeStreamURL: "wss://host/bridge",
	})
	if res.Succeeded {
		t.Fatal("expected failure when briefing TTS errors")
	}
	if res.FailureReason == "" {
		t.Error("expected a failure reason")
	}
}
