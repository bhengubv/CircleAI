// wearable_biosignals_test.go
//
// Verifies the CircleAI.Wearable.Biosignals port (wearable_biosignals.go):
// sample factory (confidence clamp), null + recorded sources, sliding-window
// aggregation stats, and the deterministic affect-mapper rule sheet.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestBiosignals_SampleFactoryAndSources(t *testing.T) {
	s := circleai.NewBiosignalSample(circleai.BiosignalKindHeartRate, 72, "bpm", 1.5, false)
	if s.Confidence != 1.0 {
		t.Fatalf("confidence must clamp to 1.0, got %v", s.Confidence)
	}
	if s.Kind != circleai.BiosignalKindHeartRate || s.Value != 72 || s.Unit != "bpm" {
		t.Fatalf("sample fields wrong: %+v", s)
	}

	// Null source: supports nothing, streams nothing.
	var null circleai.BiosignalSource = circleai.NullBiosignalSource{}
	if len(null.SupportedKinds()) != 0 || null.IsSupported(circleai.BiosignalKindHeartRate) {
		t.Fatalf("null source must support nothing")
	}
	count := 0
	for range null.Stream(context.Background()) {
		count++
	}
	if count != 0 {
		t.Fatalf("null source must stream nothing, got %d", count)
	}

	// Recorded source replays its samples and reports its kinds.
	samples := []circleai.BiosignalSample{
		circleai.NewBiosignalSample(circleai.BiosignalKindHeartRate, 60, "bpm", 1, false),
		circleai.NewBiosignalSample(circleai.BiosignalKindSteps, 100, "count", 1, true),
	}
	rec, err := circleai.NewRecordedBiosignalSource(samples, 0)
	if err != nil {
		t.Fatalf("new recorded: %v", err)
	}
	if !rec.IsSupported(circleai.BiosignalKindHeartRate) || rec.IsSupported(circleai.BiosignalKindOxygenSaturation) {
		t.Fatalf("recorded IsSupported wrong")
	}
	got := 0
	for range rec.Stream(context.Background()) {
		got++
	}
	if got != 2 {
		t.Fatalf("recorded stream count = %d, want 2", got)
	}
	if _, err := circleai.NewRecordedBiosignalSource(nil, 0); err == nil {
		t.Fatalf("nil samples must error")
	}
}

func TestBiosignals_AggregatorSnapshot(t *testing.T) {
	now := time.Now().UTC()
	mk := func(k circleai.BiosignalKind, v float32) circleai.BiosignalSample {
		s := circleai.NewBiosignalSample(k, v, "u", 1, false)
		s.MeasuredAt = now
		return s
	}
	rec, _ := circleai.NewRecordedBiosignalSource([]circleai.BiosignalSample{
		mk(circleai.BiosignalKindHeartRate, 60),
		mk(circleai.BiosignalKindHeartRate, 80),
		mk(circleai.BiosignalKindHeartRate, 100),
	}, 0)
	agg, err := circleai.NewBiosignalAggregator(rec)
	if err != nil {
		t.Fatalf("new aggregator: %v", err)
	}
	snap, err := agg.Snapshot(context.Background(), time.Minute)
	if err != nil {
		t.Fatalf("snapshot: %v", err)
	}
	hr, ok := snap.Stats[circleai.BiosignalKindHeartRate]
	if !ok {
		t.Fatalf("no HeartRate stats in snapshot: %+v", snap.Stats)
	}
	if hr.SampleCount != 3 || hr.Min != 60 || hr.Max != 100 || hr.Mean != 80 {
		t.Fatalf("HR stats wrong: %+v", hr)
	}
	if _, err := agg.Snapshot(context.Background(), 0); err == nil {
		t.Fatalf("non-positive window must error")
	}
}

func TestBiosignals_AffectMapper(t *testing.T) {
	// High heart rate raises Energy and Uncertainty.
	a := circleai.NewAffectState("u1")
	e0, u0 := a.Energy, a.Uncertainty
	hr := circleai.NewBiosignalSample(circleai.BiosignalKindHeartRate, 140, "bpm", 1, false)
	circleai.ApplyBiosignalToAffect(hr, &a)
	if a.Energy <= e0 || a.Uncertainty <= u0 {
		t.Fatalf("high HR should raise Energy and Uncertainty: e %v->%v u %v->%v", e0, a.Energy, u0, a.Uncertainty)
	}

	// Low confidence is ignored.
	b := circleai.NewAffectState("u2")
	eb := b.Energy
	lowConf := circleai.NewBiosignalSample(circleai.BiosignalKindHeartRate, 140, "bpm", 0.2, false)
	circleai.ApplyBiosignalToAffect(lowConf, &b)
	if b.Energy != eb {
		t.Fatalf("low-confidence sample must not mutate affect: %v -> %v", eb, b.Energy)
	}

	// Low SpO2 raises Uncertainty.
	c := circleai.NewAffectState("u3")
	uc := c.Uncertainty
	spo2 := circleai.NewBiosignalSample(circleai.BiosignalKindOxygenSaturation, 85, "%", 1, false)
	circleai.ApplyBiosignalToAffect(spo2, &c)
	if c.Uncertainty <= uc {
		t.Fatalf("low SpO2 should raise Uncertainty: %v -> %v", uc, c.Uncertainty)
	}

	// SleepStage does not mutate the affect dimensions.
	d := circleai.NewAffectState("u4")
	ed, ud := d.Energy, d.Uncertainty
	sleep := circleai.NewBiosignalSample(circleai.BiosignalKindSleepStage, 2, "stage", 1, false)
	circleai.ApplyBiosignalToAffect(sleep, &d)
	if d.Energy != ed || d.Uncertainty != ud {
		t.Fatalf("sleep stage must not change affect dimensions")
	}
}
