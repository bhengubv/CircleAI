// hosting_warmup.go
//
// Ports CircleAI.Hosting.Warmup (RT-07 predictive warmup):
//   ArrivalForecast, IRequestPredictor (IRequestPredictor.cs)
//   HistogramRequestPredictor (HistogramRequestPredictor.cs)
//   PredictiveWarmupOptions (PredictiveWarmupOptions.cs)
//   PredictiveWarmupController (PredictiveWarmupController.cs)
//
// The predictor keeps a per-minute-of-day arrival-rate histogram (EWMA over a
// rolling N-day window) and forecasts P(>=1 arrival) via the Poisson tail. The
// controller polls it and pre-warms the generator before a predicted spike. All
// local-only — no telemetry.

package circleai

import (
	"context"
	"math"
	"sync"
	"sync/atomic"
	"time"
)

// ArrivalForecast is a forecast of inbound requests over a window. Ports
// CircleAI.Hosting.Warmup.ArrivalForecast (readonly record struct).
type ArrivalForecast struct {
	// ProbabilityOfArrival is P(>=1 arrival) in [0,1].
	ProbabilityOfArrival float64
	// ExpectedCount is the best estimate of how many arrivals to expect.
	ExpectedCount float64
	// Confidence is [0,1]; cold-start histograms return ~0.
	Confidence float64
}

// IRequestPredictor learns request arrival timing and forecasts spikes. Ports
// CircleAI.Hosting.Warmup.IRequestPredictor.
type IRequestPredictor interface {
	// RecordArrival records one arrival at utc.
	RecordArrival(utc time.Time)
	// Predict forecasts arrivals in forecastWindow starting at utcNow.
	Predict(utcNow time.Time, forecastWindow time.Duration) ArrivalForecast
	// ObservedArrivals is the total arrivals observed since construction.
	ObservedArrivals() int64
}

const (
	histMinutesPerDay               = 24 * 60
	histWarmConfidence              = 1.0
	histMinSamplesForFullConfidence = 25
)

// HistogramRequestPredictor is the default IRequestPredictor. Ports
// CircleAI.Hosting.Warmup.HistogramRequestPredictor. Thread-safe.
type HistogramRequestPredictor struct {
	historyDays    int
	mu             sync.Mutex
	perMinuteRate  [histMinutesPerDay]float64
	perMinuteCount [histMinutesPerDay]int
	observed       atomic.Int64
}

// NewHistogramRequestPredictor builds a predictor with a rolling history of
// historyDays days. A non-positive value defaults to 7 (one calendar week).
func NewHistogramRequestPredictor(historyDays int) *HistogramRequestPredictor {
	if historyDays <= 0 {
		historyDays = 7
	}
	return &HistogramRequestPredictor{historyDays: historyDays}
}

// ObservedArrivals returns the total arrivals observed.
func (p *HistogramRequestPredictor) ObservedArrivals() int64 { return p.observed.Load() }

// RecordArrival records one arrival, updating the EWMA rate for its
// minute-of-day slot. Ports HistogramRequestPredictor.RecordArrival.
func (p *HistogramRequestPredictor) RecordArrival(utc time.Time) {
	u := utc.UTC()
	minute := u.Hour()*60 + u.Minute()
	p.mu.Lock()
	p.perMinuteCount[minute]++
	cnt := p.perMinuteCount[minute]
	// EWMA: alpha shrinks as cnt grows (capped at historyDays), so early
	// samples dominate less.
	m := cnt
	if p.historyDays < m {
		m = p.historyDays
	}
	alpha := 2.0 / (float64(m) + 1)
	p.perMinuteRate[minute] = alpha*1.0 + (1-alpha)*p.perMinuteRate[minute]
	p.mu.Unlock()
	p.observed.Add(1)
}

// Predict forecasts arrivals in forecastWindow. Ports
// HistogramRequestPredictor.Predict: expected = sum of per-minute rates over the
// covered slots; probability = 1 - exp(-expected); confidence rises with samples.
func (p *HistogramRequestPredictor) Predict(utcNow time.Time, forecastWindow time.Duration) ArrivalForecast {
	if forecastWindow <= 0 {
		return ArrivalForecast{}
	}
	if p.ObservedArrivals() == 0 {
		return ArrivalForecast{}
	}
	u := utcNow.UTC()
	minute := u.Hour()*60 + u.Minute()
	minutes := int(math.Ceil(forecastWindow.Minutes()))
	if minutes < 1 {
		minutes = 1
	}

	var expected float64
	var coveredSamples int
	p.mu.Lock()
	for i := 0; i < minutes; i++ {
		idx := (minute + i) % histMinutesPerDay
		expected += p.perMinuteRate[idx]
		coveredSamples += p.perMinuteCount[idx]
	}
	p.mu.Unlock()

	probability := 1.0 - math.Exp(-expected)
	confidence := math.Min(histWarmConfidence,
		float64(coveredSamples)/float64(histMinSamplesForFullConfidence*minutes))
	return ArrivalForecast{ProbabilityOfArrival: probability, ExpectedCount: expected, Confidence: confidence}
}

// ResetForTests wipes all histogram state. Test-only. Ports
// HistogramRequestPredictor.ResetForTests.
func (p *HistogramRequestPredictor) ResetForTests() {
	p.mu.Lock()
	p.perMinuteRate = [histMinutesPerDay]float64{}
	p.perMinuteCount = [histMinutesPerDay]int{}
	p.mu.Unlock()
	p.observed.Store(0)
}

var _ IRequestPredictor = (*HistogramRequestPredictor)(nil)

// ---------------------------------------------------------------------------
// PredictiveWarmupController
// ---------------------------------------------------------------------------

// PredictiveWarmupOptions configures PredictiveWarmupController. Ports
// CircleAI.Hosting.Warmup.PredictiveWarmupOptions (with the same defaults).
type PredictiveWarmupOptions struct {
	// Enabled: when false (default), the controller does not pre-warm.
	Enabled bool
	// PollInterval: how often the controller asks the predictor. Default 30 s.
	PollInterval time.Duration
	// ForecastWindow: how far ahead to forecast. Default 60 s.
	ForecastWindow time.Duration
	// WarmupThreshold: pre-warm when prob*confidence >= this. Default 0.5.
	WarmupThreshold float64
	// MinTimeBetweenWarmups: minimum delay between pre-warms. Default 5 min.
	MinTimeBetweenWarmups time.Duration
}

// DefaultPredictiveWarmupOptions returns the C# defaults.
func DefaultPredictiveWarmupOptions() PredictiveWarmupOptions {
	return PredictiveWarmupOptions{
		Enabled:               false,
		PollInterval:          30 * time.Second,
		ForecastWindow:        60 * time.Second,
		WarmupThreshold:       0.5,
		MinTimeBetweenWarmups: 5 * time.Minute,
	}
}

// PredictiveWarmupController is an async background loop that polls an
// IRequestPredictor and pre-warms an IAIService before predicted spikes. Ports
// CircleAI.Hosting.Warmup.PredictiveWarmupController.
type PredictiveWarmupController struct {
	service   IAIService
	predictor IRequestPredictor
	options   PredictiveWarmupOptions
	clock     func() time.Time

	mu         sync.Mutex
	cancel     context.CancelFunc
	done       chan struct{}
	lastWarmup time.Time
	running    bool
}

// NewPredictiveWarmupController constructs the controller. A nil clock defaults
// to time.Now().UTC.
func NewPredictiveWarmupController(
	service IAIService,
	predictor IRequestPredictor,
	options PredictiveWarmupOptions,
	clock func() time.Time,
) *PredictiveWarmupController {
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &PredictiveWarmupController{
		service:    service,
		predictor:  predictor,
		options:    options,
		clock:      clock,
		lastWarmup: time.Time{},
	}
}

// Start begins polling on a background loop. No-op when Enabled is false or the
// loop is already running. Ports PredictiveWarmupController.StartAsync.
func (c *PredictiveWarmupController) Start(parent context.Context) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if !c.options.Enabled || c.running {
		return
	}
	if parent == nil {
		parent = context.Background()
	}
	ctx, cancel := context.WithCancel(parent)
	c.cancel = cancel
	c.done = make(chan struct{})
	c.running = true
	go c.runLoop(ctx, c.done)
}

// Stop cancels the loop and waits for it to exit. Ports
// PredictiveWarmupController.DisposeAsync.
func (c *PredictiveWarmupController) Stop() {
	c.mu.Lock()
	cancel := c.cancel
	done := c.done
	c.cancel = nil
	c.done = nil
	c.running = false
	c.mu.Unlock()
	if cancel == nil {
		return
	}
	cancel()
	<-done
}

// NotifyArrival records a request arrival on the underlying predictor at "now".
// Ports PredictiveWarmupController.NotifyArrival.
func (c *PredictiveWarmupController) NotifyArrival() {
	c.predictor.RecordArrival(c.clock())
}

// Tick runs one prediction + decide-and-maybe-warm cycle. Returns true when
// warmup was triggered. Ports PredictiveWarmupController.TickAsync.
func (c *PredictiveWarmupController) Tick(ctx context.Context) (bool, error) {
	now := c.clock()
	forecast := c.predictor.Predict(now, c.options.ForecastWindow)
	score := forecast.ProbabilityOfArrival * forecast.Confidence
	if score < c.options.WarmupThreshold {
		return false, nil
	}

	c.mu.Lock()
	last := c.lastWarmup
	c.mu.Unlock()
	if now.Sub(last) < c.options.MinTimeBetweenWarmups {
		return false, nil
	}

	c.mu.Lock()
	c.lastWarmup = now
	c.mu.Unlock()

	if err := c.service.Prewarm(ctx); err != nil {
		if ctx.Err() != nil {
			return false, err
		}
		return false, nil // warmup failure is logged in C#, non-fatal
	}
	return true, nil
}

func (c *PredictiveWarmupController) runLoop(ctx context.Context, done chan struct{}) {
	defer close(done)
	ticker := time.NewTicker(c.options.PollInterval)
	defer ticker.Stop()
	// C# do/while: tick once immediately, then on each interval.
	_, _ = c.Tick(ctx)
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			_, _ = c.Tick(ctx)
		}
	}
}
