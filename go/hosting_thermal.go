// hosting_thermal.go
//
// Ports CircleAI.Hosting thermal throttling:
//   ThermalState (IThermalThrottleService.cs)
//   IThermalThrottleService (IThermalThrottleService.cs)
//   ThermalThrottleService (ThermalThrottleService.cs)
//
// The C# implementation samples an OS-specific thermal API behind a per-platform
// #if. Go injects that sampling as a ThermalSampler func so the service is
// portable and deterministic in tests; the polling loop, state-change eventing,
// and ShouldPauseInference threshold match the C# exactly.

package circleai

import (
	"context"
	"sync"
	"sync/atomic"
	"time"
)

// ThermalState is a coarse thermal level, ordered coolest→hottest so numeric
// comparisons (>= ThermalSerious) are meaningful. Ports
// CircleAI.Hosting.ThermalState (stable ordinals).
type ThermalState int32

const (
	// ThermalUnknown — state could not be determined.
	ThermalUnknown ThermalState = 0
	// ThermalNormal — within normal operating temperature.
	ThermalNormal ThermalState = 1
	// ThermalFair — slightly warm; performance may be lightly throttled.
	ThermalFair ThermalState = 2
	// ThermalSerious — hot; OS may have begun throttling.
	ThermalSerious ThermalState = 3
	// ThermalCritical — critically hot; aggressive throttling imminent.
	ThermalCritical ThermalState = 4
)

// thermalPollInterval mirrors ThermalThrottleService.PollInterval (10 s).
const thermalPollInterval = 10 * time.Second

// ThermalSampler returns the current device thermal state. It stands in for the
// C# per-platform SamplePlatform(). It must not block for long. A sampler that
// panics is treated as ThermalUnknown (mirrors the C# catch → Unknown).
type ThermalSampler func() ThermalState

// IThermalThrottleService polls platform thermal APIs and exposes the current
// state. Ports CircleAI.Hosting.IThermalThrottleService.
type IThermalThrottleService interface {
	// CurrentState is the most-recently sampled thermal state.
	CurrentState() ThermalState
	// ShouldPauseInference is true when CurrentState >= ThermalSerious.
	ShouldPauseInference() bool
	// SetOnStateChanged registers the callback fired on every state change.
	SetOnStateChanged(handler func(ThermalState))
	// StartMonitoring starts the background poll loop. Idempotent.
	StartMonitoring(ctx context.Context)
	// StopMonitoring stops the poll loop, retaining the current state.
	StopMonitoring()
}

// ThermalThrottleService is the cross-platform thermal poller. Ports
// CircleAI.Hosting.ThermalThrottleService. StateChanged fires whenever a sample
// differs from the previously observed state.
type ThermalThrottleService struct {
	sampler ThermalSampler

	currentState atomic.Int32 // ThermalState

	mu      sync.Mutex
	cancel  context.CancelFunc
	done    chan struct{}
	running bool
	handler func(ThermalState)
}

// NewThermalThrottleService builds a service driven by sampler. A nil sampler
// defaults to one that always reports ThermalUnknown.
func NewThermalThrottleService(sampler ThermalSampler) *ThermalThrottleService {
	if sampler == nil {
		sampler = func() ThermalState { return ThermalUnknown }
	}
	s := &ThermalThrottleService{sampler: sampler}
	s.currentState.Store(int32(ThermalUnknown))
	return s
}

// CurrentState returns the most-recent sampled state.
func (s *ThermalThrottleService) CurrentState() ThermalState {
	return ThermalState(s.currentState.Load())
}

// ShouldPauseInference reports whether the device is at Serious or Critical.
func (s *ThermalThrottleService) ShouldPauseInference() bool {
	return s.CurrentState() >= ThermalSerious
}

// SetOnStateChanged registers the state-change callback.
func (s *ThermalThrottleService) SetOnStateChanged(handler func(ThermalState)) {
	s.mu.Lock()
	s.handler = handler
	s.mu.Unlock()
}

// StartMonitoring starts the poll loop. Only one loop runs at a time.
func (s *ThermalThrottleService) StartMonitoring(parent context.Context) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.running {
		return
	}
	if parent == nil {
		parent = context.Background()
	}
	ctx, cancel := context.WithCancel(parent)
	s.cancel = cancel
	s.done = make(chan struct{})
	s.running = true
	go s.pollLoop(ctx, s.done)
}

// StopMonitoring stops the loop. The current state is retained.
func (s *ThermalThrottleService) StopMonitoring() {
	s.mu.Lock()
	cancel := s.cancel
	done := s.done
	s.cancel = nil
	s.done = nil
	s.running = false
	s.mu.Unlock()

	if cancel == nil {
		return
	}
	cancel()
	<-done
}

func (s *ThermalThrottleService) pollLoop(ctx context.Context, done chan struct{}) {
	defer close(done)

	// Sample immediately so callers get a valid state before the first tick.
	s.applyNewState(s.sampleSafe())

	ticker := time.NewTicker(thermalPollInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			s.applyNewState(s.sampleSafe())
		}
	}
}

// sampleSafe calls the sampler, mapping a panic to ThermalUnknown.
func (s *ThermalThrottleService) sampleSafe() (state ThermalState) {
	defer func() {
		if recover() != nil {
			state = ThermalUnknown
		}
	}()
	return s.sampler()
}

func (s *ThermalThrottleService) applyNewState(newState ThermalState) {
	previous := ThermalState(s.currentState.Swap(int32(newState)))
	if previous == newState {
		return
	}
	s.mu.Lock()
	handler := s.handler
	s.mu.Unlock()
	if handler != nil {
		func() {
			defer func() { _ = recover() }() // handler errors are non-fatal
			handler(newState)
		}()
	}
}

var _ IThermalThrottleService = (*ThermalThrottleService)(nil)
