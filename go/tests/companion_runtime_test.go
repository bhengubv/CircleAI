// companion_runtime_test.go
//
// Verifies CompanionRuntime (ported from CompanionRuntime.cs):
//   - Start runs the sync engine and (when CatchUpOnStart) an OnDemand tick.
//   - Stop closes the sync engine and is safe to call.
//   - ConsolidateNow delegates an OnDemand tick.
//   - SyncNow delegates to the engine (and no-ops when no engine is wired).
//   - IngestMedia errors when no ingester was wired.
//   - Periodic loops disabled via zero intervals do not run (deterministic test).

package circleai_test

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// fakeConsolidator records how many times each kind was ticked.
type fakeConsolidator struct {
	mu    sync.Mutex
	ticks map[circleai.SleepKind]int
	out   circleai.ConsolidationOutcome
	err   error
}

func newFakeConsolidator() *fakeConsolidator {
	return &fakeConsolidator{ticks: make(map[circleai.SleepKind]int)}
}

func (f *fakeConsolidator) Tick(_ context.Context, kind circleai.SleepKind) (circleai.ConsolidationOutcome, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.ticks[kind]++
	if f.err != nil {
		return circleai.ConsolidationOutcome{}, f.err
	}
	out := f.out
	out.Kind = kind
	return out, nil
}

func (f *fakeConsolidator) count(kind circleai.SleepKind) int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.ticks[kind]
}

// fakeSyncEngine records lifecycle calls for delegation assertions.
type fakeSyncEngine struct {
	mu        sync.Mutex
	started   int
	syncNows  int
	closed    int
	startErr  error
	syncNoErr error
}

func (e *fakeSyncEngine) Start(context.Context) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.started++
	return e.startErr
}
func (e *fakeSyncEngine) SyncNow(context.Context) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.syncNows++
	return e.syncNoErr
}
func (e *fakeSyncEngine) WriteLocal(context.Context, string, string, string, bool) (circleai.SyncableEntry, error) {
	return circleai.SyncableEntry{}, nil
}
func (e *fakeSyncEngine) Close() error {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.closed++
	return nil
}

// noPeriodicOptions disables timer loops so tests are deterministic.
func noPeriodicOptions(catchUp bool) *circleai.CompanionRuntimeOptions {
	return &circleai.CompanionRuntimeOptions{
		DailyTickInterval:     0,
		WeeklyTickInterval:    0,
		MonthlyTickInterval:   0,
		SyncBroadcastInterval: 0,
		InitialDelay:          0,
		CatchUpOnStart:        catchUp,
	}
}

func TestCompanionRuntime_StartRunsCatchUpAndEngine(t *testing.T) {
	ctx := context.Background()
	cons := newFakeConsolidator()
	eng := &fakeSyncEngine{}
	rt, err := circleai.NewCompanionRuntime(cons, circleai.CompanionRuntimeDeps{
		SyncEngine: eng,
		Options:    noPeriodicOptions(true),
	})
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	if err := rt.Start(ctx); err != nil {
		t.Fatalf("Start: %v", err)
	}
	defer rt.Close()

	if eng.started != 1 {
		t.Errorf("engine Start count: got %d want 1", eng.started)
	}
	if cons.count(circleai.SleepOnDemand) != 1 {
		t.Errorf("catch-up OnDemand tick: got %d want 1", cons.count(circleai.SleepOnDemand))
	}
}

func TestCompanionRuntime_CatchUpDisabled(t *testing.T) {
	ctx := context.Background()
	cons := newFakeConsolidator()
	rt, _ := circleai.NewCompanionRuntime(cons, circleai.CompanionRuntimeDeps{
		Options: noPeriodicOptions(false),
	})
	if err := rt.Start(ctx); err != nil {
		t.Fatalf("Start: %v", err)
	}
	defer rt.Close()
	if cons.count(circleai.SleepOnDemand) != 0 {
		t.Errorf("no catch-up expected, got %d", cons.count(circleai.SleepOnDemand))
	}
}

func TestCompanionRuntime_CatchUpFailureIsNonFatal(t *testing.T) {
	ctx := context.Background()
	cons := newFakeConsolidator()
	cons.err = errors.New("boom")
	rt, _ := circleai.NewCompanionRuntime(cons, circleai.CompanionRuntimeDeps{
		Options: noPeriodicOptions(true),
	})
	// Start must not return the catch-up error.
	if err := rt.Start(ctx); err != nil {
		t.Fatalf("Start should swallow catch-up error, got %v", err)
	}
	_ = rt.Close()
}

func TestCompanionRuntime_ConsolidateNowAndSyncNow(t *testing.T) {
	ctx := context.Background()
	cons := newFakeConsolidator()
	eng := &fakeSyncEngine{}
	rt, _ := circleai.NewCompanionRuntime(cons, circleai.CompanionRuntimeDeps{
		SyncEngine: eng,
		Options:    noPeriodicOptions(false),
	})

	if _, err := rt.ConsolidateNow(ctx); err != nil {
		t.Fatalf("ConsolidateNow: %v", err)
	}
	if cons.count(circleai.SleepOnDemand) != 1 {
		t.Errorf("ConsolidateNow should tick OnDemand once, got %d", cons.count(circleai.SleepOnDemand))
	}

	if err := rt.SyncNow(ctx); err != nil {
		t.Fatalf("SyncNow: %v", err)
	}
	if eng.syncNows != 1 {
		t.Errorf("SyncNow should delegate once, got %d", eng.syncNows)
	}
}

func TestCompanionRuntime_SyncNowNoEngineIsNoop(t *testing.T) {
	ctx := context.Background()
	rt, _ := circleai.NewCompanionRuntime(newFakeConsolidator(), circleai.CompanionRuntimeDeps{
		Options: noPeriodicOptions(false),
	})
	if err := rt.SyncNow(ctx); err != nil {
		t.Errorf("SyncNow without engine should be a no-op, got %v", err)
	}
}

func TestCompanionRuntime_IngestMediaWithoutIngesterErrors(t *testing.T) {
	ctx := context.Background()
	rt, _ := circleai.NewCompanionRuntime(newFakeConsolidator(), circleai.CompanionRuntimeDeps{
		Options: noPeriodicOptions(false),
	})
	_, err := rt.IngestMedia(ctx, circleai.MediaImage, []byte{1, 2, 3}, circleai.IngestOptions{})
	if err == nil {
		t.Error("IngestMedia without ingester should error")
	}
}

func TestCompanionRuntime_StopClosesEngineAndIsIdempotent(t *testing.T) {
	ctx := context.Background()
	eng := &fakeSyncEngine{}
	rt, _ := circleai.NewCompanionRuntime(newFakeConsolidator(), circleai.CompanionRuntimeDeps{
		SyncEngine: eng,
		Options:    noPeriodicOptions(false),
	})
	if err := rt.Start(ctx); err != nil {
		t.Fatalf("Start: %v", err)
	}
	if err := rt.Stop(ctx); err != nil {
		t.Fatalf("Stop: %v", err)
	}
	if eng.closed != 1 {
		t.Errorf("engine Close count: got %d want 1", eng.closed)
	}
	// Second stop is a no-op (does not re-close).
	if err := rt.Stop(ctx); err != nil {
		t.Fatalf("Stop 2: %v", err)
	}
	if eng.closed != 1 {
		t.Errorf("second Stop should not re-close: got %d", eng.closed)
	}
}

func TestCompanionRuntime_PeriodicLoopRunsWhenEnabled(t *testing.T) {
	ctx := context.Background()
	cons := newFakeConsolidator()
	opts := &circleai.CompanionRuntimeOptions{
		DailyTickInterval: 5 * time.Millisecond,
		InitialDelay:      0,
		CatchUpOnStart:    false,
	}
	rt, _ := circleai.NewCompanionRuntime(cons, circleai.CompanionRuntimeDeps{Options: opts})
	if err := rt.Start(ctx); err != nil {
		t.Fatalf("Start: %v", err)
	}

	// Poll until at least one daily tick fires or we time out.
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if cons.count(circleai.SleepDaily) >= 1 {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}
	if err := rt.Stop(ctx); err != nil {
		t.Fatalf("Stop: %v", err)
	}
	if cons.count(circleai.SleepDaily) < 1 {
		t.Error("daily periodic loop should have ticked at least once")
	}
}

func TestCompanionRuntime_NilConsolidatorErrors(t *testing.T) {
	if _, err := circleai.NewCompanionRuntime(nil, circleai.CompanionRuntimeDeps{}); err == nil {
		t.Error("nil consolidator should error")
	}
}
