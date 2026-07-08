// hosting_runtime_test.go
//
// Verifies CircleAI.Hosting runtime ports:
//   ProactiveReasoningService + IdleTrigger + ScheduleTrigger
//   ThermalThrottleService (injected sampler)
//   ManualMemoryPressureSource / NullMemoryPressureSource
//   BackgroundInferenceWorker thermal pause
//   MemoryPressureLevel / ThermalState / BrownoutReason ordinals

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── Triggers ───────────────────────────────────────────────────────────────

func TestIdleTrigger(t *testing.T) {
	tr := circleai.NewIdleTrigger(time.Hour)
	if tr.Name() != "idle" {
		t.Errorf("name = %q", tr.Name())
	}
	met, _ := tr.IsMet(context.Background(), circleai.ProactiveContext{TimeSinceLastInteraction: 2 * time.Hour})
	if !met {
		t.Error("2h idle should meet 1h threshold")
	}
	met, _ = tr.IsMet(context.Background(), circleai.ProactiveContext{TimeSinceLastInteraction: 30 * time.Minute})
	if met {
		t.Error("30m idle should not meet 1h threshold")
	}
}

func TestIdleTrigger_DefaultThreshold(t *testing.T) {
	tr := circleai.NewIdleTrigger(0)
	if tr.IdleThreshold() != 4*time.Hour {
		t.Errorf("default threshold = %v, want 4h", tr.IdleThreshold())
	}
}

func TestScheduleTrigger_FiresOncePerDay(t *testing.T) {
	// Build a trigger whose window covers "now" in local time.
	now := time.Now().Local()
	todStart := time.Duration(now.Hour())*time.Hour + time.Duration(now.Minute())*time.Minute
	tr := circleai.NewScheduleTrigger(todStart, "daily")
	if tr.Name() != "daily" {
		t.Errorf("name = %q", tr.Name())
	}

	pctx := circleai.ProactiveContext{NowUTC: now.UTC()}
	met, _ := tr.IsMet(context.Background(), pctx)
	if !met {
		t.Fatal("trigger should fire within its 5-minute window")
	}
	// Second call same day must not re-fire.
	met, _ = tr.IsMet(context.Background(), pctx)
	if met {
		t.Error("trigger fired twice on the same day")
	}
}

func TestScheduleTrigger_OutsideWindow(t *testing.T) {
	now := time.Now().Local()
	// Window 6 hours from now — definitely not active.
	tod := time.Duration(now.Add(6*time.Hour).Hour()) * time.Hour
	tr := circleai.NewScheduleTrigger(tod, "later")
	met, _ := tr.IsMet(context.Background(), circleai.ProactiveContext{NowUTC: now.UTC()})
	if met {
		t.Error("trigger fired outside its window")
	}
}

// alwaysTrigger fires whenever asked (priority-order test aid).
type alwaysTrigger struct{ name string }

func (a alwaysTrigger) Name() string { return a.name }
func (a alwaysTrigger) IsMet(context.Context, circleai.ProactiveContext) (bool, error) {
	return true, nil
}

func TestProactiveReasoningService_FiresFirstTrigger(t *testing.T) {
	ctx := context.Background()
	butler := &fakeButler{askReply: "Hey, checking in!"}
	triggers := []circleai.ITriggerCondition{
		alwaysTrigger{name: "first"},
		alwaysTrigger{name: "second"},
	}
	svc := circleai.NewProactiveReasoningService(butler, nil, nil, triggers)

	var events []circleai.ProactiveMessageEventArgs
	svc.SetOnProactiveMessage(func(e circleai.ProactiveMessageEventArgs) { events = append(events, e) })

	if err := svc.Check(ctx, "user-1"); err != nil {
		t.Fatalf("Check: %v", err)
	}
	if len(events) != 1 {
		t.Fatalf("expected exactly 1 event, got %d", len(events))
	}
	if events[0].TriggerName != "first" {
		t.Errorf("fired trigger = %q, want 'first' (priority order)", events[0].TriggerName)
	}
	if events[0].Message != "Hey, checking in!" {
		t.Errorf("message = %q", events[0].Message)
	}
	if events[0].UserID != "user-1" {
		t.Errorf("userID = %q", events[0].UserID)
	}
}

func TestProactiveReasoningService_NoTriggers(t *testing.T) {
	butler := &fakeButler{}
	svc := circleai.NewProactiveReasoningService(butler, nil, nil, nil)
	fired := false
	svc.SetOnProactiveMessage(func(circleai.ProactiveMessageEventArgs) { fired = true })
	if err := svc.Check(context.Background(), "u"); err != nil {
		t.Fatalf("Check: %v", err)
	}
	if fired {
		t.Error("no triggers should mean no event")
	}
	if len(butler.asked) != 0 {
		t.Error("butler should not be asked when no triggers")
	}
}

func TestProactiveReasoningService_EmptyUserID(t *testing.T) {
	svc := circleai.NewProactiveReasoningService(&fakeButler{}, nil, nil, []circleai.ITriggerCondition{alwaysTrigger{name: "x"}})
	if err := svc.Check(context.Background(), "  "); err == nil {
		t.Error("expected error for blank userID")
	}
}

// ── ThermalThrottleService ──────────────────────────────────────────────────

func TestThermalThrottleService_StateAndPause(t *testing.T) {
	state := circleai.ThermalNormal
	svc := circleai.NewThermalThrottleService(func() circleai.ThermalState { return state })

	changes := make(chan circleai.ThermalState, 8)
	svc.SetOnStateChanged(func(s circleai.ThermalState) { changes <- s })

	svc.StartMonitoring(context.Background())
	defer svc.StopMonitoring()

	// Immediate sample should have applied Normal.
	waitFor(t, func() bool { return svc.CurrentState() == circleai.ThermalNormal }, "initial Normal")
	if svc.ShouldPauseInference() {
		t.Error("Normal should not pause inference")
	}

	// Drain the initial change (Unknown→Normal).
	select {
	case <-changes:
	case <-time.After(time.Second):
		t.Fatal("no initial state-change event")
	}

	state = circleai.ThermalCritical
	// The poll interval is 10s; drive a manual re-evaluation is not exposed, so
	// verify the threshold logic directly on a fresh service seeded Critical.
	svc2 := circleai.NewThermalThrottleService(func() circleai.ThermalState { return circleai.ThermalCritical })
	svc2.StartMonitoring(context.Background())
	defer svc2.StopMonitoring()
	waitFor(t, func() bool { return svc2.CurrentState() == circleai.ThermalCritical }, "Critical")
	if !svc2.ShouldPauseInference() {
		t.Error("Critical should pause inference")
	}
}

func TestThermalState_Ordinals(t *testing.T) {
	pairs := []struct {
		got  circleai.ThermalState
		want int32
	}{
		{circleai.ThermalUnknown, 0},
		{circleai.ThermalNormal, 1},
		{circleai.ThermalFair, 2},
		{circleai.ThermalSerious, 3},
		{circleai.ThermalCritical, 4},
	}
	for _, p := range pairs {
		if int32(p.got) != p.want {
			t.Errorf("ThermalState ordinal: got %d, want %d", int32(p.got), p.want)
		}
	}
	if !(circleai.ThermalSerious >= circleai.ThermalFair) {
		t.Error("ordering broken: Serious should be >= Fair")
	}
}

// ── Memory pressure ─────────────────────────────────────────────────────────

func TestManualMemoryPressureSource(t *testing.T) {
	ctx := context.Background()
	src := circleai.NewManualMemoryPressureSource()
	if src.Current() != circleai.MemoryPressureNormal {
		t.Error("initial pressure should be Normal")
	}

	type transition struct{ from, to circleai.MemoryPressureLevel }
	var seen []transition
	unsub := src.Subscribe(func(_ context.Context, from, to circleai.MemoryPressureLevel) error {
		seen = append(seen, transition{from, to})
		return nil
	})

	_ = src.Raise(ctx, circleai.MemoryPressureCritical)
	if src.Current() != circleai.MemoryPressureCritical {
		t.Error("Current should be Critical after Raise")
	}
	// Idempotent: same level does not re-fire.
	_ = src.Raise(ctx, circleai.MemoryPressureCritical)
	if len(seen) != 1 {
		t.Fatalf("handler fired %d times, want 1 (transitions only)", len(seen))
	}
	if seen[0].from != circleai.MemoryPressureNormal || seen[0].to != circleai.MemoryPressureCritical {
		t.Errorf("transition = %+v, want Normal→Critical", seen[0])
	}

	// After unsubscribe, no more callbacks.
	unsub()
	_ = src.Raise(ctx, circleai.MemoryPressureNormal)
	if len(seen) != 1 {
		t.Error("handler fired after unsubscribe")
	}
}

func TestNullMemoryPressureSource(t *testing.T) {
	src := circleai.NullMemoryPressureSourceInstance
	if src.Current() != circleai.MemoryPressureNormal {
		t.Error("null source should always be Normal")
	}
	unsub := src.Subscribe(func(context.Context, circleai.MemoryPressureLevel, circleai.MemoryPressureLevel) error {
		t.Error("null source must never call handlers")
		return nil
	})
	unsub() // must not panic
}

func TestBrownoutReason_Ordinals(t *testing.T) {
	pairs := []struct {
		got  circleai.BrownoutReason
		want int
	}{
		{circleai.BrownoutMemoryPressure, 0},
		{circleai.BrownoutBatteryFloor, 1},
		{circleai.BrownoutThermalCritical, 2},
		{circleai.BrownoutManual, 3},
	}
	for _, p := range pairs {
		if int(p.got) != p.want {
			t.Errorf("BrownoutReason ordinal: got %d, want %d", int(p.got), p.want)
		}
	}
}

// ── BackgroundInferenceWorker ───────────────────────────────────────────────

func TestBackgroundInferenceWorker_ThermalPause(t *testing.T) {
	ctx := context.Background()
	butler := &fakeButler{}
	thermal := circleai.NewThermalThrottleService(func() circleai.ThermalState { return circleai.ThermalCritical })
	worker := circleai.NewBackgroundInferenceWorker(butler, thermal)

	if err := worker.Start(ctx); err != nil {
		t.Fatalf("start: %v", err)
	}
	defer worker.Stop(ctx)

	if !butler.IsReady() {
		t.Error("butler should be started by the worker")
	}
	waitFor(t, worker.IsPaused, "worker paused under Critical thermal")
}

func TestBackgroundInferenceWorker_NoThermal(t *testing.T) {
	ctx := context.Background()
	butler := &fakeButler{}
	worker := circleai.NewBackgroundInferenceWorker(butler, nil)
	if err := worker.Start(ctx); err != nil {
		t.Fatalf("start: %v", err)
	}
	defer worker.Stop(ctx)
	if worker.IsPaused() {
		t.Error("worker with no thermal service should never be paused")
	}
	// Double-stop is a no-op.
	_ = worker.Stop(ctx)
	_ = worker.Stop(ctx)
}

// waitFor polls cond up to ~2s.
func waitFor(t *testing.T, cond func() bool, what string) {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for: %s", what)
}
