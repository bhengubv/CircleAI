// inference_runtime_test.go
//
// Verifies the standalone CircleAI.Inference runtime ports:
//   - PowerBudget → knobs mapping (ResolvePowerBudget) + KvCompressionMode
//     ordinals + apply-result / mode code helpers (PowerBudget.cs, MnnInterop.cs).
//   - ContextWindowBudgetManager fill/evict math + guards (ContextWindowBudgetManager.cs).
//   - LayerStreaming orchestrator forward + null-runner failure + shard discovery
//     (LayerStreamingInference.cs).
//   - VisionInput required-bytes guard (VisionInput.cs).

package circleai_test

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── PowerBudget ───────────────────────────────────────────────────────────────

func TestResolvePowerBudget_Mapping(t *testing.T) {
	// None honours the request literally with TQ4.
	r := circleai.ResolvePowerBudget(circleai.PowerBudgetNone, 5000, nil, false)
	if r.MaxTokens != 5000 || r.PreferredKvMode != circleai.KvCompressionTurboQuant4Bit || r.PreferSmallerModelInChain {
		t.Errorf("None: %+v", r)
	}
	// Low caps at 64 + prefers smaller model.
	r = circleai.ResolvePowerBudget(circleai.PowerBudgetLow, 5000, nil, false)
	if r.MaxTokens != 64 || !r.PreferSmallerModelInChain {
		t.Errorf("Low: %+v", r)
	}
	// Normal caps at 512.
	r = circleai.ResolvePowerBudget(circleai.PowerBudgetNormal, 5000, nil, false)
	if r.MaxTokens != 512 {
		t.Errorf("Normal: %+v", r)
	}
	// High caps at 2048 with KV off.
	r = circleai.ResolvePowerBudget(circleai.PowerBudgetHigh, 5000, nil, false)
	if r.MaxTokens != 2048 || r.PreferredKvMode != circleai.KvCompressionOff {
		t.Errorf("High: %+v", r)
	}
}

func TestResolvePowerBudget_AutoDowngrade(t *testing.T) {
	low := 10
	// Normal below 15% battery downgrades to Low (cap 64).
	r := circleai.ResolvePowerBudget(circleai.PowerBudgetNormal, 5000, &low, false)
	if r.MaxTokens != 64 {
		t.Errorf("Normal@10%% should downgrade to Low (64), got %d", r.MaxTokens)
	}
	// High under thermal throttle downgrades to Normal (cap 512, TQ4).
	r = circleai.ResolvePowerBudget(circleai.PowerBudgetHigh, 5000, nil, true)
	if r.MaxTokens != 512 || r.PreferredKvMode != circleai.KvCompressionTurboQuant4Bit {
		t.Errorf("High+thermal should downgrade to Normal, got %+v", r)
	}
}

func TestKvCompressionCodeHelpers(t *testing.T) {
	if circleai.KvCompressionApplyResultFromCode(0) != circleai.KvApplyApplied {
		t.Error("0 → Applied")
	}
	if circleai.KvCompressionApplyResultFromCode(2) != circleai.KvApplyNotImplemented {
		t.Error("2 → NotImplemented")
	}
	if circleai.KvCompressionApplyResultFromCode(-5) != circleai.KvApplyHandleInvalid {
		t.Error("out-of-range → HandleInvalid")
	}
	if circleai.KvCompressionModeFromCode(3) != circleai.KvCompressionTurboQuant2Bit {
		t.Error("3 → TQ2")
	}
	if circleai.KvCompressionModeFromCode(99) != circleai.KvCompressionOff {
		t.Error("invalid → Off")
	}
	if circleai.IsValidKvCompressionMode(4) {
		t.Error("4 is out of range")
	}
}

// ── ContextWindowBudgetManager ────────────────────────────────────────────────

func TestContextWindowBudgetManager_FillAndEvict(t *testing.T) {
	m, err := circleai.NewContextWindowBudgetManager(1000, circleai.DefaultEvictionThreshold)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if m.ContextSize() != 1000 || m.RemainingTokens() != 1000 {
		t.Errorf("fresh state wrong: size=%d rem=%d", m.ContextSize(), m.RemainingTokens())
	}
	if m.ShouldEvict() {
		t.Error("fresh manager should not evict")
	}
	if err := m.RecordExchange(500, 400); err != nil {
		t.Fatalf("record: %v", err)
	}
	if m.UsedTokens() != 900 || m.RemainingTokens() != 100 {
		t.Errorf("used/remaining wrong: %d/%d", m.UsedTokens(), m.RemainingTokens())
	}
	if m.FillRatio() != 0.9 {
		t.Errorf("fill ratio: got %v", m.FillRatio())
	}
	if !m.ShouldEvict() {
		t.Error("0.90 ≥ 0.85 threshold should trigger eviction")
	}
	// Evict back to 0.50 → drop 900-500 = 400.
	n, err := m.CalculateEvictionCount(0.50)
	if err != nil {
		t.Fatalf("calc: %v", err)
	}
	if n != 400 {
		t.Errorf("eviction count: got %d want 400", n)
	}
	m.Reset()
	if m.UsedTokens() != 0 {
		t.Error("reset should zero used tokens")
	}
}

func TestContextWindowBudgetManager_Guards(t *testing.T) {
	if _, err := circleai.NewContextWindowBudgetManager(0, 0.5); err == nil {
		t.Error("zero context size should error")
	}
	if _, err := circleai.NewContextWindowBudgetManager(10, 1.5); err == nil {
		t.Error("threshold > 1 should error")
	}
	m, _ := circleai.NewContextWindowBudgetManager(10, 0.85)
	if err := m.RecordExchange(-1, 0); err == nil {
		t.Error("negative tokens should error")
	}
	if _, err := m.CalculateEvictionCount(2.0); err == nil {
		t.Error("target > 1 should error")
	}
}

// ── LayerStreaming ────────────────────────────────────────────────────────────

// echoLayerRunner adds 1.0 to every hidden element per layer, deterministically.
type echoLayerRunner struct{ evicted []int }

func (r *echoLayerRunner) BackendID() string { return "echo" }
func (r *echoLayerRunner) IsAvailable() bool { return true }
func (r *echoLayerRunner) RunLayer(_ context.Context, shard circleai.LayerWeightShard, in []float32) (circleai.LayerActivations, error) {
	out := make([]float32, len(in))
	for i, v := range in {
		out[i] = v + 1
	}
	return circleai.LayerActivations{LayerIndex: shard.LayerIndex, Hidden: out}, nil
}
func (r *echoLayerRunner) Evict(_ context.Context, layerIndex int) error {
	r.evicted = append(r.evicted, layerIndex)
	return nil
}

func TestLayerStreamingOrchestrator_Forward(t *testing.T) {
	runner := &echoLayerRunner{}
	orch, err := circleai.NewLayerStreamingOrchestrator(runner)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	plan := circleai.LayerStreamingPlan{
		ModelID:     "m",
		TotalLayers: 3,
		Shards: []circleai.LayerWeightShard{
			{LayerIndex: 0}, {LayerIndex: 1}, {LayerIndex: 2},
		},
	}
	var completed []int
	last, err := orch.Forward(context.Background(), plan, []float32{0, 0}, func(a circleai.LayerActivations) {
		completed = append(completed, a.LayerIndex)
	})
	if err != nil {
		t.Fatalf("forward: %v", err)
	}
	// 3 layers each +1 → final hidden = 3.
	if last.Hidden[0] != 3 || last.Hidden[1] != 3 {
		t.Errorf("final hidden wrong: %v", last.Hidden)
	}
	if len(completed) != 3 || len(runner.evicted) != 3 {
		t.Errorf("expected 3 completions + 3 evictions, got %d/%d", len(completed), len(runner.evicted))
	}
}

func TestLayerStreaming_NullRunnerAndEmptyPlan(t *testing.T) {
	orch, _ := circleai.NewLayerStreamingOrchestrator(circleai.NullLayerStreamingRunnerInstance)
	// Empty plan errors before touching the runner.
	if _, err := orch.Forward(context.Background(), circleai.LayerStreamingPlan{}, nil, nil); err == nil {
		t.Error("empty plan should error")
	}
	// Null runner fails on first RunLayer.
	plan := circleai.LayerStreamingPlan{Shards: []circleai.LayerWeightShard{{LayerIndex: 0}}}
	if _, err := orch.Forward(context.Background(), plan, nil, nil); err == nil {
		t.Error("null runner should fail")
	}
	if circleai.NullLayerStreamingRunnerInstance.IsAvailable() {
		t.Error("null runner should report unavailable")
	}
}

func TestDiscoverLayerShards(t *testing.T) {
	dir := t.TempDir()
	// Create out-of-order layer files + a non-layer file.
	for _, n := range []string{"layer_2.safetensors", "layer_0.safetensors", "layer_10.bin", "readme.txt"} {
		if err := os.WriteFile(filepath.Join(dir, n), []byte("xx"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	plan, err := circleai.DiscoverLayerShards("m", dir)
	if err != nil {
		t.Fatalf("discover: %v", err)
	}
	if plan.TotalLayers != 3 {
		t.Errorf("expected 3 layer shards, got %d", plan.TotalLayers)
	}
	// Sorted by index: 0, 2, 10.
	wantOrder := []int{0, 2, 10}
	for i, s := range plan.Shards {
		if s.LayerIndex != wantOrder[i] {
			t.Errorf("shard %d index: got %d want %d", i, s.LayerIndex, wantOrder[i])
		}
	}
	if plan.ApproxParameterBytes != 6 { // 3 files × 2 bytes
		t.Errorf("approx bytes: got %d want 6", plan.ApproxParameterBytes)
	}
	if _, err := circleai.DiscoverLayerShards("m", filepath.Join(dir, "nope")); err == nil {
		t.Error("missing directory should error")
	}
}

// ── VisionInput ───────────────────────────────────────────────────────────────

func TestVisionInput_RequiresBytes(t *testing.T) {
	if _, err := circleai.NewVisionInput(nil, "image/png"); err == nil {
		t.Error("nil image bytes should error")
	}
	v, err := circleai.NewVisionInput([]byte{1, 2, 3}, "image/jpeg")
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if len(v.ImageBytes) != 3 || v.MimeType != "image/jpeg" {
		t.Errorf("vision input wrong: %+v", v)
	}
	// Defensive copy: mutating the source must not affect the stored bytes.
	src := []byte{9, 9}
	v2, _ := circleai.NewVisionInput(src, "")
	src[0] = 0
	if v2.ImageBytes[0] != 9 {
		t.Error("VisionInput should copy the byte slice")
	}
}
