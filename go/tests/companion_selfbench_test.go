// companion_selfbench_test.go
//
// Verifies SelfBenchSelfImprovementLoop (ported from
// SelfBenchSelfImprovementLoop.cs) and its deterministic in-memory SelfBench
// surface (InMemoryBenchSuiteProvider + ScoredAbComparer): empty suite → skip,
// gate-pass → promote (+ onPromote + best-score), gate-fail → reject, and the
// exact regression-gate arithmetic (mean improvement, p95 regression, critical
// regressions).

package circleai_test

import (
	"context"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func selfBenchLoop(t *testing.T, provider circleai.BenchSuiteProvider, baseline, candidate circleai.NamedAIService, gate *circleai.RegressionGateConfig, onPromote func(context.Context, circleai.AbVerdict) error) *circleai.SelfBenchSelfImprovementLoop {
	t.Helper()
	loop, err := circleai.NewSelfBenchSelfImprovementLoop(
		provider,
		circleai.ScoredAbComparer{},
		func(context.Context) (circleai.SelfBenchAIService, error) { return baseline, nil },
		func(context.Context) (circleai.SelfBenchAIService, error) { return candidate, nil },
		onPromote,
		gate,
	)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	return loop
}

func TestSelfBenchLoop_EmptySuiteSkips(t *testing.T) {
	ctx := context.Background()
	provider := circleai.NewInMemoryBenchSuiteProvider()
	loop := selfBenchLoop(t, provider,
		circleai.NamedAIService{Name: "base"},
		circleai.NamedAIService{Name: "cand"}, nil, nil)

	v, err := loop.Cycle(ctx, "missing")
	if err != nil {
		t.Fatalf("Cycle: %v", err)
	}
	if !strings.Contains(v.ImprovementsApplied, "skipped") || v.NewBenchScore != 0 {
		t.Errorf("empty suite verdict: %+v", v)
	}
}

func TestSelfBenchLoop_PromotesOnImprovement(t *testing.T) {
	ctx := context.Background()
	provider := circleai.NewInMemoryBenchSuiteProvider()
	_ = provider.Register("default", []circleai.SelfBenchTask{
		{ID: "t1", IsCritical: true},
		{ID: "t2"},
	})

	base := circleai.NamedAIService{Name: "base", Scores: map[string]float64{"t1": 0.5, "t2": 0.5}, P95: 100}
	cand := circleai.NamedAIService{Name: "cand", Scores: map[string]float64{"t1": 0.9, "t2": 0.9}, P95: 110}

	var promoted circleai.AbVerdict
	promotedCalled := 0
	loop := selfBenchLoop(t, provider, base, cand, nil, func(_ context.Context, v circleai.AbVerdict) error {
		promoted = v
		promotedCalled++
		return nil
	})

	v, err := loop.Cycle(ctx, "") // "" → "default"
	if err != nil {
		t.Fatalf("Cycle: %v", err)
	}
	if !strings.HasPrefix(v.ImprovementsApplied, "promoted candidate") {
		t.Errorf("should promote: %+v", v)
	}
	// Candidate mean = (0.9+0.9)/2 = 0.9.
	if v.NewBenchScore != 0.9 {
		t.Errorf("new score: got %v want 0.9", v.NewBenchScore)
	}
	if promotedCalled != 1 || !promoted.ShouldPromote {
		t.Errorf("onPromote not invoked with promote verdict: called=%d verdict=%+v", promotedCalled, promoted)
	}
	if loop.BestScoreFor("default") != 0.9 {
		t.Errorf("best score: got %v want 0.9", loop.BestScoreFor("default"))
	}
}

func TestSelfBenchLoop_RejectsOnNoImprovement(t *testing.T) {
	ctx := context.Background()
	provider := circleai.NewInMemoryBenchSuiteProvider()
	_ = provider.Register("default", []circleai.SelfBenchTask{{ID: "t1"}, {ID: "t2"}})

	// Candidate no better than baseline → mean delta 0 < 0.01 → reject.
	base := circleai.NamedAIService{Name: "base", Scores: map[string]float64{"t1": 0.7, "t2": 0.7}, P95: 100}
	cand := circleai.NamedAIService{Name: "cand", Scores: map[string]float64{"t1": 0.7, "t2": 0.7}, P95: 100}

	promotedCalled := 0
	loop := selfBenchLoop(t, provider, base, cand, nil, func(context.Context, circleai.AbVerdict) error {
		promotedCalled++
		return nil
	})
	v, _ := loop.Cycle(ctx, "default")
	if !strings.HasPrefix(v.ImprovementsApplied, "rejected") {
		t.Errorf("should reject: %+v", v)
	}
	if promotedCalled != 0 {
		t.Errorf("onPromote should not fire on rejection, called %d", promotedCalled)
	}
	if loop.BestScoreFor("default") != 0 {
		t.Errorf("best score should remain 0 on rejection, got %v", loop.BestScoreFor("default"))
	}
}

func TestSelfBenchLoop_RejectsOnCriticalRegression(t *testing.T) {
	ctx := context.Background()
	provider := circleai.NewInMemoryBenchSuiteProvider()
	_ = provider.Register("default", []circleai.SelfBenchTask{
		{ID: "crit", IsCritical: true},
		{ID: "other"},
	})

	// Overall mean improves (other jumps) but the critical task regresses.
	base := circleai.NamedAIService{Name: "base", Scores: map[string]float64{"crit": 1.0, "other": 0.2}, P95: 100}
	cand := circleai.NamedAIService{Name: "cand", Scores: map[string]float64{"crit": 0.4, "other": 1.0}, P95: 100}

	loop := selfBenchLoop(t, provider, base, cand, nil, nil)
	v, _ := loop.Cycle(ctx, "default")
	if !strings.HasPrefix(v.ImprovementsApplied, "rejected") {
		t.Errorf("critical regression should reject even with mean improvement: %+v", v)
	}
	if !strings.Contains(v.ImprovementsApplied, "critical") {
		t.Errorf("rejection reason should cite the critical regression: %q", v.ImprovementsApplied)
	}
}

func TestSelfBenchLoop_RejectsOnLatencyRegression(t *testing.T) {
	ctx := context.Background()
	provider := circleai.NewInMemoryBenchSuiteProvider()
	_ = provider.Register("default", []circleai.SelfBenchTask{{ID: "t1"}})

	// Mean improves but p95 regresses beyond the 250ms gate.
	base := circleai.NamedAIService{Name: "base", Scores: map[string]float64{"t1": 0.5}, P95: 100}
	cand := circleai.NamedAIService{Name: "cand", Scores: map[string]float64{"t1": 0.9}, P95: 500}

	loop := selfBenchLoop(t, provider, base, cand, nil, nil)
	v, _ := loop.Cycle(ctx, "default")
	if !strings.HasPrefix(v.ImprovementsApplied, "rejected") {
		t.Errorf("latency regression should reject: %+v", v)
	}
	if !strings.Contains(v.ImprovementsApplied, "latency") {
		t.Errorf("rejection reason should cite latency: %q", v.ImprovementsApplied)
	}
}

func TestSelfBenchLoop_Validation(t *testing.T) {
	provider := circleai.NewInMemoryBenchSuiteProvider()
	baseFactory := func(context.Context) (circleai.SelfBenchAIService, error) {
		return circleai.NamedAIService{Name: "b"}, nil
	}
	if _, err := circleai.NewSelfBenchSelfImprovementLoop(nil, circleai.ScoredAbComparer{}, baseFactory, baseFactory, nil, nil); err == nil {
		t.Error("nil provider should error")
	}
	if _, err := circleai.NewSelfBenchSelfImprovementLoop(provider, nil, baseFactory, baseFactory, nil, nil); err == nil {
		t.Error("nil comparer should error")
	}
	if _, err := circleai.NewSelfBenchSelfImprovementLoop(provider, circleai.ScoredAbComparer{}, nil, baseFactory, nil, nil); err == nil {
		t.Error("nil baseline factory should error")
	}
	if _, err := circleai.NewSelfBenchSelfImprovementLoop(provider, circleai.ScoredAbComparer{}, baseFactory, nil, nil, nil); err == nil {
		t.Error("nil candidate factory should error")
	}
}
