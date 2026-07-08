// proactive_scheduler_test.go
//
// Verifies the proactive scheduling substrate ported from
// CircleAI.Companion.Proactive: CronExpression (fixtures/cron_expression.json),
// the task primitives, the default ProactiveScheduler (cron tick + event
// dispatch + manual run + last-run tracking + refresh pruning), the
// null/in-memory/delegate impls, and the background tick loop.

package circleai_test

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── CronExpression (fixtures) ────────────────────────────────────────────────

type cronFixture struct {
	ParseOK    []string `json:"parse_ok"`
	ParseError []string `json:"parse_error"`
	Next       []struct {
		ID       string `json:"id"`
		Expr     string `json:"expr"`
		After    string `json:"after"`
		Expected string `json:"expected"`
	} `json:"next"`
	Matches []struct {
		ID       string `json:"id"`
		Expr     string `json:"expr"`
		Moment   string `json:"moment"`
		Expected bool   `json:"expected"`
	} `json:"matches"`
}

func TestCronExpression_Fixtures(t *testing.T) {
	data, err := os.ReadFile(filepath.Join(fixturesDir(t), "cron_expression.json"))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	var fix cronFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse fixture: %v", err)
	}

	for _, expr := range fix.ParseOK {
		if _, err := circleai.ParseCronExpression(expr); err != nil {
			t.Errorf("Parse(%q) should succeed: %v", expr, err)
		}
	}
	for _, expr := range fix.ParseError {
		if _, err := circleai.ParseCronExpression(expr); err == nil {
			t.Errorf("Parse(%q) should fail", expr)
		}
	}

	for _, c := range fix.Next {
		c := c
		t.Run("next/"+c.ID, func(t *testing.T) {
			expr, err := circleai.ParseCronExpression(c.Expr)
			if err != nil {
				t.Fatalf("Parse: %v", err)
			}
			after := mustTime(t, c.After)
			want := mustTime(t, c.Expected)
			got, err := expr.GetNextOccurrence(after)
			if err != nil {
				t.Fatalf("GetNextOccurrence: %v", err)
			}
			if !got.Equal(want) {
				t.Errorf("next: got %s want %s", got.Format(time.RFC3339), want.Format(time.RFC3339))
			}
		})
	}

	for _, c := range fix.Matches {
		c := c
		t.Run("matches/"+c.ID, func(t *testing.T) {
			expr, err := circleai.ParseCronExpression(c.Expr)
			if err != nil {
				t.Fatalf("Parse: %v", err)
			}
			if got := expr.Matches(mustTime(t, c.Moment)); got != c.Expected {
				t.Errorf("Matches: got %v want %v", got, c.Expected)
			}
		})
	}
}

// ── recordingRunner ──────────────────────────────────────────────────────────

type recordingRunner struct {
	mu   sync.Mutex
	runs []struct {
		id   string
		vars map[string]string
	}
	result func(task circleai.ProactiveTask) circleai.ProactiveTaskRunResult
}

func (r *recordingRunner) BackendID() string { return "recording" }

func (r *recordingRunner) Run(_ context.Context, task circleai.ProactiveTask, vars map[string]string) (circleai.ProactiveTaskRunResult, error) {
	r.mu.Lock()
	r.runs = append(r.runs, struct {
		id   string
		vars map[string]string
	}{task.ID, vars})
	r.mu.Unlock()
	if r.result != nil {
		return r.result(task), nil
	}
	return circleai.ProactiveTaskRunResult{TaskID: task.ID, Success: true}, nil
}

func (r *recordingRunner) count() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.runs)
}

func (r *recordingRunner) ids() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]string, len(r.runs))
	for i, x := range r.runs {
		out[i] = x.id
	}
	return out
}

func cronPtr(s string) *string { return &s }

// ── ProactiveScheduler ───────────────────────────────────────────────────────

func TestProactiveScheduler_TickFiresDueCron(t *testing.T) {
	ctx := context.Background()
	src := circleai.NewInMemoryProactiveTaskSource()
	// Fires at minute 0 of every hour.
	src.Upsert(circleai.ProactiveTask{
		ID:      "hourly",
		Trigger: circleai.ProactiveTrigger{Cron: cronPtr("0 * * * *")},
		Payload: "p",
	})
	runner := &recordingRunner{}
	sched, err := circleai.NewProactiveScheduler(src, runner)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if err := sched.Refresh(ctx); err != nil {
		t.Fatalf("Refresh: %v", err)
	}
	if len(sched.Tasks()) != 1 {
		t.Fatalf("tasks after refresh: %d", len(sched.Tasks()))
	}

	// At 10:00:00 the task is due (next occurrence after 09:59:00 is 10:00:00 <= now).
	now := mustTime(t, "2026-07-08T10:00:05Z")
	if err := sched.Tick(ctx, now); err != nil {
		t.Fatalf("Tick: %v", err)
	}
	if runner.count() != 1 {
		t.Fatalf("expected 1 run, got %d", runner.count())
	}

	// Ticking again in the same minute must NOT re-fire (last-run guard).
	if err := sched.Tick(ctx, now.Add(10*time.Second)); err != nil {
		t.Fatalf("Tick 2: %v", err)
	}
	if runner.count() != 1 {
		t.Errorf("re-fire within same minute: got %d runs", runner.count())
	}

	// An hour later it fires again.
	later := mustTime(t, "2026-07-08T11:00:02Z")
	if err := sched.Tick(ctx, later); err != nil {
		t.Fatalf("Tick 3: %v", err)
	}
	if runner.count() != 2 {
		t.Errorf("next-hour fire: got %d runs", runner.count())
	}
}

func TestProactiveScheduler_EventDispatchAndManualRun(t *testing.T) {
	ctx := context.Background()
	src := circleai.NewInMemoryProactiveTaskSource()
	src.Upsert(circleai.ProactiveTask{
		ID:      "on-note",
		Trigger: circleai.ProactiveTrigger{OnEvent: cronPtr("note-saved")},
		Payload: "p",
	})
	src.Upsert(circleai.ProactiveTask{
		ID:      "manual-only",
		Trigger: circleai.ProactiveTrigger{Manual: true},
		Payload: "p",
	})
	runner := &recordingRunner{}
	sched, _ := circleai.NewProactiveScheduler(src, runner)
	_ = sched.Refresh(ctx)

	// Event dispatch fires matching event tasks (case-insensitive) with vars.
	if err := sched.DispatchEvent(ctx, "NOTE-SAVED", map[string]string{"path": "/n.md"}); err != nil {
		t.Fatalf("DispatchEvent: %v", err)
	}
	if runner.count() != 1 || runner.ids()[0] != "on-note" {
		t.Errorf("event dispatch: got %v", runner.ids())
	}

	// A cron tick does NOT fire event/manual tasks.
	if err := sched.Tick(ctx, mustTime(t, "2026-07-08T10:00:00Z")); err != nil {
		t.Fatalf("Tick: %v", err)
	}
	if runner.count() != 1 {
		t.Errorf("tick should not fire non-cron tasks: %v", runner.ids())
	}

	// Manual run by id.
	res, err := sched.RunByID(ctx, "manual-only", nil)
	if err != nil {
		t.Fatalf("RunByID: %v", err)
	}
	if !res.Success {
		t.Errorf("manual run result: %+v", res)
	}
	// Unknown id => failure result, no error.
	res, err = sched.RunByID(ctx, "ghost", nil)
	if err != nil {
		t.Fatalf("RunByID(ghost): %v", err)
	}
	if res.Success || res.FailureMessage == nil {
		t.Errorf("unknown id should fail: %+v", res)
	}
}

func TestProactiveScheduler_GetNextRunAndRefreshPrune(t *testing.T) {
	ctx := context.Background()
	src := circleai.NewInMemoryProactiveTaskSource()
	cronTask := circleai.ProactiveTask{ID: "c", Trigger: circleai.ProactiveTrigger{Cron: cronPtr("0 6 * * *")}, Payload: "p"}
	manualTask := circleai.ProactiveTask{ID: "m", Trigger: circleai.ProactiveTrigger{Manual: true}, Payload: "p"}
	badCron := circleai.ProactiveTask{ID: "b", Trigger: circleai.ProactiveTrigger{Cron: cronPtr("not a cron")}, Payload: "p"}
	src.Upsert(cronTask)
	src.Upsert(manualTask)
	src.Upsert(badCron)
	sched, _ := circleai.NewProactiveScheduler(src, &recordingRunner{})
	_ = sched.Refresh(ctx)

	after := mustTime(t, "2026-07-08T10:00:00Z")
	if next := sched.GetNextRun(cronTask, after); next == nil || !next.Equal(mustTime(t, "2026-07-09T06:00:00Z")) {
		t.Errorf("GetNextRun(cron): got %v", next)
	}
	if next := sched.GetNextRun(manualTask, after); next != nil {
		t.Errorf("GetNextRun(manual) should be nil, got %v", next)
	}
	if next := sched.GetNextRun(badCron, after); next != nil {
		t.Errorf("GetNextRun(bad cron) should be nil, got %v", next)
	}

	// Fire the cron task to seed last-run state, then remove it and refresh:
	// pruning drops its last-run state (no crash, task gone).
	_ = sched.Tick(ctx, mustTime(t, "2026-07-08T06:00:01Z"))
	src.Remove("c", nil)
	if err := sched.Refresh(ctx); err != nil {
		t.Fatalf("Refresh after remove: %v", err)
	}
	for _, task := range sched.Tasks() {
		if task.ID == "c" {
			t.Error("removed task should be gone after refresh")
		}
	}
}

// ── Null / delegate impls ────────────────────────────────────────────────────

func TestNullAndDelegateImpls(t *testing.T) {
	ctx := context.Background()

	var src circleai.IProactiveTaskSource = circleai.NullProactiveTaskSource{}
	if src.BackendID() != "null" {
		t.Errorf("null source backend: %q", src.BackendID())
	}
	tasks, _ := src.GetTasks(ctx)
	errs, _ := src.GetErrors(ctx)
	if len(tasks) != 0 || len(errs) != 0 {
		t.Errorf("null source should be empty: %d tasks, %d errs", len(tasks), len(errs))
	}

	var nr circleai.IProactiveTaskRunner = circleai.NullProactiveTaskRunner{}
	res, _ := nr.Run(ctx, circleai.ProactiveTask{ID: "x"}, nil)
	if res.Success || res.FailureMessage == nil {
		t.Errorf("null runner should fail-closed: %+v", res)
	}

	// Delegate runner forwards to the handler.
	var gotID string
	dr, err := circleai.NewDelegateProactiveTaskRunner(func(_ context.Context, task circleai.ProactiveTask, _ map[string]string) (circleai.ProactiveTaskRunResult, error) {
		gotID = task.ID
		return circleai.ProactiveTaskRunResult{TaskID: task.ID, Success: true}, nil
	})
	if err != nil {
		t.Fatalf("delegate ctor: %v", err)
	}
	if dr.BackendID() != "delegate" {
		t.Errorf("delegate backend: %q", dr.BackendID())
	}
	if _, err := dr.Run(ctx, circleai.ProactiveTask{ID: "d1"}, nil); err != nil {
		t.Fatalf("delegate Run: %v", err)
	}
	if gotID != "d1" {
		t.Errorf("delegate handler saw id %q", gotID)
	}
	if _, err := circleai.NewDelegateProactiveTaskRunner(nil); err == nil {
		t.Error("nil handler should error")
	}
}

// ── InMemory source multi-context ────────────────────────────────────────────

func TestInMemoryProactiveTaskSource_MultiContext(t *testing.T) {
	ctx := context.Background()
	src := circleai.NewInMemoryProactiveTaskSource()
	ctxA, ctxB := "tenantA", "tenantB"
	src.Upsert(circleai.ProactiveTask{ID: "t", Trigger: circleai.ProactiveTrigger{Manual: true}, Payload: 1, SourceContext: &ctxA})
	src.Upsert(circleai.ProactiveTask{ID: "t", Trigger: circleai.ProactiveTrigger{Manual: true}, Payload: 2, SourceContext: &ctxB})

	got, _ := src.GetTasks(ctx)
	if len(got) != 2 {
		t.Fatalf("same id in two contexts should coexist: got %d", len(got))
	}

	// Remove one context leaves the other.
	if !src.Remove("t", &ctxA) {
		t.Error("remove tenantA/t should report true")
	}
	got, _ = src.GetTasks(ctx)
	if len(got) != 1 || got[0].SourceContext == nil || *got[0].SourceContext != ctxB {
		t.Errorf("after remove: got %+v", got)
	}

	// RecordError surfaces through GetErrors.
	src.RecordError(circleai.ProactiveTaskLoadError{TaskID: "t", Message: "boom"})
	errs, _ := src.GetErrors(ctx)
	if len(errs) != 1 || errs[0].Message != "boom" {
		t.Errorf("errors: %+v", errs)
	}

	src.Clear()
	got, _ = src.GetTasks(ctx)
	errs, _ = src.GetErrors(ctx)
	if len(got) != 0 || len(errs) != 0 {
		t.Errorf("clear should empty everything: %d tasks, %d errs", len(got), len(errs))
	}
}

// ── Background service ───────────────────────────────────────────────────────

func TestProactiveSchedulerBackgroundService(t *testing.T) {
	ctx := context.Background()
	src := circleai.NewInMemoryProactiveTaskSource()
	src.Upsert(circleai.ProactiveTask{
		ID:      "min",
		Trigger: circleai.ProactiveTrigger{Cron: cronPtr("* * * * *")}, // every minute
		Payload: "p",
	})
	runner := &recordingRunner{}
	sched, _ := circleai.NewProactiveScheduler(src, runner)

	bg, err := circleai.NewProactiveSchedulerBackgroundService(sched, circleai.ProactiveSchedulerOptions{
		TickInterval:    20 * time.Millisecond,
		RefreshInterval: 20 * time.Millisecond,
	})
	if err != nil {
		t.Fatalf("bg ctor: %v", err)
	}
	if err := bg.Start(ctx); err != nil {
		t.Fatalf("Start: %v", err)
	}

	// Within a few tick intervals the every-minute task fires at least once
	// (its next-run after now-1min is <= now).
	deadline := time.Now().Add(2 * time.Second)
	for runner.count() == 0 && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if runner.count() == 0 {
		t.Error("background service should have fired the every-minute task")
	}

	if err := bg.Stop(ctx); err != nil {
		t.Fatalf("Stop: %v", err)
	}
	// Stop is idempotent.
	if err := bg.Stop(ctx); err != nil {
		t.Fatalf("second Stop: %v", err)
	}
}
