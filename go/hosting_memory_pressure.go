// hosting_memory_pressure.go
//
// Ports CircleAI.Hosting.IMemoryPressureSource.cs (RT-04):
//   MemoryPressureLevel, IMemoryPressureSource,
//   NullMemoryPressureSource, ManualMemoryPressureSource.
//
// A platform-published memory-pressure signal. AIService listens and triggers a
// brownout swap when the level reaches Critical. Handlers receive (old, new) and
// are invoked on the caller's goroutine; the source isolates handler errors.

package circleai

import (
	"context"
	"sync"
)

// MemoryPressureLevel is a coarse memory-pressure level mirroring Android's
// onTrimMemory contract and iOS memory warnings. Ports
// CircleAI.Hosting.MemoryPressureLevel (stable ordinals).
type MemoryPressureLevel int

const (
	// MemoryPressureNormal — plenty of headroom; no action.
	MemoryPressureNormal MemoryPressureLevel = 0
	// MemoryPressureTrim — OS asked apps to release optional caches.
	MemoryPressureTrim MemoryPressureLevel = 1
	// MemoryPressureCritical — OS is about to kill the process; drop everything.
	MemoryPressureCritical MemoryPressureLevel = 2
)

// MemoryPressureHandler receives (oldLevel, newLevel) on a pressure transition.
type MemoryPressureHandler func(ctx context.Context, oldLevel, newLevel MemoryPressureLevel) error

// IMemoryPressureSource is a platform-published memory-pressure signal. Ports
// CircleAI.Hosting.IMemoryPressureSource.
type IMemoryPressureSource interface {
	// Current is the pressure level as last observed.
	Current() MemoryPressureLevel
	// Subscribe registers a transition handler and returns an unsubscribe func.
	Subscribe(handler MemoryPressureHandler) func()
}

// NullMemoryPressureSource always reports Normal and never raises events. Ports
// CircleAI.Hosting.NullMemoryPressureSource. Used when no platform source is
// registered — CircleAI keeps working, brownout simply never fires.
type NullMemoryPressureSource struct{}

// NullMemoryPressureSourceInstance is the shared singleton (mirrors the C#
// static Instance).
var NullMemoryPressureSourceInstance = NullMemoryPressureSource{}

// Current always returns MemoryPressureNormal.
func (NullMemoryPressureSource) Current() MemoryPressureLevel { return MemoryPressureNormal }

// Subscribe is a no-op that returns an unsubscribe func doing nothing.
func (NullMemoryPressureSource) Subscribe(MemoryPressureHandler) func() { return func() {} }

// ManualMemoryPressureSource is a manually-driven IMemoryPressureSource. Hosts
// (or tests) construct one and call Raise on a platform pressure event. Ports
// CircleAI.Hosting.ManualMemoryPressureSource. Thread-safe.
type ManualMemoryPressureSource struct {
	mu       sync.Mutex
	current  MemoryPressureLevel
	handlers []*MemoryPressureHandler
}

// NewManualMemoryPressureSource constructs a source at Normal pressure.
func NewManualMemoryPressureSource() *ManualMemoryPressureSource {
	return &ManualMemoryPressureSource{current: MemoryPressureNormal}
}

// Current returns the last observed level.
func (m *ManualMemoryPressureSource) Current() MemoryPressureLevel {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.current
}

// Subscribe registers a transition handler; the returned func unsubscribes.
func (m *ManualMemoryPressureSource) Subscribe(handler MemoryPressureHandler) func() {
	if handler == nil {
		return func() {}
	}
	m.mu.Lock()
	h := &handler
	m.handlers = append(m.handlers, h)
	m.mu.Unlock()
	return func() {
		m.mu.Lock()
		defer m.mu.Unlock()
		for i, existing := range m.handlers {
			if existing == h {
				m.handlers = append(m.handlers[:i], m.handlers[i+1:]...)
				break
			}
		}
	}
}

// Raise publishes a new pressure level. Idempotent for the same level — only
// transitions fire handlers. Ports ManualMemoryPressureSource.Raise. Handler
// errors are isolated so a pressure handler can never break the source.
func (m *ManualMemoryPressureSource) Raise(ctx context.Context, level MemoryPressureLevel) error {
	m.mu.Lock()
	if m.current == level {
		m.mu.Unlock()
		return nil
	}
	previous := m.current
	m.current = level
	snapshot := make([]*MemoryPressureHandler, len(m.handlers))
	copy(snapshot, m.handlers)
	m.mu.Unlock()

	for _, h := range snapshot {
		if err := ctx.Err(); err != nil {
			return err
		}
		func() {
			defer func() { _ = recover() }()
			_ = (*h)(ctx, previous, level)
		}()
	}
	return nil
}

var (
	_ IMemoryPressureSource = NullMemoryPressureSource{}
	_ IMemoryPressureSource = (*ManualMemoryPressureSource)(nil)
)
