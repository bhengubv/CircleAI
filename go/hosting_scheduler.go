// hosting_scheduler.go
//
// Ports CircleAI.Hosting scheduling runtime:
//   IScheduledTaskStore (IScheduledTaskStore.cs)
//   InMemoryScheduledTaskStore (InMemoryScheduledTaskStore.cs)
//   ScheduledAIService + JobCompletedEventArgs (ScheduledAIService.cs)
//
// The scheduled service runs a background poll loop that fires due cron jobs
// via IAIService.Ask, recomputes NextRunUTC from the cron expression, and
// invokes a completion callback for host-side delivery routing.

package circleai

import (
	"context"
	"sync"
	"time"
)

// IScheduledTaskStore is the persistence contract for CronJob records.
// Ports CircleAI.Hosting.IScheduledTaskStore. Implementations must be
// thread-safe.
type IScheduledTaskStore interface {
	// List returns every registered job, regardless of enabled state.
	List(ctx context.Context) ([]CronJob, error)
	// Get returns the job with the given id, or nil if not found.
	Get(ctx context.Context, id string) (*CronJob, error)
	// Upsert inserts or replaces the job identified by CronJob.ID.
	Upsert(ctx context.Context, job CronJob) (CronJob, error)
	// Delete removes the job with the given id. No-op if it does not exist.
	Delete(ctx context.Context, id string) error
	// GetDueJobs returns all enabled jobs whose NextRunUTC is in the past.
	GetDueJobs(ctx context.Context) ([]CronJob, error)
}

// InMemoryScheduledTaskStore is a thread-safe, in-process IScheduledTaskStore.
// Ports CircleAI.Hosting.InMemoryScheduledTaskStore. All state is lost when the
// process exits.
type InMemoryScheduledTaskStore struct {
	mu    sync.RWMutex
	store map[string]CronJob
}

// NewInMemoryScheduledTaskStore builds an empty store.
func NewInMemoryScheduledTaskStore() *InMemoryScheduledTaskStore {
	return &InMemoryScheduledTaskStore{store: make(map[string]CronJob)}
}

// List returns every registered job.
func (s *InMemoryScheduledTaskStore) List(_ context.Context) ([]CronJob, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]CronJob, 0, len(s.store))
	for _, j := range s.store {
		out = append(out, j)
	}
	return out, nil
}

// Get returns the job with the given id, or nil.
func (s *InMemoryScheduledTaskStore) Get(_ context.Context, id string) (*CronJob, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if j, ok := s.store[id]; ok {
		cp := j
		return &cp, nil
	}
	return nil, nil
}

// Upsert inserts or replaces the job.
func (s *InMemoryScheduledTaskStore) Upsert(_ context.Context, job CronJob) (CronJob, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.store[job.ID] = job
	return job, nil
}

// Delete removes the job with the given id.
func (s *InMemoryScheduledTaskStore) Delete(_ context.Context, id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.store, id)
	return nil
}

// GetDueJobs returns all enabled jobs whose NextRunUTC is <= now.
func (s *InMemoryScheduledTaskStore) GetDueJobs(_ context.Context) ([]CronJob, error) {
	now := time.Now().UTC()
	s.mu.RLock()
	defer s.mu.RUnlock()
	var due []CronJob
	for _, j := range s.store {
		if j.IsEnabled && j.NextRunUTC != nil && !j.NextRunUTC.After(now) {
			due = append(due, j)
		}
	}
	return due, nil
}

var _ IScheduledTaskStore = (*InMemoryScheduledTaskStore)(nil)

// ---------------------------------------------------------------------------
// ScheduledAIService
// ---------------------------------------------------------------------------

// JobCompletedEventArgs is the event data emitted when a scheduled job finishes.
// Ports CircleAI.Hosting.JobCompletedEventArgs. Err is non-nil on failure and
// Response is empty in that case.
type JobCompletedEventArgs struct {
	Job      CronJob
	Response string
	Err      error
}

// scheduledPollInterval mirrors ScheduledAIService.PollInterval (30 s).
const scheduledPollInterval = 30 * time.Second

// ScheduledAIService runs a background loop that polls an IScheduledTaskStore
// for due CronJob records, executes them via IAIService.Ask, and invokes an
// OnJobCompleted callback. Ports CircleAI.Hosting.ScheduledAIService.
//
// Delivery routing (push/email/Telegram) is left to the host via OnJobCompleted
// so the SDK carries no platform notification dependency.
type ScheduledAIService struct {
	butler IAIService
	store  IScheduledTaskStore

	// OnJobCompleted is invoked on the poll goroutine whenever a job completes
	// (success or failure). Set before StartAsync. Subscriber panics/errors are
	// isolated and never crash the loop.
	OnJobCompleted func(JobCompletedEventArgs)

	// pollInterval is configurable for tests; defaults to scheduledPollInterval.
	pollInterval time.Duration

	mu     sync.Mutex
	cancel context.CancelFunc
	done   chan struct{}
}

// NewScheduledAIService constructs the service. Call Start to begin polling.
func NewScheduledAIService(butler IAIService, store IScheduledTaskStore) *ScheduledAIService {
	return &ScheduledAIService{
		butler:       butler,
		store:        store,
		pollInterval: scheduledPollInterval,
	}
}

// SetPollInterval overrides the poll cadence (used by tests). No-op while running.
func (s *ScheduledAIService) SetPollInterval(d time.Duration) {
	if d > 0 {
		s.pollInterval = d
	}
}

// Start begins the background polling loop. Calling this while already running
// is a no-op. Ports ScheduledAIService.StartAsync.
func (s *ScheduledAIService) Start() {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.cancel != nil {
		return
	}
	ctx, cancel := context.WithCancel(context.Background())
	s.cancel = cancel
	s.done = make(chan struct{})
	go s.runLoop(ctx, s.done)
}

// Stop signals the polling loop to stop and waits for it to exit. Ports
// ScheduledAIService.StopAsync.
func (s *ScheduledAIService) Stop() {
	s.mu.Lock()
	cancel := s.cancel
	done := s.done
	s.cancel = nil
	s.done = nil
	s.mu.Unlock()

	if cancel == nil {
		return
	}
	cancel()
	<-done
}

func (s *ScheduledAIService) runLoop(ctx context.Context, done chan struct{}) {
	defer close(done)
	timer := time.NewTimer(s.pollInterval)
	defer timer.Stop()
	for {
		s.processDueJobs(ctx)
		timer.Reset(s.pollInterval)
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
		}
	}
}

func (s *ScheduledAIService) processDueJobs(ctx context.Context) {
	dueJobs, err := s.store.GetDueJobs(ctx)
	if err != nil || len(dueJobs) == 0 {
		return
	}
	for _, job := range dueJobs {
		if ctx.Err() != nil {
			return
		}
		s.executeJob(ctx, job)
	}
}

// ExecuteJobNow runs a single job immediately and returns the completion args.
// Exposed for deterministic testing (mirrors the loop's per-job body). Ports
// ScheduledAIService.ExecuteJobAsync.
func (s *ScheduledAIService) ExecuteJobNow(ctx context.Context, job CronJob) JobCompletedEventArgs {
	return s.executeJob(ctx, job)
}

func (s *ScheduledAIService) executeJob(ctx context.Context, job CronJob) JobCompletedEventArgs {
	now := time.Now().UTC()

	// Mark as Running.
	running := job
	running.State = CronJobRunning
	_, _ = s.store.Upsert(ctx, running)

	var response string
	var jobErr error

	resp, err := s.butler.Ask(ctx, job.Prompt)
	if err != nil {
		if ctx.Err() != nil {
			// Cancellation is not a failure — restore previous state.
			restored := job
			restored.State = CronJobPending
			_, _ = s.store.Upsert(context.Background(), restored)
			return JobCompletedEventArgs{Job: restored, Err: ctx.Err()}
		}
		jobErr = err
	} else {
		response = resp
	}

	nextRun := computeNextRun(job.CronExpression, now)
	updatedState := CronJobSucceeded
	if jobErr != nil {
		updatedState = CronJobFailed
	}

	updated := job
	updated.LastRunUTC = &now
	updated.NextRunUTC = nextRun
	updated.State = updatedState
	_, _ = s.store.Upsert(context.Background(), updated)

	args := JobCompletedEventArgs{Job: updated, Response: response, Err: jobErr}
	if s.OnJobCompleted != nil {
		func() {
			defer func() { _ = recover() }() // subscriber errors are non-fatal
			s.OnJobCompleted(args)
		}()
	}
	return args
}

// computeNextRun returns the next occurrence, or nil when the expression can't
// be parsed. Ports ScheduledAIService.ComputeNextRun.
func computeNextRun(cronExpression string, after time.Time) *time.Time {
	next, err := GetNextCronOccurrence(cronExpression, after)
	if err != nil {
		return nil
	}
	return &next
}
