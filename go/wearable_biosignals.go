// wearable_biosignals.go
//
// Ports the CircleAI.Wearable.Biosignals module:
//   BiosignalKind (enum, explicit 0..8)          -> int consts, stable ordinals
//   BiosignalSample (record + Create factory)     -> value struct + constructor
//   IBiosignalSource (streaming contract)         -> BiosignalSource interface
//   NullBiosignalSource / RecordedBiosignalSource -> in-memory impls
//   BiosignalStats / BiosignalSnapshot (records)  -> value structs
//   BiosignalAggregator (sliding-window snapshot) -> struct
//   BiosignalAffectMapper (static rule sheet)     -> ApplyBiosignalToAffect
//
// Go mapping of the C# async surface:
//   IAsyncEnumerable<BiosignalSample> StreamAsync(ct)
//                            -> Stream(ctx) (<-chan BiosignalSample)
//   Task<bool> IsSupportedAsync(kind, ct)
//                            -> IsSupported(kind) bool  (the C# body is
//                               synchronous; the ct is unused there)
//   float (C#)               -> float32 (byte-identical rule thresholds)
//   Guid                     -> uuid.UUID
//
// CONCURRENCY: RecordedBiosignalSource.Stream creates the output channel and
// starts the producer goroutine; the goroutine closes the channel on completion
// or ctx cancellation, so a synchronous range over the channel terminates
// cleanly. BiosignalAggregator.Snapshot derives a window-bounded context so a
// never-completing source still yields a snapshot when the window elapses.
//
// The mapper mutates the shared AffectState (float32 fields), reusing the
// package clamp32 helper so results match the C# Math.Clamp(_, 0, 1) exactly.

package circleai

import (
	"context"
	"errors"
	"math"
	"time"

	"github.com/google/uuid"
)

// BiosignalKind is the canonical taxonomy of biosignals. Ports the BiosignalKind
// enum; integer values are explicit and stable (do not renumber).
type BiosignalKind int

const (
	// BiosignalKindHeartRate is heart rate, beats per minute.
	BiosignalKindHeartRate BiosignalKind = 0
	// BiosignalKindHeartRateVariability is HRV, RMSSD in milliseconds.
	BiosignalKindHeartRateVariability BiosignalKind = 1
	// BiosignalKindOxygenSaturation is SpO2, percent (0-100).
	BiosignalKindOxygenSaturation BiosignalKind = 2
	// BiosignalKindAccelerometer is accelerometer magnitude, m/s^2.
	BiosignalKindAccelerometer BiosignalKind = 3
	// BiosignalKindBodyTemperature is body temperature, degrees Celsius.
	BiosignalKindBodyTemperature BiosignalKind = 4
	// BiosignalKindSleepStage is sleep stage (0=awake,1=light,2=deep,3=REM).
	BiosignalKindSleepStage BiosignalKind = 5
	// BiosignalKindSteps is step count (cumulative or delta per IsCumulative).
	BiosignalKindSteps BiosignalKind = 6
	// BiosignalKindGalvanicSkinResponse is GSR, microsiemens.
	BiosignalKindGalvanicSkinResponse BiosignalKind = 7
	// BiosignalKindUnknown is a catch-all for vendor-specific/future signals.
	BiosignalKindUnknown BiosignalKind = 8
)

// BiosignalSample is a single biosignal measurement. Ports the BiosignalSample
// record. Value and Confidence are float32 (C# float).
type BiosignalSample struct {
	Id           uuid.UUID
	Kind         BiosignalKind
	Value        float32
	Unit         string
	Confidence   float32
	IsCumulative bool
	MeasuredAt   time.Time
}

// NewBiosignalSample creates a fresh sample with a new UUID, current UTC time,
// and confidence clamped to [0,1]. Ports BiosignalSample.Create (confidence
// defaults to 1.0, isCumulative to false at the C# call sites).
func NewBiosignalSample(kind BiosignalKind, value float32, unit string, confidence float32, isCumulative bool) BiosignalSample {
	return BiosignalSample{
		Id:           uuid.New(),
		Kind:         kind,
		Value:        value,
		Unit:         unit,
		Confidence:   clamp32(confidence, 0, 1),
		IsCumulative: isCumulative,
		MeasuredAt:   time.Now().UTC(),
	}
}

// BiosignalSource is a streaming source of biosignal samples. Ports
// IBiosignalSource.
type BiosignalSource interface {
	// SupportedKinds lists the kinds this source can emit (may be empty).
	SupportedKinds() []BiosignalKind
	// Stream emits samples until ctx is cancelled or the source is exhausted. The
	// returned channel is closed when streaming ends.
	Stream(ctx context.Context) <-chan BiosignalSample
	// IsSupported reports whether the source can produce the given kind.
	IsSupported(kind BiosignalKind) bool
}

// NullBiosignalSource supports nothing and emits nothing. Ports
// NullBiosignalSource ("no wearable connected").
type NullBiosignalSource struct{}

// SupportedKinds returns an empty slice. Ports SupportedKinds.
func (NullBiosignalSource) SupportedKinds() []BiosignalKind { return []BiosignalKind{} }

// IsSupported always returns false. Ports IsSupportedAsync.
func (NullBiosignalSource) IsSupported(kind BiosignalKind) bool { return false }

// Stream returns an immediately-closed channel. Ports StreamAsync (yield break).
func (NullBiosignalSource) Stream(ctx context.Context) <-chan BiosignalSample {
	ch := make(chan BiosignalSample)
	close(ch)
	return ch
}

// RecordedBiosignalSource replays a fixed list of samples. Ports
// RecordedBiosignalSource.
type RecordedBiosignalSource struct {
	samples     []BiosignalSample
	kinds       []BiosignalKind
	replayDelay time.Duration
}

// NewRecordedBiosignalSource constructs a replay source. Ports the constructor
// (nil samples -> error; replayDelay defaults to zero). SupportedKinds are the
// distinct kinds present in samples.
func NewRecordedBiosignalSource(samples []BiosignalSample, replayDelay time.Duration) (*RecordedBiosignalSource, error) {
	if samples == nil {
		return nil, errors.New("samples required")
	}
	seen := make(map[BiosignalKind]struct{})
	kinds := make([]BiosignalKind, 0)
	for _, s := range samples {
		if _, ok := seen[s.Kind]; !ok {
			seen[s.Kind] = struct{}{}
			kinds = append(kinds, s.Kind)
		}
	}
	cp := make([]BiosignalSample, len(samples))
	copy(cp, samples)
	return &RecordedBiosignalSource{samples: cp, kinds: kinds, replayDelay: replayDelay}, nil
}

// SupportedKinds lists the distinct kinds in the recording. Ports SupportedKinds.
func (s *RecordedBiosignalSource) SupportedKinds() []BiosignalKind {
	out := make([]BiosignalKind, len(s.kinds))
	copy(out, s.kinds)
	return out
}

// IsSupported reports whether the given kind appears in the recording. Ports
// IsSupportedAsync.
func (s *RecordedBiosignalSource) IsSupported(kind BiosignalKind) bool {
	for _, k := range s.kinds {
		if k == kind {
			return true
		}
	}
	return false
}

// Stream replays the recorded samples, honouring the optional replay delay and
// ctx cancellation. Ports StreamAsync.
func (s *RecordedBiosignalSource) Stream(ctx context.Context) <-chan BiosignalSample {
	ch := make(chan BiosignalSample)
	go func() {
		defer close(ch)
		for _, sample := range s.samples {
			if ctx.Err() != nil {
				return
			}
			if s.replayDelay > 0 {
				select {
				case <-ctx.Done():
					return
				case <-time.After(s.replayDelay):
				}
			}
			select {
			case <-ctx.Done():
				return
			case ch <- sample:
			}
		}
	}()
	return ch
}

// BiosignalStats is per-kind aggregate statistics over a window. Ports the
// BiosignalStats record.
type BiosignalStats struct {
	SampleCount int
	Min         float32
	Max         float32
	Mean        float32
}

// BiosignalSnapshot is a snapshot of aggregates across observed kinds. Ports the
// BiosignalSnapshot record. Kinds with no in-window samples are absent from
// Stats.
type BiosignalSnapshot struct {
	Stats       map[BiosignalKind]BiosignalStats
	GeneratedAt time.Time
}

// BiosignalAggregator computes sliding-window aggregates over a BiosignalSource.
// Ports BiosignalAggregator.
type BiosignalAggregator struct {
	source BiosignalSource
}

// NewBiosignalAggregator wraps the given source. Ports the constructor (nil
// source -> error).
func NewBiosignalAggregator(source BiosignalSource) (*BiosignalAggregator, error) {
	if source == nil {
		return nil, errors.New("source required")
	}
	return &BiosignalAggregator{source: source}, nil
}

// biosignalAccumulator accumulates count/min/max/sum for one kind.
type biosignalAccumulator struct {
	count int
	min   float32
	max   float32
	sum   float64
}

func newBiosignalAccumulator() *biosignalAccumulator {
	return &biosignalAccumulator{min: float32(math.Inf(1)), max: float32(math.Inf(-1))}
}

func (a *biosignalAccumulator) add(v float32) {
	a.count++
	if v < a.min {
		a.min = v
	}
	if v > a.max {
		a.max = v
	}
	a.sum += float64(v)
}

func (a *biosignalAccumulator) toStats() BiosignalStats {
	mean := float32(0)
	if a.count != 0 {
		mean = float32(a.sum / float64(a.count))
	}
	return BiosignalStats{SampleCount: a.count, Min: a.min, Max: a.max, Mean: mean}
}

// Snapshot consumes samples until the source completes or the window elapses,
// then returns aggregates over the in-window samples. Ports SnapshotAsync
// (window <= 0 -> error). Single-shot, not continuous.
func (agg *BiosignalAggregator) Snapshot(ctx context.Context, window time.Duration) (BiosignalSnapshot, error) {
	if window <= 0 {
		return BiosignalSnapshot{}, errors.New("Window must be positive.")
	}
	generatedAt := time.Now().UTC()
	cutoff := generatedAt.Add(-window)
	deadline := generatedAt.Add(window)

	// Time-bound the read so a never-completing source still yields a snapshot.
	wctx, cancel := context.WithTimeout(ctx, window)
	defer cancel()

	acc := make(map[BiosignalKind]*biosignalAccumulator)
	for sample := range agg.source.Stream(wctx) {
		if sample.MeasuredAt.Before(cutoff) {
			continue
		}
		a, ok := acc[sample.Kind]
		if !ok {
			a = newBiosignalAccumulator()
			acc[sample.Kind] = a
		}
		a.add(sample.Value)
		if !time.Now().UTC().Before(deadline) {
			break
		}
	}

	stats := make(map[BiosignalKind]BiosignalStats, len(acc))
	for kind, a := range acc {
		stats[kind] = a.toStats()
	}
	return BiosignalSnapshot{Stats: stats, GeneratedAt: generatedAt}, nil
}

// biosignalMinConfidence is the confidence gate below which samples never mutate
// affect. Ports BiosignalAffectMapper.MinConfidence.
const biosignalMinConfidence float32 = 0.5

// ApplyBiosignalToAffect applies the deterministic rule for sample to affect,
// mutating affect in place. Ports BiosignalAffectMapper.Apply. All mutated
// fields are clamped to [0,1]. Low-confidence samples are ignored.
//
// Rule sheet (clamped to [0,1]):
//   HeartRate  > 130 bpm: Energy += 0.10, Uncertainty += 0.05
//   HeartRate  > 100 bpm: Energy += 0.05
//   HeartRate  <  50 bpm: Energy -= 0.05
//   HRV        <  20 ms:  Uncertainty += 0.05, Rapport -= 0.02
//   HRV        >  60 ms:  Engagement += 0.02
//   SpO2       <  90 %:   Uncertainty += 0.10
//   SleepStage / others:  no mutation
func ApplyBiosignalToAffect(sample BiosignalSample, affect *AffectState) {
	if affect == nil {
		return
	}
	if sample.Confidence < biosignalMinConfidence {
		return
	}
	switch sample.Kind {
	case BiosignalKindHeartRate:
		applyHeartRate(sample.Value, affect)
	case BiosignalKindHeartRateVariability:
		applyHrv(sample.Value, affect)
	case BiosignalKindOxygenSaturation:
		applySpO2(sample.Value, affect)
	default:
		// SleepStage and the remaining kinds do not currently drive affect.
	}
	affect.LastUpdatedUTC = time.Now().UTC()
}

func applyHeartRate(bpm float32, a *AffectState) {
	switch {
	case bpm > 130:
		a.Energy = clamp32(a.Energy+0.10, 0, 1)
		a.Uncertainty = clamp32(a.Uncertainty+0.05, 0, 1)
	case bpm > 100:
		a.Energy = clamp32(a.Energy+0.05, 0, 1)
	case bpm < 50:
		a.Energy = clamp32(a.Energy-0.05, 0, 1)
	}
}

func applyHrv(rmssdMs float32, a *AffectState) {
	switch {
	case rmssdMs < 20:
		a.Uncertainty = clamp32(a.Uncertainty+0.05, 0, 1)
		a.Rapport = clamp32(a.Rapport-0.02, 0, 1)
	case rmssdMs > 60:
		a.Engagement = clamp32(a.Engagement+0.02, 0, 1)
	}
}

func applySpO2(percent float32, a *AffectState) {
	if percent < 90 {
		a.Uncertainty = clamp32(a.Uncertainty+0.10, 0, 1)
	}
}

// Interface guards.
var (
	_ BiosignalSource = NullBiosignalSource{}
	_ BiosignalSource = (*RecordedBiosignalSource)(nil)
)
