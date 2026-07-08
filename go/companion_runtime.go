// companion_runtime.go
//
// Ports CircleAI.Memory.Runtime.CompanionRuntime (CompanionRuntime.cs) and
// CircleAI.Memory.Runtime.CompanionRuntimeOptions (CompanionRuntimeOptions.cs).
//
// The host orchestrator that ticks the consolidator on a schedule, keeps the
// sync engine running, and exposes a single ingestion entry point for
// multimodal artefacts. The C# reference implements IHostedService; here Start
// launches background goroutines and Stop cancels + joins them.

package circleai

import (
	"context"
	"errors"
	"sync"
	"time"
)

// RuntimeLogger is the minimal logging seam CompanionRuntime uses. The default
// (nil) logger is a no-op, mirroring the C# NullLogger.
type RuntimeLogger interface {
	// Info logs an informational message with optional key/value args.
	Info(msg string, args ...any)
	// Warn logs a warning, typically with an error, with optional key/value args.
	Warn(err error, msg string, args ...any)
}

// nopRuntimeLogger discards all log output.
type nopRuntimeLogger struct{}

func (nopRuntimeLogger) Info(string, ...any)        {}
func (nopRuntimeLogger) Warn(error, string, ...any) {}

// CompanionRuntimeOptions configures CompanionRuntime. All fields have sensible
// defaults via NewCompanionRuntimeOptions so a host gets a working pipeline.
type CompanionRuntimeOptions struct {
	// DailyTickInterval is the cadence for the daily-tier consolidation pass.
	// Default: every 6 hours. Zero disables automatic daily ticks.
	DailyTickInterval time.Duration
	// WeeklyTickInterval is the cadence for the weekly-tier pass. Default: 24h.
	WeeklyTickInterval time.Duration
	// MonthlyTickInterval is the cadence for the monthly (persona-delta) pass.
	// Default: 48h.
	MonthlyTickInterval time.Duration
	// SyncBroadcastInterval is the cadence at which the runtime broadcasts its
	// sync state vector to peers. Default: 5m. Zero disables periodic sync
	// (the engine still responds to inbound envelopes).
	SyncBroadcastInterval time.Duration
	// InitialDelay is the delay before the first consolidator tick after Start.
	// Default: 30s. Keeps startup quiet.
	InitialDelay time.Duration
	// CatchUpOnStart runs an OnDemand consolidation pass during Start to catch
	// up anything pending before the timer cadence kicks in. Default: true.
	CatchUpOnStart bool
}

// NewCompanionRuntimeOptions returns options populated with the C# defaults.
func NewCompanionRuntimeOptions() CompanionRuntimeOptions {
	return CompanionRuntimeOptions{
		DailyTickInterval:     6 * time.Hour,
		WeeklyTickInterval:    24 * time.Hour,
		MonthlyTickInterval:   48 * time.Hour,
		SyncBroadcastInterval: 5 * time.Minute,
		InitialDelay:          30 * time.Second,
		CatchUpOnStart:        true,
	}
}

// CompanionRuntime owns the lifecycle of the memory pipeline (consolidator, sync
// engine, multimodal ingester) and ticks consolidation passes on a schedule.
type CompanionRuntime struct {
	consolidator IMemoryConsolidator
	syncEngine   ICompanionStateSyncEngine // optional; nil when text-only host
	ingester     *MultimodalMemoryIngester // optional; nil for text-only host
	options      CompanionRuntimeOptions
	logger       RuntimeLogger

	mu       sync.Mutex
	stopCtx  context.Context
	stopFn   context.CancelFunc
	wg       sync.WaitGroup
	started  bool
	disposed bool
}

// CompanionRuntimeDeps holds the optional collaborators for a CompanionRuntime.
// Any of SyncEngine, Ingester, Options, and Logger may be zero.
type CompanionRuntimeDeps struct {
	// SyncEngine is the optional companion-state sync engine.
	SyncEngine ICompanionStateSyncEngine
	// Ingester is the optional multimodal memory ingester.
	Ingester *MultimodalMemoryIngester
	// Options tunes the schedule; nil uses NewCompanionRuntimeOptions.
	Options *CompanionRuntimeOptions
	// Logger receives lifecycle logs; nil discards them.
	Logger RuntimeLogger
}

// NewCompanionRuntime wires a runtime over a required consolidator plus optional
// deps. Returns an error when consolidator is nil.
func NewCompanionRuntime(consolidator IMemoryConsolidator, deps CompanionRuntimeDeps) (*CompanionRuntime, error) {
	if consolidator == nil {
		return nil, errors.New("consolidator required")
	}
	opts := NewCompanionRuntimeOptions()
	if deps.Options != nil {
		opts = *deps.Options
	}
	var logger RuntimeLogger = nopRuntimeLogger{}
	if deps.Logger != nil {
		logger = deps.Logger
	}
	return &CompanionRuntime{
		consolidator: consolidator,
		syncEngine:   deps.SyncEngine,
		ingester:     deps.Ingester,
		options:      opts,
		logger:       logger,
	}, nil
}

// Start starts the sync engine (if wired), optionally catches up consolidation,
// and launches the periodic consolidation + sync-broadcast loops.
func (r *CompanionRuntime) Start(ctx context.Context) error {
	r.mu.Lock()
	if r.disposed {
		r.mu.Unlock()
		return errors.New("runtime disposed")
	}
	if r.started {
		r.mu.Unlock()
		return nil
	}
	r.started = true
	r.stopCtx, r.stopFn = context.WithCancel(context.Background())
	stopCtx := r.stopCtx
	r.mu.Unlock()

	r.logger.Info("CompanionRuntime starting.")

	if r.syncEngine != nil {
		if err := r.syncEngine.Start(ctx); err != nil {
			return err
		}
		r.logger.Info("Sync engine started.")
	}

	if r.options.CatchUpOnStart {
		outcome, err := r.consolidator.Tick(ctx, SleepOnDemand)
		if err != nil {
			r.logger.Warn(err, "Catch-up consolidation failed (non-fatal).")
		} else {
			r.logger.Info("Catch-up consolidation.",
				"daily", outcome.DailySummariesProduced,
				"weekly", outcome.SemanticClustersProduced,
				"monthly", outcome.PersonaDeltasProduced,
				"core", outcome.CorePromotions)
		}
	}

	if r.options.DailyTickInterval > 0 {
		r.launchPeriodic(SleepDaily, r.options.DailyTickInterval, stopCtx)
	}
	if r.options.WeeklyTickInterval > 0 {
		r.launchPeriodic(SleepWeekly, r.options.WeeklyTickInterval, stopCtx)
	}
	if r.options.MonthlyTickInterval > 0 {
		r.launchPeriodic(SleepMonthly, r.options.MonthlyTickInterval, stopCtx)
	}
	if r.syncEngine != nil && r.options.SyncBroadcastInterval > 0 {
		r.launchSyncBroadcasts(r.options.SyncBroadcastInterval, stopCtx)
	}

	r.logger.Info("CompanionRuntime started.")
	return nil
}

// Stop cancels the background loops, waits for them to finish, and disposes the
// sync engine. Idempotent.
func (r *CompanionRuntime) Stop(_ context.Context) error {
	r.mu.Lock()
	if !r.started {
		r.mu.Unlock()
		return nil
	}
	stopFn := r.stopFn
	r.mu.Unlock()

	r.logger.Info("CompanionRuntime stopping.")
	if stopFn != nil {
		stopFn()
	}
	r.wg.Wait()

	r.mu.Lock()
	r.started = false
	r.mu.Unlock()

	if r.syncEngine != nil {
		if err := r.syncEngine.Close(); err != nil {
			r.logger.Warn(err, "Sync engine close failed.")
		}
	}

	r.logger.Info("CompanionRuntime stopped.")
	return nil
}

// Close stops the runtime. Idempotent.
func (r *CompanionRuntime) Close() error {
	r.mu.Lock()
	if r.disposed {
		r.mu.Unlock()
		return nil
	}
	r.disposed = true
	r.mu.Unlock()
	return r.Stop(context.Background())
}

// ConsolidateNow triggers an OnDemand consolidation pass. Hosts call this after
// large chunks of new activity when they don't want to wait for the timer.
func (r *CompanionRuntime) ConsolidateNow(ctx context.Context) (ConsolidationOutcome, error) {
	return r.consolidator.Tick(ctx, SleepOnDemand)
}

// IngestMedia forwards multimodal ingestion to the registered ingester. Returns
// an error when no ingester was wired (the runtime can run text-only).
func (r *CompanionRuntime) IngestMedia(ctx context.Context, modality MediaModality, sourceBytes []byte, options IngestOptions) (IngestionResult, error) {
	if r.ingester == nil {
		return IngestionResult{}, errors.New("CompanionRuntime was constructed without a MultimodalMemoryIngester")
	}
	return r.ingester.Ingest(ctx, modality, sourceBytes, options)
}

// SyncNow forces an immediate sync broadcast. No-op when sync isn't wired.
func (r *CompanionRuntime) SyncNow(ctx context.Context) error {
	if r.syncEngine == nil {
		return nil
	}
	return r.syncEngine.SyncNow(ctx)
}

// ── Internals ─────────────────────────────────────────────────────────────────

func (r *CompanionRuntime) launchPeriodic(kind SleepKind, interval time.Duration, ctx context.Context) {
	r.wg.Add(1)
	go func() {
		defer r.wg.Done()
		if !runtimeSleep(ctx, r.options.InitialDelay) {
			return
		}
		for ctx.Err() == nil {
			outcome, err := r.consolidator.Tick(ctx, kind)
			if err != nil {
				if ctx.Err() != nil {
					return
				}
				r.logger.Warn(err, "Consolidation tick failed.", "kind", kind.String())
			} else if outcome.DailySummariesProduced+outcome.SemanticClustersProduced+
				outcome.PersonaDeltasProduced+outcome.CorePromotions > 0 {
				r.logger.Info("Consolidation tick.",
					"kind", kind.String(),
					"daily", outcome.DailySummariesProduced,
					"weekly", outcome.SemanticClustersProduced,
					"monthly", outcome.PersonaDeltasProduced,
					"core", outcome.CorePromotions)
			}
			if !runtimeSleep(ctx, interval) {
				return
			}
		}
	}()
}

func (r *CompanionRuntime) launchSyncBroadcasts(interval time.Duration, ctx context.Context) {
	r.wg.Add(1)
	go func() {
		defer r.wg.Done()
		if !runtimeSleep(ctx, r.options.InitialDelay) {
			return
		}
		for ctx.Err() == nil {
			if err := r.syncEngine.SyncNow(ctx); err != nil {
				if ctx.Err() != nil {
					return
				}
				r.logger.Warn(err, "Sync broadcast failed.")
			}
			if !runtimeSleep(ctx, interval) {
				return
			}
		}
	}()
}

// runtimeSleep sleeps for d or until ctx is cancelled. Returns false when the
// context was cancelled (caller should stop looping).
func runtimeSleep(ctx context.Context, d time.Duration) bool {
	if d <= 0 {
		return ctx.Err() == nil
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-t.C:
		return true
	}
}
