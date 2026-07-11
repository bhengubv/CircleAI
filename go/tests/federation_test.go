// federation_test.go
//
// Verifies the CircleAI.Federation port (federation.go): sample-weighted
// averaging + float codecs, the round lifecycle (open/submit/commit) with a
// signature validator, and the HMAC-gated delta dispatcher (accept / duplicate /
// signature-invalid / round-closed).

package circleai_test

import (
	"math"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
	"github.com/google/uuid"
)

func TestFederation_WeightedAverage(t *testing.T) {
	// deltas [1,1] (weight 3) and [3,3] (weight 1) -> (3*1 + 1*3)/4 = 1.5 each.
	d1 := circleai.ModelDelta{DeltaPayload: circleai.FederatedEncodeFloats([]float32{1, 1}), SampleCount: 3}
	d2 := circleai.ModelDelta{DeltaPayload: circleai.FederatedEncodeFloats([]float32{3, 3}), SampleCount: 1}
	out, err := circleai.FederatedAverage([]circleai.ModelDelta{d1, d2})
	if err != nil {
		t.Fatalf("average: %v", err)
	}
	got, err := circleai.FederatedDecodeFloats(out)
	if err != nil || len(got) != 2 {
		t.Fatalf("decode = %v err=%v", got, err)
	}
	for _, v := range got {
		if math.Abs(float64(v)-1.5) > 1e-5 {
			t.Fatalf("weighted avg = %v, want 1.5", got)
		}
	}
	if _, err := circleai.FederatedAverage(nil); err == nil {
		t.Fatalf("empty list must error")
	}
}

func TestFederation_RoundLifecycle(t *testing.T) {
	agg := circleai.NewInMemoryFederationAggregator(func(circleai.ModelDelta) bool { return true })
	round, err := agg.OpenRound("m", "1.0", "1.1", 2, 5)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	mk := func(sc int) circleai.ModelDelta {
		return circleai.ModelDelta{ID: uuid.New(), RoundID: round.ID, DeltaPayload: circleai.FederatedEncodeFloats([]float32{1}), SampleCount: sc}
	}
	// Below MinParticipants -> nil payload.
	if err := agg.SubmitDelta(mk(1)); err != nil {
		t.Fatalf("submit1: %v", err)
	}
	if payload, _ := agg.TryCommit(round.ID); payload != nil {
		t.Fatalf("commit below min must return nil")
	}
	if err := agg.SubmitDelta(mk(2)); err != nil {
		t.Fatalf("submit2: %v", err)
	}
	payload, err := agg.TryCommit(round.ID)
	if err != nil || payload == nil {
		t.Fatalf("commit at min = %v err=%v", payload, err)
	}
	got, _ := agg.GetRound(round.ID)
	if got.Status != circleai.RoundStatusCommitted || got.CurrentParticipantCount != 2 {
		t.Fatalf("round after commit = %+v", got)
	}
	// Idempotent re-commit returns the same payload.
	again, _ := agg.TryCommit(round.ID)
	if len(again) != len(payload) {
		t.Fatalf("re-commit payload differs")
	}
	// Unknown round errors.
	if _, err := agg.GetRound(uuid.New()); err == nil {
		t.Fatalf("unknown round must error")
	}
}

func TestFederation_DeltaDispatcher(t *testing.T) {
	key := []byte("0123456789abcdef0123456789abcdef")
	agg := circleai.NewInMemoryFederationAggregator(circleai.HMACSignatureValidator(key))
	round, _ := agg.OpenRound("m", "1.0", "1.1", 1, 5)
	disp := circleai.NewInMemoryFederationDeltaDispatcher(agg, circleai.HMACSignatureValidator(key))

	good := circleai.ModelDelta{ID: uuid.New(), RoundID: round.ID, ContributorUhid: "u", ModelID: "m", FromVersion: "1.0", DeltaPayload: circleai.FederatedEncodeFloats([]float32{1})}
	good.Signature = circleai.HMACSignDelta(key, good)

	if outcome, _ := disp.VerifyAndSubmit(good); outcome != circleai.DeltaAccepted {
		t.Fatalf("first submit outcome = %d, want Accepted", outcome)
	}
	if outcome, _ := disp.VerifyAndSubmit(good); outcome != circleai.DeltaDuplicate {
		t.Fatalf("replay outcome = %d, want Duplicate", outcome)
	}
	// Tampered signature.
	bad := good
	bad.ID = uuid.New()
	bad.Signature = []byte("wrong")
	if outcome, _ := disp.VerifyAndSubmit(bad); outcome != circleai.DeltaSignatureInvalid {
		t.Fatalf("bad-sig outcome = %d, want SignatureInvalid", outcome)
	}
	// Unknown round.
	stray := good
	stray.ID = uuid.New()
	stray.RoundID = uuid.New()
	stray.Signature = circleai.HMACSignDelta(key, stray)
	if outcome, _ := disp.VerifyAndSubmit(stray); outcome != circleai.DeltaRoundUnknown {
		t.Fatalf("unknown-round outcome = %d, want RoundUnknown", outcome)
	}
}
