// hosting_worker.go
//
// Ports CircleAI.Hosting.BackgroundInferenceWorker.cs.
//
// A host-lifecycle adapter that starts/stops an IAIService and, when an
// IThermalThrottleService is wired, pauses inference (IsPaused) while the device
// is at Serious/Critical thermal state. Callers check IsPaused before submitting
// work. The .NET IHostedService shape maps to explicit Start/Stop here.

package circleai

import (
	"context"
	"sync"
	"sync/atomic"
)

// BackgroundInferenceWorker wraps an IAIService in a host lifecycle and honours
// thermal throttling. Ports CircleAI.Hosting.BackgroundInferenceWorker.
type BackgroundInferenceWorker struct {
	butler  IAIService
	thermal IThermalThrottleService

	paused  atomic.Bool
	stopped atomic.Int32 // 0 = running, 1 = stopped

	mu          sync.Mutex
	monitorCtx  context.Context
	monitorStop context.CancelFunc
	subscribed  bool
}

// NewBackgroundInferenceWorker builds the worker. thermal may be nil, in which
// case thermal monitoring is skipped and IsPaused is always false.
func NewBackgroundInferenceWorker(butler IAIService, thermal IThermalThrottleService) *BackgroundInferenceWorker {
	return &BackgroundInferenceWorker{butler: butler, thermal: thermal}
}

// IsPaused reports whether the device is in a thermally-throttled state
// (Serious/Critical). Callers that queue inference should check this first.
func (w *BackgroundInferenceWorker) IsPaused() bool { return w.paused.Load() }

// Start starts the butler (model load + optional warm-up) and, when a thermal
// service is available, begins monitoring. Ports BackgroundInferenceWorker.StartAsync.
func (w *BackgroundInferenceWorker) Start(ctx context.Context) error {
	if w.thermal != nil {
		w.thermal.SetOnStateChanged(w.onThermalStateChanged)
		w.mu.Lock()
		mctx, cancel := context.WithCancel(context.Background())
		w.monitorCtx = mctx
		w.monitorStop = cancel
		w.subscribed = true
		w.mu.Unlock()
		w.thermal.StartMonitoring(mctx)
		// Seed the initial paused state from the current thermal reading.
		w.onThermalStateChanged(w.thermal.CurrentState())
	}
	return w.butler.Start(ctx)
}

// Stop stops the butler and thermal monitoring in order. Safe to call multiple
// times — subsequent calls are no-ops. Ports BackgroundInferenceWorker.StopAsync.
func (w *BackgroundInferenceWorker) Stop(ctx context.Context) error {
	if !w.stopped.CompareAndSwap(0, 1) {
		return nil
	}
	if w.thermal != nil {
		w.thermal.StopMonitoring()
		w.mu.Lock()
		if w.monitorStop != nil {
			w.monitorStop()
			w.monitorStop = nil
		}
		w.subscribed = false
		w.mu.Unlock()
	}
	return w.butler.Stop(ctx)
}

func (w *BackgroundInferenceWorker) onThermalStateChanged(newState ThermalState) {
	shouldPause := newState >= ThermalSerious
	if shouldPause && !w.paused.Load() {
		w.paused.Store(true)
	} else if !shouldPause && w.paused.Load() {
		w.paused.Store(false)
	}
}
