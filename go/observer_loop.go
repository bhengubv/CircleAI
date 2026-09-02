// observer_loop.go
//
// Ports CircleAI.Observer:
//   SensorReading / ISensor                      (Contracts.cs)
//   ObservationTool / IObservationToolbox        (Contracts.cs)
//   ObservationTick / IObservationLoop           (Contracts.cs)
//   SensorRecorder / ObserverDecision            (InMemoryObserver.cs)
//   InMemoryObservationLoop                      (InMemoryObserver.cs)
//   NullSensor / InMemoryObservationToolbox / NullObservationLoop (NullImplementations.cs)
//
// The perceive-reason-act loop ticks at a configured interval, snapshots the
// latest reading from each registered sensor, asks a host reason func for a
// decision, runs the named tools, then fans the tick out to subscribers.
//
// CONCURRENCY (per the task's rules):
//   - subscriber list is snapshotted UNDER the lock, then invoked OUTSIDE it,
//     so a subscriber's handler (or an unsubscribe from within it) never
//     deadlocks against the fan-out.
//   - SensorRecorder subscribes to its sensor synchronously at construction,
//     before the loop goroutine starts, so no reading is missed by a race.
//   - the returned unsubscribe closure is idempotent.

package circleai

import (
	"context"
	"errors"
	"sync"
	"time"
)

// SensorReading is one snapshot from one sensor. Ports the SensorReading
// record. Payload is []byte (nil == none), modelling ReadOnlyMemory<byte>?.
type SensorReading struct {
	SensorID      string
	Kind          string
	CapturedAtUTC time.Time
	Values        map[string]string
	Payload       []byte
}

// ISensor is a single perception source. Ports ISensor. Subscribe returns an
// idempotent unsubscribe func (the C# IDisposable).
type ISensor interface {
	SensorID() string
	Kind() string
	BackendID() string
	Start(ctx context.Context) error
	Stop(ctx context.Context) error
	Subscribe(handler func(SensorReading)) (unsubscribe func())
}

// ObservationTool is one tool the observer can invoke during its act tick.
// Ports ObservationTool. Invoke returns the tool's result string.
type ObservationTool struct {
	ToolID      string
	Description string
	Tags        []string
	Invoke      func(ctx context.Context, args map[string]string) (string, error)
}

// IObservationToolbox is the registry of tools. Ports IObservationToolbox.
type IObservationToolbox interface {
	BackendID() string
	RegisterTool(tool ObservationTool)
	TryGet(toolID string) (ObservationTool, bool)
	ListTools() []ObservationTool
}

// ObservationTick is one loop tick record. Ports ObservationTick.
type ObservationTick struct {
	AtUTC        time.Time
	Perceived    []SensorReading
	Reasoning    string
	ToolsInvoked []string
}

// IObservationLoop is the perceive-reason-act loop. Ports IObservationLoop.
type IObservationLoop interface {
	BackendID() string
	Start(ctx context.Context, tickInterval time.Duration) error
	Stop(ctx context.Context) error
	Subscribe(handler func(ObservationTick)) (unsubscribe func())
	Dispose()
}

// ObserverDecision is the shape returned by the reasoner. Ports
// ObserverDecision. ToolArgs nil == empty args.
type ObserverDecision struct {
	Reasoning     string
	ToolsToInvoke []string
	ToolArgs      map[string]string
}

// ObserverReasonFunc is the host-supplied reasoner: given the latest readings,
// it returns a decision. Ports the Func<IReadOnlyList<SensorReading>, CT,
// ValueTask<ObserverDecision>> delegate.
type ObserverReasonFunc func(ctx context.Context, readings []SensorReading) (ObserverDecision, error)

// ---------------------------------------------------------------------------
// SensorRecorder
// ---------------------------------------------------------------------------

// SensorRecorder captures the latest reading from a sensor. Ports SensorRecorder.
type SensorRecorder struct {
	mu          sync.Mutex
	latest      *SensorReading
	unsubscribe func()
}

// NewSensorRecorder subscribes to sensor and records its latest reading. Ports
// the SensorRecorder ctor. Panics if sensor is nil.
func NewSensorRecorder(sensor ISensor) *SensorRecorder {
	if sensor == nil {
		panic("sensor must not be nil")
	}
	r := &SensorRecorder{}
	r.unsubscribe = sensor.Subscribe(func(reading SensorReading) {
		r.mu.Lock()
		rc := reading
		r.latest = &rc
		r.mu.Unlock()
	})
	return r
}

// Latest returns the most recent reading, or (zero,false). Ports Latest.
func (r *SensorRecorder) Latest() (SensorReading, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.latest == nil {
		return SensorReading{}, false
	}
	return *r.latest, true
}

// Dispose unsubscribes from the sensor. Ports Dispose.
func (r *SensorRecorder) Dispose() {
	if r.unsubscribe != nil {
		r.unsubscribe()
	}
}

// ---------------------------------------------------------------------------
// InMemoryObservationToolbox
// ---------------------------------------------------------------------------

// InMemoryObservationToolbox is a thread-safe tool registry. Ports
// InMemoryObservationToolbox.
type InMemoryObservationToolbox struct {
	mu    sync.Mutex
	tools map[string]ObservationTool
}

// NewInMemoryObservationToolbox constructs an empty toolbox.
func NewInMemoryObservationToolbox() *InMemoryObservationToolbox {
	return &InMemoryObservationToolbox{tools: make(map[string]ObservationTool)}
}

// BackendID returns "in-memory".
func (t *InMemoryObservationToolbox) BackendID() string { return "in-memory" }

// RegisterTool stores (or replaces by ToolId) a tool. Ports RegisterTool.
func (t *InMemoryObservationToolbox) RegisterTool(tool ObservationTool) {
	t.mu.Lock()
	t.tools[tool.ToolID] = tool
	t.mu.Unlock()
}

// TryGet returns the tool for toolID. Ports TryGet.
func (t *InMemoryObservationToolbox) TryGet(toolID string) (ObservationTool, bool) {
	t.mu.Lock()
	tool, ok := t.tools[toolID]
	t.mu.Unlock()
	return tool, ok
}

// ListTools returns a snapshot of registered tools. Ports ListTools.
func (t *InMemoryObservationToolbox) ListTools() []ObservationTool {
	t.mu.Lock()
	out := make([]ObservationTool, 0, len(t.tools))
	for _, v := range t.tools {
		out = append(out, v)
	}
	t.mu.Unlock()
	return out
}

var _ IObservationToolbox = (*InMemoryObservationToolbox)(nil)

// ---------------------------------------------------------------------------
// InMemoryObservationLoop
// ---------------------------------------------------------------------------

// InMemoryObservationLoop is the perceive-reason-act loop. Ports
// InMemoryObservationLoop.
type InMemoryObservationLoop struct {
	recorders []*SensorRecorder
	toolbox   IObservationToolbox
	reason    ObserverReasonFunc

	mu     sync.Mutex
	subs   []*observationSub
	cancel context.CancelFunc
	done   chan struct{}
}

type observationSub struct {
	handler func(ObservationTick)
}

// NewInMemoryObservationLoop constructs the loop over sensors, a toolbox, and a
// reasoner. Ports the ctor. Panics if toolbox or reason is nil.
func NewInMemoryObservationLoop(sensors []ISensor, toolbox IObservationToolbox, reason ObserverReasonFunc) *InMemoryObservationLoop {
	if toolbox == nil {
		panic("toolbox must not be nil")
	}
	if reason == nil {
		panic("reason must not be nil")
	}
	recorders := make([]*SensorRecorder, 0, len(sensors))
	for _, s := range sensors {
		recorders = append(recorders, NewSensorRecorder(s))
	}
	return &InMemoryObservationLoop{recorders: recorders, toolbox: toolbox, reason: reason}
}

// BackendID returns "in-memory".
func (l *InMemoryObservationLoop) BackendID() string { return "in-memory" }

// Start begins the tick loop. Ports StartAsync. Errors if already started.
func (l *InMemoryObservationLoop) Start(ctx context.Context, tickInterval time.Duration) error {
	l.mu.Lock()
	if l.cancel != nil {
		l.mu.Unlock()
		return errors.New("already started")
	}
	runCtx, cancel := context.WithCancel(ctx)
	l.cancel = cancel
	done := make(chan struct{})
	l.done = done
	l.mu.Unlock()

	go l.run(runCtx, tickInterval, done)
	return nil
}

// Stop cancels the loop and waits for it to exit. Ports StopAsync (idempotent).
func (l *InMemoryObservationLoop) Stop(ctx context.Context) error {
	l.mu.Lock()
	cancel := l.cancel
	done := l.done
	l.cancel = nil
	l.done = nil
	l.mu.Unlock()
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

// Subscribe registers handler and returns an idempotent unsubscribe. Ports
// Subscribe.
func (l *InMemoryObservationLoop) Subscribe(handler func(ObservationTick)) (unsubscribe func()) {
	if handler == nil {
		panic("handler must not be nil")
	}
	sub := &observationSub{handler: handler}
	l.mu.Lock()
	l.subs = append(l.subs, sub)
	l.mu.Unlock()
	var once sync.Once
	return func() { once.Do(func() { l.removeSub(sub) }) }
}

func (l *InMemoryObservationLoop) removeSub(sub *observationSub) {
	l.mu.Lock()
	for i, s := range l.subs {
		if s == sub {
			l.subs = append(l.subs[:i], l.subs[i+1:]...)
			break
		}
	}
	l.mu.Unlock()
}

// Dispose stops the loop and releases the recorders. Ports DisposeAsync.
func (l *InMemoryObservationLoop) Dispose() {
	_ = l.Stop(context.Background())
	for _, r := range l.recorders {
		r.Dispose()
	}
}

func (l *InMemoryObservationLoop) run(ctx context.Context, interval time.Duration, done chan struct{}) {
	defer close(done)
	for {
		if ctx.Err() != nil {
			return
		}
		l.tick(ctx)
		// Delay between ticks; exit promptly on cancellation.
		if interval <= 0 {
			interval = time.Millisecond
		}
		timer := time.NewTimer(interval)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
	}
}

func (l *InMemoryObservationLoop) tick(ctx context.Context) {
	// Perceive: snapshot latest readings.
	readings := make([]SensorReading, 0, len(l.recorders))
	for _, r := range l.recorders {
		if reading, ok := r.Latest(); ok {
			readings = append(readings, reading)
		}
	}

	// Reason: ask the host. A reasoner error skips this tick (mirrors the C#
	// catch-and-skip).
	decision, err := l.reason(ctx, readings)
	if err != nil {
		return
	}

	// Act: run each named tool, collecting the ids that ran.
	invoked := make([]string, 0, len(decision.ToolsToInvoke))
	args := decision.ToolArgs
	if args == nil {
		args = map[string]string{}
	}
	for _, toolID := range decision.ToolsToInvoke {
		tool, ok := l.toolbox.TryGet(toolID)
		if !ok || tool.Invoke == nil {
			continue
		}
		if _, terr := tool.Invoke(ctx, args); terr != nil {
			// Tool failure is logged-and-skipped in C#; swallow here.
			continue
		}
		invoked = append(invoked, toolID)
	}

	tick := ObservationTick{
		AtUTC:        time.Now().UTC(),
		Perceived:    readings,
		Reasoning:    decision.Reasoning,
		ToolsInvoked: invoked,
	}

	// Fan out: snapshot subscribers UNDER the lock, invoke OUTSIDE it.
	l.mu.Lock()
	snap := make([]*observationSub, len(l.subs))
	copy(snap, l.subs)
	l.mu.Unlock()
	for _, s := range snap {
		func() {
			defer func() { _ = recover() }()
			s.handler(tick)
		}()
	}
}

var _ IObservationLoop = (*InMemoryObservationLoop)(nil)

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullSensor is a fail-safe no-op sensor. Ports NullSensor.
type NullSensor struct{}

func (NullSensor) SensorID() string                                   { return "null" }
func (NullSensor) Kind() string                                       { return "null" }
func (NullSensor) BackendID() string                                  { return "null" }
func (NullSensor) Start(context.Context) error                        { return nil }
func (NullSensor) Stop(context.Context) error                         { return nil }
func (NullSensor) Subscribe(func(SensorReading)) (unsubscribe func()) { return func() {} }

// NullObservationLoop is a fail-safe no-op loop. Ports NullObservationLoop.
type NullObservationLoop struct{}

func (NullObservationLoop) BackendID() string                          { return "null" }
func (NullObservationLoop) Start(context.Context, time.Duration) error { return nil }
func (NullObservationLoop) Stop(context.Context) error                 { return nil }
func (NullObservationLoop) Subscribe(func(ObservationTick)) func() {
	return func() {}
}
func (NullObservationLoop) Dispose() {}

var (
	_ ISensor          = NullSensor{}
	_ IObservationLoop = NullObservationLoop{}
)
