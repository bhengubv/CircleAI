// proactive_scheduler.go
//
// Ported from CircleAI.Companion.Proactive (a separate C# project) — the C#
// reference. The proactive scheduling substrate: a 5-field cron parser, the task
// primitives, the three split contracts (source / runner / scheduler), the
// default cron-tick scheduler with per-context last-run tracking, the
// null/in-memory/delegate implementations, and the background tick loop.
//
// C# files ported here:
//   - CronExpression.cs                         -> CronExpression
//   - Primitives.cs                             -> ProactiveTask / ProactiveTrigger /
//                                                  ProactiveTaskRunResult / ProactiveTaskLoadError
//   - Contracts.cs                              -> IProactiveTaskSource /
//                                                  IProactiveTaskRunner / IProactiveScheduler
//   - ProactiveScheduler.cs                     -> ProactiveScheduler
//   - NullImplementations.cs                    -> NullProactiveTaskSource /
//                                                  NullProactiveTaskRunner /
//                                                  InMemoryProactiveTaskSource /
//                                                  DelegateProactiveTaskRunner
//   - ProactiveSchedulerBackgroundService.cs    -> ProactiveSchedulerBackgroundService /
//                                                  ProactiveSchedulerOptions

package circleai

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ===================================================================
// CronExpression — 5-field cron parser (minute hour dom month dow).
//
// Supports '*', integers, ranges (1-5), lists (1,15,30), and step values
// (*/15). Day-of-week is 0=Sunday..6=Saturday. Day-of-month AND day-of-week must
// both match (AND semantics, matching the C# reference).
// ===================================================================

// CronExpression is a parsed 5-field cron expression. Ported from the C#
// CronExpression.
type CronExpression struct {
	minutes     map[int]struct{}
	hours       map[int]struct{}
	daysOfMonth map[int]struct{}
	months      map[int]struct{}
	daysOfWeek  map[int]struct{}
}

// ParseCronExpression parses a 5-field cron expression. Ported from the C#
// CronExpression.Parse. Returns an error for the wrong field count or an
// out-of-range / malformed field.
func ParseCronExpression(expression string) (*CronExpression, error) {
	fields := splitCronFields(expression)
	if len(fields) != 5 {
		return nil, fmt.Errorf("Cron expression must have 5 fields, got %d: '%s'", len(fields), expression)
	}
	minutes, err := parseCronField(fields[0], 0, 59)
	if err != nil {
		return nil, err
	}
	hours, err := parseCronField(fields[1], 0, 23)
	if err != nil {
		return nil, err
	}
	dom, err := parseCronField(fields[2], 1, 31)
	if err != nil {
		return nil, err
	}
	months, err := parseCronField(fields[3], 1, 12)
	if err != nil {
		return nil, err
	}
	dow, err := parseCronField(fields[4], 0, 6)
	if err != nil {
		return nil, err
	}
	return &CronExpression{
		minutes:     minutes,
		hours:       hours,
		daysOfMonth: dom,
		months:      months,
		daysOfWeek:  dow,
	}, nil
}

// GetNextOccurrence returns the next UTC time strictly after `after` when the
// expression matches. Searches at most one year forward. Ported from the C#
// GetNextOccurrence.
func (c *CronExpression) GetNextOccurrence(after time.Time) (time.Time, error) {
	t := after.Add(time.Minute)
	// Truncate to minute, UTC (matches the C# new DateTimeOffset(... 0, Zero)).
	t = time.Date(t.Year(), t.Month(), t.Day(), t.Hour(), t.Minute(), 0, 0, time.UTC)
	limit := t.AddDate(1, 0, 0)
	for !t.After(limit) {
		if c.Matches(t) {
			return t, nil
		}
		t = t.Add(time.Minute)
	}
	return time.Time{}, errors.New("Cron expression does not match any time in the next year.")
}

// Matches reports whether moment satisfies all five fields. Ported from the C#
// Matches. moment is interpreted in UTC.
func (c *CronExpression) Matches(moment time.Time) bool {
	m := moment.UTC()
	if _, ok := c.minutes[m.Minute()]; !ok {
		return false
	}
	if _, ok := c.hours[m.Hour()]; !ok {
		return false
	}
	if _, ok := c.daysOfMonth[m.Day()]; !ok {
		return false
	}
	if _, ok := c.months[int(m.Month())]; !ok {
		return false
	}
	// Go time.Weekday: Sunday=0..Saturday=6 — same convention as the C# cast.
	if _, ok := c.daysOfWeek[int(m.Weekday())]; !ok {
		return false
	}
	return true
}

// splitCronFields splits on whitespace, dropping empties and trimming — matches
// the C# Split(' ', RemoveEmptyEntries | TrimEntries).
func splitCronFields(expression string) []string {
	return strings.Fields(expression)
}

func parseCronField(field string, min, max int) (map[int]struct{}, error) {
	values := make(map[int]struct{})
	for _, part := range strings.Split(field, ",") {
		if err := expandCronPart(strings.TrimSpace(part), min, max, values); err != nil {
			return nil, err
		}
	}
	if len(values) == 0 {
		return nil, fmt.Errorf("Cron field '%s' resolved to no values.", field)
	}
	return values, nil
}

func expandCronPart(part string, min, max int, sink map[int]struct{}) error {
	step := 1
	if slash := strings.IndexByte(part, '/'); slash >= 0 {
		s, err := strconv.Atoi(part[slash+1:])
		if err != nil || s <= 0 {
			return fmt.Errorf("Cron step '%s' is not a positive integer.", part)
		}
		step = s
		part = part[:slash]
	}

	var rangeStart, rangeEnd int
	switch {
	case part == "*":
		rangeStart = min
		rangeEnd = max
	case strings.Contains(part, "-"):
		dash := strings.IndexByte(part, '-')
		a, err1 := strconv.Atoi(part[:dash])
		b, err2 := strconv.Atoi(part[dash+1:])
		if err1 != nil || err2 != nil {
			return fmt.Errorf("Cron part '%s' out of range [%d,%d].", part, min, max)
		}
		rangeStart = a
		rangeEnd = b
	default:
		v, err := strconv.Atoi(part)
		if err != nil {
			return fmt.Errorf("Cron part '%s' out of range [%d,%d].", part, min, max)
		}
		rangeStart = v
		rangeEnd = v
	}

	if rangeStart < min || rangeEnd > max || rangeStart > rangeEnd {
		return fmt.Errorf("Cron part '%s' out of range [%d,%d].", part, min, max)
	}

	for v := rangeStart; v <= rangeEnd; v += step {
		sink[v] = struct{}{}
	}
	return nil
}

// ===================================================================
// Primitives — ProactiveTask / Trigger / RunResult / LoadError.
// ===================================================================

// ProactiveTrigger describes how a task fires. Exactly one of Cron, OnEvent, or
// Manual is meaningful. Ported from the C# record ProactiveTrigger.
type ProactiveTrigger struct {
	// Cron is a 5-field cron expression (nil for non-cron triggers).
	Cron *string
	// OnEvent is an event name (nil for non-event triggers).
	OnEvent *string
	// Manual is true if the task only fires when explicitly invoked.
	Manual bool
}

// ProactiveTask is one scheduled task. Payload is opaque to the substrate — the
// host's runner reads it. Ported from the C# record ProactiveTask.
type ProactiveTask struct {
	// ID is the unique task id within its source (used for last-run tracking).
	ID string
	// Trigger is the cron / event / manual trigger.
	Trigger ProactiveTrigger
	// Payload is the consumer-owned object; the substrate never inspects it.
	Payload any
	// SourceContext keeps per-context last-run state separate (nil → "").
	SourceContext *string
}

// ProactiveTaskRunResult is one run outcome. Ported from the C# record
// ProactiveTaskRunResult.
type ProactiveTaskRunResult struct {
	TaskID         string
	Success        bool
	FailureMessage *string
}

// ProactiveTaskLoadError is one parse/load failure surfaced from the source.
// Ported from the C# record ProactiveTaskLoadError.
type ProactiveTaskLoadError struct {
	TaskID        string
	Message       string
	SourceContext *string
}

// ===================================================================
// Contracts — source / runner / scheduler.
// ===================================================================

// IProactiveTaskSource is where the active set of tasks comes from. Ported from
// the C# IProactiveTaskSource.
type IProactiveTaskSource interface {
	// BackendID self-identifies the backend ("vault-fs", "in-memory", "null").
	BackendID() string
	// GetTasks snapshots the current set of tasks.
	GetTasks(ctx context.Context) ([]ProactiveTask, error)
	// GetErrors returns parse/load failures from the last refresh.
	GetErrors(ctx context.Context) ([]ProactiveTaskLoadError, error)
}

// IProactiveTaskRunner executes one task. Ported from the C#
// IProactiveTaskRunner.
type IProactiveTaskRunner interface {
	// BackendID self-identifies the backend ("workflow-engine", "delegate", "null").
	BackendID() string
	// Run executes one task. variables carry trigger-time context (may be nil).
	Run(ctx context.Context, task ProactiveTask, variables map[string]string) (ProactiveTaskRunResult, error)
}

// IProactiveScheduler is the scheduling loop: cron parsing, last-run tracking,
// and event dispatch. Ported from the C# IProactiveScheduler.
type IProactiveScheduler interface {
	// BackendID self-identifies the scheduler.
	BackendID() string
	// Tasks is the current snapshot (populated by Refresh).
	Tasks() []ProactiveTask
	// LoadErrors are the load errors from the source.
	LoadErrors() []ProactiveTaskLoadError
	// GetNextRun returns the next cron firing for a task, or nil for non-cron /
	// unparseable triggers.
	GetNextRun(task ProactiveTask, after time.Time) *time.Time
	// Refresh re-snapshots tasks from the source, dropping last-run state for
	// tasks the source no longer reports.
	Refresh(ctx context.Context) error
	// Tick runs every cron task whose next-run is at-or-before now and that has
	// not already fired for the matching minute.
	Tick(ctx context.Context, now time.Time) error
	// DispatchEvent fires every event-triggered task matching eventName.
	DispatchEvent(ctx context.Context, eventName string, variables map[string]string) error
	// RunByID is a one-shot manual run by task id.
	RunByID(ctx context.Context, id string, variables map[string]string) (ProactiveTaskRunResult, error)
}

// ===================================================================
// ProactiveScheduler — default IProactiveScheduler.
//
// Owns cron parsing, per-(context, taskId) last-run tracking, refresh, and event
// dispatch. Singleton-safe. Ported from the C# ProactiveScheduler.
// ===================================================================

// ProactiveScheduler is the default cron-tick scheduler. Ported from the C#
// ProactiveScheduler.
type ProactiveScheduler struct {
	source IProactiveTaskSource
	runner IProactiveTaskRunner

	gate   sync.Mutex
	tasks  []ProactiveTask
	errors []ProactiveTaskLoadError

	// lastRuns: context -> taskId -> last run time. Context = SourceContext or
	// "". Keeps multi-tenant hosts' schedules independent.
	lastRuns map[string]map[string]time.Time
}

// NewProactiveScheduler builds the default scheduler. source and runner are
// required. Ported from the C# ProactiveScheduler constructor.
func NewProactiveScheduler(source IProactiveTaskSource, runner IProactiveTaskRunner) (*ProactiveScheduler, error) {
	if source == nil {
		return nil, errors.New("source required")
	}
	if runner == nil {
		return nil, errors.New("runner required")
	}
	return &ProactiveScheduler{
		source:   source,
		runner:   runner,
		lastRuns: make(map[string]map[string]time.Time),
	}, nil
}

// BackendID returns "default".
func (s *ProactiveScheduler) BackendID() string { return "default" }

// Tasks returns a snapshot copy of the current task set.
func (s *ProactiveScheduler) Tasks() []ProactiveTask {
	s.gate.Lock()
	defer s.gate.Unlock()
	out := make([]ProactiveTask, len(s.tasks))
	copy(out, s.tasks)
	return out
}

// LoadErrors returns a snapshot copy of the current load errors.
func (s *ProactiveScheduler) LoadErrors() []ProactiveTaskLoadError {
	s.gate.Lock()
	defer s.gate.Unlock()
	out := make([]ProactiveTaskLoadError, len(s.errors))
	copy(out, s.errors)
	return out
}

// GetNextRun returns the next cron firing for task after `after`, or nil for a
// non-cron or unparseable trigger. Ported from the C# GetNextRun.
func (s *ProactiveScheduler) GetNextRun(task ProactiveTask, after time.Time) *time.Time {
	if task.Trigger.Cron == nil {
		return nil
	}
	expr, err := ParseCronExpression(*task.Trigger.Cron)
	if err != nil {
		return nil
	}
	next, err := expr.GetNextOccurrence(after)
	if err != nil {
		return nil
	}
	return &next
}

// Refresh re-snapshots tasks + errors from the source and prunes last-run state
// for (context, taskId) pairs the source no longer reports. Ported from the C#
// RefreshAsync.
func (s *ProactiveScheduler) Refresh(ctx context.Context) error {
	snapshot, err := s.source.GetTasks(ctx)
	if err != nil {
		return err
	}
	errs, err := s.source.GetErrors(ctx)
	if err != nil {
		return err
	}

	s.gate.Lock()
	defer s.gate.Unlock()
	s.tasks = append([]ProactiveTask(nil), snapshot...)
	s.errors = append([]ProactiveTaskLoadError(nil), errs...)

	type ck struct{ ctx, id string }
	live := make(map[ck]struct{}, len(s.tasks))
	for _, t := range s.tasks {
		live[ck{contextKey(t.SourceContext), t.ID}] = struct{}{}
	}
	for ctxKey, ids := range s.lastRuns {
		for id := range ids {
			if _, ok := live[ck{ctxKey, id}]; !ok {
				delete(ids, id)
			}
		}
		if len(ids) == 0 {
			delete(s.lastRuns, ctxKey)
		}
	}
	return nil
}

// Tick runs every cron task due at-or-before now that has not already fired for
// its matching minute. Ported from the C# TickAsync.
func (s *ProactiveScheduler) Tick(ctx context.Context, now time.Time) error {
	s.gate.Lock()
	var candidates []ProactiveTask
	for _, t := range s.tasks {
		if t.Trigger.Cron != nil {
			candidates = append(candidates, t)
		}
	}
	s.gate.Unlock()

	for _, task := range candidates {
		if err := ctx.Err(); err != nil {
			return err
		}
		ctxKey := contextKey(task.SourceContext)

		s.gate.Lock()
		lastRun, ok := s.lastRuns[ctxKey][task.ID]
		if !ok {
			lastRun = time.Time{} // DateTimeOffset.MinValue
		}
		s.gate.Unlock()

		expr, err := ParseCronExpression(*task.Trigger.Cron)
		if err != nil {
			continue // parse error already surfaced via LoadErrors.
		}
		anchor := lastRun
		if lastRun.IsZero() {
			anchor = now.Add(-time.Minute)
		}
		next, err := expr.GetNextOccurrence(anchor)
		if err != nil {
			continue
		}
		if !next.After(now) { // next <= now
			if _, err := s.runner.Run(ctx, task, nil); err != nil {
				// A runner error does not crash the tick (C# swallows into the
				// per-task try/catch). Continue to the next candidate.
				continue
			}
			s.markRun(task, now)
		}
	}
	return nil
}

// DispatchEvent fires every event-triggered task whose OnEvent matches eventName
// (case-insensitive). Ported from the C# DispatchEventAsync.
func (s *ProactiveScheduler) DispatchEvent(ctx context.Context, eventName string, variables map[string]string) error {
	if strings.TrimSpace(eventName) == "" {
		return errors.New("eventName required")
	}
	s.gate.Lock()
	var matched []ProactiveTask
	for _, t := range s.tasks {
		if t.Trigger.OnEvent != nil && strings.EqualFold(*t.Trigger.OnEvent, eventName) {
			matched = append(matched, t)
		}
	}
	s.gate.Unlock()

	for _, task := range matched {
		if err := ctx.Err(); err != nil {
			return err
		}
		if _, err := s.runner.Run(ctx, task, variables); err != nil {
			return err
		}
		s.markRun(task, time.Now().UTC())
	}
	return nil
}

// RunByID runs a single task by id. Returns a failure result if no task matches.
// Ported from the C# RunByIdAsync.
func (s *ProactiveScheduler) RunByID(ctx context.Context, id string, variables map[string]string) (ProactiveTaskRunResult, error) {
	if strings.TrimSpace(id) == "" {
		return ProactiveTaskRunResult{}, errors.New("id required")
	}
	s.gate.Lock()
	var task *ProactiveTask
	for i := range s.tasks {
		if strings.EqualFold(s.tasks[i].ID, id) {
			t := s.tasks[i]
			task = &t
			break
		}
	}
	s.gate.Unlock()

	if task == nil {
		msg := fmt.Sprintf("No task with id '%s'.", id)
		return ProactiveTaskRunResult{TaskID: id, Success: false, FailureMessage: &msg}, nil
	}

	result, err := s.runner.Run(ctx, *task, variables)
	if err != nil {
		return ProactiveTaskRunResult{}, err
	}
	s.markRun(*task, time.Now().UTC())
	return result, nil
}

func (s *ProactiveScheduler) markRun(task ProactiveTask, when time.Time) {
	ctxKey := contextKey(task.SourceContext)
	s.gate.Lock()
	defer s.gate.Unlock()
	m, ok := s.lastRuns[ctxKey]
	if !ok {
		m = make(map[string]time.Time)
		s.lastRuns[ctxKey] = m
	}
	m[task.ID] = when
}

func contextKey(sourceContext *string) string {
	if sourceContext == nil {
		return ""
	}
	return *sourceContext
}

var _ IProactiveScheduler = (*ProactiveScheduler)(nil)

// ===================================================================
// Null / in-memory / delegate implementations.
// ===================================================================

// NullProactiveTaskSource is an empty source — no tasks, no errors. Ported from
// the C# NullProactiveTaskSource.
type NullProactiveTaskSource struct{}

// BackendID returns "null".
func (NullProactiveTaskSource) BackendID() string { return "null" }

// GetTasks returns no tasks.
func (NullProactiveTaskSource) GetTasks(context.Context) ([]ProactiveTask, error) {
	return []ProactiveTask{}, nil
}

// GetErrors returns no errors.
func (NullProactiveTaskSource) GetErrors(context.Context) ([]ProactiveTaskLoadError, error) {
	return []ProactiveTaskLoadError{}, nil
}

var _ IProactiveTaskSource = NullProactiveTaskSource{}

// NullProactiveTaskRunner reports every run as a failure with a
// "no runner registered" message. Fail-closed default. Ported from the C#
// NullProactiveTaskRunner.
type NullProactiveTaskRunner struct{}

// BackendID returns "null".
func (NullProactiveTaskRunner) BackendID() string { return "null" }

// Run returns a fail-closed result naming the missing runner.
func (NullProactiveTaskRunner) Run(_ context.Context, task ProactiveTask, _ map[string]string) (ProactiveTaskRunResult, error) {
	msg := "No IProactiveTaskRunner registered; using NullProactiveTaskRunner."
	return ProactiveTaskRunResult{TaskID: task.ID, Success: false, FailureMessage: &msg}, nil
}

var _ IProactiveTaskRunner = NullProactiveTaskRunner{}

// InMemoryProactiveTaskSource is an in-memory source for testing + simple
// consumers. Keyed by (sourceContext, id) so multi-tenant hosts can hold the
// same id in two contexts. Ported from the C# InMemoryProactiveTaskSource.
type InMemoryProactiveTaskSource struct {
	gate   sync.Mutex
	byKey  map[proactiveKey]ProactiveTask
	order  []proactiveKey // preserves insertion order for stable snapshots.
	errors []ProactiveTaskLoadError
}

type proactiveKey struct {
	ctx string
	id  string
}

// NewInMemoryProactiveTaskSource returns an empty in-memory source.
func NewInMemoryProactiveTaskSource() *InMemoryProactiveTaskSource {
	return &InMemoryProactiveTaskSource{byKey: make(map[proactiveKey]ProactiveTask)}
}

// BackendID returns "in-memory".
func (s *InMemoryProactiveTaskSource) BackendID() string { return "in-memory" }

// Upsert inserts or replaces a task by (SourceContext, ID).
func (s *InMemoryProactiveTaskSource) Upsert(task ProactiveTask) {
	k := proactiveKeyOf(task)
	s.gate.Lock()
	if _, exists := s.byKey[k]; !exists {
		s.order = append(s.order, k)
	}
	s.byKey[k] = task
	s.gate.Unlock()
}

// Remove deletes a task by id (+ optional context). Returns whether it existed.
func (s *InMemoryProactiveTaskSource) Remove(id string, sourceContext *string) bool {
	if strings.TrimSpace(id) == "" {
		return false
	}
	k := proactiveKey{ctx: contextKey(sourceContext), id: id}
	s.gate.Lock()
	defer s.gate.Unlock()
	if _, ok := s.byKey[k]; !ok {
		return false
	}
	delete(s.byKey, k)
	for i, ok := range s.order {
		if ok == k {
			s.order = append(s.order[:i], s.order[i+1:]...)
			break
		}
	}
	return true
}

// Clear removes all tasks and errors.
func (s *InMemoryProactiveTaskSource) Clear() {
	s.gate.Lock()
	s.byKey = make(map[proactiveKey]ProactiveTask)
	s.order = nil
	s.errors = nil
	s.gate.Unlock()
}

// RecordError records a load error surfaced through the source.
func (s *InMemoryProactiveTaskSource) RecordError(e ProactiveTaskLoadError) {
	s.gate.Lock()
	s.errors = append(s.errors, e)
	s.gate.Unlock()
}

// GetTasks snapshots the current tasks (in insertion order).
func (s *InMemoryProactiveTaskSource) GetTasks(context.Context) ([]ProactiveTask, error) {
	s.gate.Lock()
	defer s.gate.Unlock()
	out := make([]ProactiveTask, 0, len(s.order))
	for _, k := range s.order {
		out = append(out, s.byKey[k])
	}
	return out, nil
}

// GetErrors snapshots the current load errors.
func (s *InMemoryProactiveTaskSource) GetErrors(context.Context) ([]ProactiveTaskLoadError, error) {
	s.gate.Lock()
	defer s.gate.Unlock()
	out := make([]ProactiveTaskLoadError, len(s.errors))
	copy(out, s.errors)
	return out, nil
}

func proactiveKeyOf(task ProactiveTask) proactiveKey {
	return proactiveKey{ctx: contextKey(task.SourceContext), id: task.ID}
}

var _ IProactiveTaskSource = (*InMemoryProactiveTaskSource)(nil)

// ProactiveRunHandler runs one task for a DelegateProactiveTaskRunner.
type ProactiveRunHandler func(ctx context.Context, task ProactiveTask, variables map[string]string) (ProactiveTaskRunResult, error)

// DelegateProactiveTaskRunner hands every task to a host-supplied delegate.
// Ported from the C# DelegateProactiveTaskRunner.
type DelegateProactiveTaskRunner struct {
	handler ProactiveRunHandler
}

// NewDelegateProactiveTaskRunner wraps handler (required).
func NewDelegateProactiveTaskRunner(handler ProactiveRunHandler) (*DelegateProactiveTaskRunner, error) {
	if handler == nil {
		return nil, errors.New("handler required")
	}
	return &DelegateProactiveTaskRunner{handler: handler}, nil
}

// BackendID returns "delegate".
func (r *DelegateProactiveTaskRunner) BackendID() string { return "delegate" }

// Run forwards to the wrapped handler.
func (r *DelegateProactiveTaskRunner) Run(ctx context.Context, task ProactiveTask, variables map[string]string) (ProactiveTaskRunResult, error) {
	return r.handler(ctx, task, variables)
}

var _ IProactiveTaskRunner = (*DelegateProactiveTaskRunner)(nil)

// ===================================================================
// ProactiveSchedulerBackgroundService — tick loop.
//
// Calls Refresh once at startup, then loops on a one-minute timer calling Tick,
// re-refreshing every RefreshInterval. Ported from the C#
// ProactiveSchedulerBackgroundService.
// ===================================================================

// ProactiveSchedulerOptions tunes the background tick loop. Ported from the C#
// ProactiveSchedulerOptions.
type ProactiveSchedulerOptions struct {
	// TickInterval is how often the scheduler ticks (0 → 1 minute).
	TickInterval time.Duration
	// RefreshInterval is how often the source is re-snapshotted (0 → 5 minutes).
	RefreshInterval time.Duration
}

func (o ProactiveSchedulerOptions) tickInterval() time.Duration {
	if o.TickInterval <= 0 {
		return time.Minute
	}
	return o.TickInterval
}

func (o ProactiveSchedulerOptions) refreshInterval() time.Duration {
	if o.RefreshInterval <= 0 {
		return 5 * time.Minute
	}
	return o.RefreshInterval
}

// ProactiveSchedulerBackgroundService drives the scheduler tick loop. Ported
// from the C# ProactiveSchedulerBackgroundService.
type ProactiveSchedulerBackgroundService struct {
	scheduler IProactiveScheduler
	options   ProactiveSchedulerOptions
	now       func() time.Time

	mu     sync.Mutex
	cancel context.CancelFunc
	done   chan struct{}
}

// NewProactiveSchedulerBackgroundService builds the background service.
// scheduler is required.
func NewProactiveSchedulerBackgroundService(scheduler IProactiveScheduler, options ProactiveSchedulerOptions) (*ProactiveSchedulerBackgroundService, error) {
	if scheduler == nil {
		return nil, errors.New("scheduler required")
	}
	return &ProactiveSchedulerBackgroundService{
		scheduler: scheduler,
		options:   options,
		now:       func() time.Time { return time.Now().UTC() },
	}, nil
}

// Start runs the tick loop until Stop or ctx cancellation. Idempotent.
func (b *ProactiveSchedulerBackgroundService) Start(ctx context.Context) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.cancel != nil {
		return nil
	}
	loopCtx, cancel := context.WithCancel(ctx)
	b.cancel = cancel
	b.done = make(chan struct{})
	go b.execute(loopCtx, b.done)
	return nil
}

// Stop halts the tick loop and waits for it to exit. Idempotent.
func (b *ProactiveSchedulerBackgroundService) Stop(ctx context.Context) error {
	b.mu.Lock()
	cancel := b.cancel
	done := b.done
	b.cancel = nil
	b.done = nil
	b.mu.Unlock()
	if cancel == nil {
		return nil
	}
	cancel()
	if done != nil {
		select {
		case <-done:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	return nil
}

func (b *ProactiveSchedulerBackgroundService) execute(ctx context.Context, done chan struct{}) {
	defer close(done)

	// Initial refresh — populate before the first tick. A failure is swallowed
	// (LogWarning in C#).
	if ctx.Err() != nil {
		return
	}
	_ = b.scheduler.Refresh(ctx)

	lastRefresh := b.now()
	tick := b.options.tickInterval()
	refresh := b.options.refreshInterval()

	for {
		timer := time.NewTimer(tick)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}

		now := b.now()
		if now.Sub(lastRefresh) >= refresh {
			_ = b.scheduler.Refresh(ctx)
			lastRefresh = now
		}
		_ = b.scheduler.Tick(ctx, now)
	}
}
