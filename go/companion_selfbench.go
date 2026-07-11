// companion_selfbench.go
//
// Ported from CircleAI.Companion (SelfBenchSelfImprovementLoop.cs) — the C#
// reference. Implements ISelfImprovementLoop by orchestrating CircleAI.SelfBench:
// run the named suite against a baseline AIService, A/B compare against a
// host-supplied candidate, and only "apply" the candidate when the regression
// gate passes.
//
// CircleAI.SelfBench is not (yet) ported to this Go tree, so the minimal SelfBench
// surface this loop touches is modelled here as injected interfaces + records
// (BenchSuiteProvider, AbBenchComparer, AbVerdict, BenchSummary,
// RegressionGateConfig). This is the same "inject the external dependency behind
// an interface" pattern the C# uses for the host-supplied factories and promote
// callback — the loop's own logic (registry lookup → compare → gate → promote →
// best-score tracking) is ported faithfully. A default in-memory comparer keeps
// the loop runnable standalone; a host swaps in the real SelfBench A/B runner.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"
)

// BenchSummary mirrors the SelfBench BenchSummary aggregate. Ported from the C#
// record BenchSummary. The full SelfBench runner (selfbench.go) populates every
// field; the Companion self-improvement loop and the A/B gate read the score and
// latency fields.
type BenchSummary struct {
	RunID          string
	SuiteID        string
	TaskCount      int
	PassCount      int
	MeanScore      float64
	P50LatencyMs   float64
	P95LatencyMs   float64
	PerTaskScore   map[string]float64
	CompletedAtUtc time.Time
}

// RegressionGateConfig mirrors the SelfBench RegressionGateConfig. Ported from
// the C# record RegressionGateConfig(double MinMeanScoreImprovement=0.01,
// double MaxP95LatencyRegressionMs=250, int MaxCriticalRegressions=0).
type RegressionGateConfig struct {
	MinMeanScoreImprovement   float64
	MaxP95LatencyRegressionMs float64
	MaxCriticalRegressions    int
}

// DefaultRegressionGateConfig returns the C# default gate.
func DefaultRegressionGateConfig() RegressionGateConfig {
	return RegressionGateConfig{
		MinMeanScoreImprovement:   0.01,
		MaxP95LatencyRegressionMs: 250.0,
		MaxCriticalRegressions:    0,
	}
}

// AbVerdict mirrors the SelfBench A/B verdict. Ported from the C# record
// AbVerdict(bool ShouldPromote, BenchSummary BaselineSummary,
// BenchSummary CandidateSummary, double MeanScoreDelta, double P95LatencyDeltaMs,
// IReadOnlyList<string> CriticalRegressions, string Reason).
type AbVerdict struct {
	ShouldPromote       bool
	BaselineSummary     BenchSummary
	CandidateSummary    BenchSummary
	MeanScoreDelta      float64
	P95LatencyDeltaMs   float64
	CriticalRegressions []string
	Reason              string
}

// SelfBenchTask is the minimal bench-task shape the loop passes through to the
// comparer. Ported from the SelfBench BenchTask (only Id + IsCritical are read
// by the A/B gate that the loop drives).
type SelfBenchTask struct {
	ID         string
	Suite      string
	Prompt     string
	Expected   string
	IsCritical bool
}

// BenchSuiteProvider supplies the tasks for a suite id. Modelled on the C#
// BenchSuiteRegistry.Get. Returns an empty slice for an unknown suite.
type BenchSuiteProvider interface {
	Get(suiteID string) []SelfBenchTask
}

// SelfBenchAIService is the candidate/baseline model handle the comparer runs
// the suite against. Modelled on the C# IAIService dependency — opaque to the
// loop, which only forwards it to the comparer.
type SelfBenchAIService interface {
	// ServiceID identifies the model variant (e.g. "baseline", "lora-v3").
	ServiceID() string
}

// AbBenchComparer runs a suite against a baseline and a candidate and returns a
// gated verdict. Modelled on the C# AbBenchRunner.CompareAsync.
type AbBenchComparer interface {
	Compare(ctx context.Context, suiteID string, tasks []SelfBenchTask,
		baseline, candidate SelfBenchAIService, gate RegressionGateConfig) (AbVerdict, error)
}

// SelfBenchSelfImprovementLoop implements ISelfImprovementLoop over a SelfBench
// A/B comparer. Ported from the C# SelfBenchSelfImprovementLoop.
type SelfBenchSelfImprovementLoop struct {
	provider         BenchSuiteProvider
	comparer         AbBenchComparer
	baselineFactory  func(ctx context.Context) (SelfBenchAIService, error)
	candidateFactory func(ctx context.Context) (SelfBenchAIService, error)
	onPromote        func(ctx context.Context, verdict AbVerdict) error
	gate             RegressionGateConfig

	mu         sync.Mutex
	bestScores map[string]float64
}

// NewSelfBenchSelfImprovementLoop wires the loop. provider, comparer,
// baselineFactory and candidateFactory are required. onPromote defaults to a
// no-op; a zero gate defaults to DefaultRegressionGateConfig.
func NewSelfBenchSelfImprovementLoop(
	provider BenchSuiteProvider,
	comparer AbBenchComparer,
	baselineFactory func(ctx context.Context) (SelfBenchAIService, error),
	candidateFactory func(ctx context.Context) (SelfBenchAIService, error),
	onPromote func(ctx context.Context, verdict AbVerdict) error,
	gate *RegressionGateConfig,
) (*SelfBenchSelfImprovementLoop, error) {
	if provider == nil {
		return nil, errors.New("provider required")
	}
	if comparer == nil {
		return nil, errors.New("comparer required")
	}
	if baselineFactory == nil {
		return nil, errors.New("baselineFactory required")
	}
	if candidateFactory == nil {
		return nil, errors.New("candidateFactory required")
	}
	if onPromote == nil {
		onPromote = func(context.Context, AbVerdict) error { return nil }
	}
	g := DefaultRegressionGateConfig()
	if gate != nil {
		g = *gate
	}
	return &SelfBenchSelfImprovementLoop{
		provider:         provider,
		comparer:         comparer,
		baselineFactory:  baselineFactory,
		candidateFactory: candidateFactory,
		onPromote:        onPromote,
		gate:             g,
		bestScores:       make(map[string]float64),
	}, nil
}

// Cycle runs the suite A/B and promotes the candidate when the gate passes.
// Mirrors the C# CycleAsync: empty suite id → "default"; no tasks → skipped;
// promote → onPromote + record best; else → rejected.
func (l *SelfBenchSelfImprovementLoop) Cycle(ctx context.Context, benchSuiteID string) (SelfImprovementVerdict, error) {
	if benchSuiteID == "" {
		benchSuiteID = "default"
	}
	tasks := l.provider.Get(benchSuiteID)
	if len(tasks) == 0 {
		return SelfImprovementVerdict{ImprovementsApplied: "skipped: no tasks in suite", NewBenchScore: 0.0}, nil
	}

	baseline, err := l.baselineFactory(ctx)
	if err != nil {
		return SelfImprovementVerdict{}, err
	}
	candidate, err := l.candidateFactory(ctx)
	if err != nil {
		return SelfImprovementVerdict{}, err
	}

	verdict, err := l.comparer.Compare(ctx, benchSuiteID, tasks, baseline, candidate, l.gate)
	if err != nil {
		return SelfImprovementVerdict{}, err
	}

	newScore := verdict.CandidateSummary.MeanScore
	var applied string
	if verdict.ShouldPromote {
		if err := l.onPromote(ctx, verdict); err != nil {
			return SelfImprovementVerdict{}, err
		}
		l.mu.Lock()
		if prev, ok := l.bestScores[benchSuiteID]; !ok || newScore > prev {
			l.bestScores[benchSuiteID] = newScore
		}
		l.mu.Unlock()
		applied = fmt.Sprintf("promoted candidate (%s)", verdict.Reason)
	} else {
		applied = fmt.Sprintf("rejected (%s)", verdict.Reason)
	}
	return SelfImprovementVerdict{ImprovementsApplied: applied, NewBenchScore: newScore}, nil
}

// BestScoreFor returns the best promoted score for benchSuiteID (0 if none).
func (l *SelfBenchSelfImprovementLoop) BestScoreFor(benchSuiteID string) float64 {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.bestScores[benchSuiteID]
}

var _ ISelfImprovementLoop = (*SelfBenchSelfImprovementLoop)(nil)

// ---------------------------------------------------------------------------
// Deterministic in-memory SelfBench surface (default runnable implementations)
// ---------------------------------------------------------------------------

// InMemoryBenchSuiteProvider is a map-backed BenchSuiteProvider. Modelled on the
// C# BenchSuiteRegistry (register + Get), without the JSON-file loader.
type InMemoryBenchSuiteProvider struct {
	mu     sync.Mutex
	suites map[string][]SelfBenchTask
}

// NewInMemoryBenchSuiteProvider returns an empty provider.
func NewInMemoryBenchSuiteProvider() *InMemoryBenchSuiteProvider {
	return &InMemoryBenchSuiteProvider{suites: make(map[string][]SelfBenchTask)}
}

// Register registers tasks under suiteID.
func (p *InMemoryBenchSuiteProvider) Register(suiteID string, tasks []SelfBenchTask) error {
	if suiteID == "" {
		return errors.New("suiteId required")
	}
	p.mu.Lock()
	p.suites[suiteID] = tasks
	p.mu.Unlock()
	return nil
}

// Get returns the tasks for suiteID, or an empty slice.
func (p *InMemoryBenchSuiteProvider) Get(suiteID string) []SelfBenchTask {
	p.mu.Lock()
	defer p.mu.Unlock()
	if s, ok := p.suites[suiteID]; ok {
		return s
	}
	return []SelfBenchTask{}
}

var _ BenchSuiteProvider = (*InMemoryBenchSuiteProvider)(nil)

// NamedAIService is a trivial SelfBenchAIService identified by name, carrying a
// per-task score map used by ScoredAbComparer to build deterministic summaries.
// This stands in for a real model handle — a host injects an IAIService-backed
// handle instead.
type NamedAIService struct {
	Name string
	// Scores maps task id -> score in [0,1] this variant achieves.
	Scores map[string]float64
	// P95 is the p95 latency (ms) this variant exhibits.
	P95 float64
}

// ServiceID returns the variant name.
func (s NamedAIService) ServiceID() string { return s.Name }

var _ SelfBenchAIService = NamedAIService{}

// ScoredAbComparer builds each side's BenchSummary from a NamedAIService's score
// map and applies the exact C# regression-gate logic (mean-improvement,
// p95-regression, critical-regression count). This is a real, deterministic
// comparer — not a stub — that a host replaces with the SelfBench A/B runner.
type ScoredAbComparer struct{}

// Compare runs the gate over the two variants' declared scores.
func (ScoredAbComparer) Compare(_ context.Context, suiteID string, tasks []SelfBenchTask,
	baseline, candidate SelfBenchAIService, gate RegressionGateConfig) (AbVerdict, error) {

	base := summariseNamed(suiteID+"@baseline", suiteID, tasks, baseline)
	cand := summariseNamed(suiteID+"@candidate", suiteID, tasks, candidate)

	meanDelta := cand.MeanScore - base.MeanScore
	p95Delta := cand.P95LatencyMs - base.P95LatencyMs

	var criticalReg []string
	for _, t := range tasks {
		if !t.IsCritical {
			continue
		}
		baseScore := base.PerTaskScore[t.ID]
		candScore := cand.PerTaskScore[t.ID]
		if candScore < baseScore-1e-9 {
			criticalReg = append(criticalReg, t.ID)
		}
	}

	promote := meanDelta >= gate.MinMeanScoreImprovement &&
		p95Delta <= gate.MaxP95LatencyRegressionMs &&
		len(criticalReg) <= gate.MaxCriticalRegressions

	var reason string
	if promote {
		reason = fmt.Sprintf("+%.3f mean, p95 Δ %.0fms, %d critical regressions", meanDelta, p95Delta, len(criticalReg))
	} else {
		reason = buildRejectionReason(meanDelta, p95Delta, criticalReg, gate)
	}

	return AbVerdict{
		ShouldPromote:       promote,
		BaselineSummary:     base,
		CandidateSummary:    cand,
		MeanScoreDelta:      meanDelta,
		P95LatencyDeltaMs:   p95Delta,
		CriticalRegressions: criticalReg,
		Reason:              reason,
	}, nil
}

var _ AbBenchComparer = ScoredAbComparer{}

func summariseNamed(runID, suiteID string, tasks []SelfBenchTask, svc SelfBenchAIService) BenchSummary {
	named, _ := svc.(NamedAIService)
	perTask := make(map[string]float64, len(tasks))
	passCount := 0
	var sum float64
	for _, t := range tasks {
		score := named.Scores[t.ID]
		perTask[t.ID] = score
		sum += score
		if score >= 0.5 {
			passCount++
		}
	}
	mean := 0.0
	if len(tasks) > 0 {
		mean = sum / float64(len(tasks))
	}
	return BenchSummary{
		RunID:        runID,
		SuiteID:      suiteID,
		TaskCount:    len(tasks),
		PassCount:    passCount,
		MeanScore:    mean,
		P50LatencyMs: named.P95,
		P95LatencyMs: named.P95,
		PerTaskScore: perTask,
	}
}

// buildRejectionReason reproduces the C# BuildRejectionReason.
func buildRejectionReason(meanDelta, p95Delta float64, criticals []string, gate RegressionGateConfig) string {
	var reasons []string
	if meanDelta < gate.MinMeanScoreImprovement {
		reasons = append(reasons, fmt.Sprintf("mean score Δ %.3f below threshold %.3f", meanDelta, gate.MinMeanScoreImprovement))
	}
	if p95Delta > gate.MaxP95LatencyRegressionMs {
		reasons = append(reasons, fmt.Sprintf("p95 latency regression %.0fms > %.0fms", p95Delta, gate.MaxP95LatencyRegressionMs))
	}
	if len(criticals) > gate.MaxCriticalRegressions {
		reasons = append(reasons, fmt.Sprintf("%d critical regressions: %s", len(criticals), joinComma(criticals)))
	}
	if len(reasons) == 0 {
		return "rejected"
	}
	return joinSemi(reasons)
}

func joinComma(xs []string) string { return joinWith(xs, ",") }
func joinSemi(xs []string) string  { return joinWith(xs, "; ") }

func joinWith(xs []string, sep string) string {
	switch len(xs) {
	case 0:
		return ""
	case 1:
		return xs[0]
	}
	n := len(sep) * (len(xs) - 1)
	for _, x := range xs {
		n += len(x)
	}
	var b []byte
	b = make([]byte, 0, n)
	for i, x := range xs {
		if i > 0 {
			b = append(b, sep...)
		}
		b = append(b, x...)
	}
	return string(b)
}
