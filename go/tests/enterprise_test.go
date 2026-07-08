// enterprise_test.go
//
// Verifies CircleAI.Inference.Server.Enterprise ports (Contracts.cs /
// InMemoryInferenceServerEnterprise.cs / NullImplementations.cs):
//   - RoundRobinTenantRouter: node registration, round-robin pick, quota get/set,
//     empty-node → no pick.
//   - InMemoryBatchScheduler: reserve produces a deadlined slot, release, guards.
//   - EvenSplitModelShardPlanner: even-bucket split with remainder distribution,
//     empty-node → no shards.
//   - PolicyCrossTierOffload: top-tier no-offload, fits-locally no-offload,
//     exceeds-ceiling offload to farm node.
//   - Null impls fall back to local execution.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestRoundRobinTenantRouter(t *testing.T) {
	ctx := context.Background()
	r := circleai.NewRoundRobinTenantRouter()
	if r.BackendID() != "round-robin" {
		t.Errorf("backend id: %q", r.BackendID())
	}

	// No nodes → no pick.
	if _, ok, _ := r.ChooseNode(ctx, circleai.TenantContext{TenantID: "t"}, "m"); ok {
		t.Error("empty router should not pick a node")
	}

	_ = r.RegisterNode("m", "node-a")
	_ = r.RegisterNode("m", "node-b")
	_ = r.RegisterNode("m", "node-a") // dedupe

	// Round-robin: a, b, a, b.
	want := []string{"node-a", "node-b", "node-a", "node-b"}
	for i, w := range want {
		got, ok, _ := r.ChooseNode(ctx, circleai.TenantContext{TenantID: "t"}, "m")
		if !ok || got != w {
			t.Errorf("pick %d: got %q ok=%v want %q", i, got, ok, w)
		}
	}

	// Quotas.
	q := circleai.TenantQuota{TenantID: "t", MaxConcurrentRequests: 5, DailyTokenBudget: 1000}
	_ = r.SetQuota(ctx, q)
	got, ok, _ := r.GetQuota(ctx, "t")
	if !ok || got.MaxConcurrentRequests != 5 || got.DailyTokenBudget != 1000 {
		t.Errorf("quota round-trip: %+v ok=%v", got, ok)
	}
	if _, ok, _ := r.GetQuota(ctx, "unknown"); ok {
		t.Error("unknown tenant should have no quota")
	}
}

func TestInMemoryBatchScheduler(t *testing.T) {
	ctx := context.Background()
	s := circleai.NewInMemoryBatchScheduler()
	if s.BackendID() != "in-memory" {
		t.Errorf("backend id: %q", s.BackendID())
	}

	before := time.Now().UTC()
	slot, err := s.Reserve(ctx, "m", 128, 2*time.Second)
	if err != nil {
		t.Fatalf("reserve: %v", err)
	}
	if slot.ModelID != "m" || slot.Tokens != 128 {
		t.Errorf("slot fields: %+v", slot)
	}
	if !slot.DeadlineUTC.After(before) {
		t.Error("slot deadline should be in the future")
	}
	if slot.SlotID == "" {
		t.Error("slot should have an id")
	}
	if err := s.Release(ctx, slot); err != nil {
		t.Fatalf("release: %v", err)
	}

	// Guards.
	if _, err := s.Reserve(ctx, "", 1, time.Second); err == nil {
		t.Error("empty modelId should error")
	}
	if _, err := s.Reserve(ctx, "m", 0, time.Second); err == nil {
		t.Error("zero tokens should error")
	}
	if _, err := s.Reserve(ctx, "m", 1, 0); err == nil {
		t.Error("zero maxWait should error")
	}
}

func TestEvenSplitModelShardPlanner(t *testing.T) {
	ctx := context.Background()
	nodes := []string{"n0", "n1", "n2"}
	planner, err := circleai.NewEvenSplitModelShardPlanner(func(string) []string { return nodes })
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	// 10 bytes over 3 nodes → buckets 4,3,3 (remainder 1 to the first).
	shards, err := planner.Plan(ctx, "m", 10)
	if err != nil {
		t.Fatalf("plan: %v", err)
	}
	if len(shards) != 3 {
		t.Fatalf("expected 3 shards, got %d", len(shards))
	}
	wantRanges := [][2]int{{0, 4}, {4, 7}, {7, 10}}
	for i, s := range shards {
		if s.RangeStart != wantRanges[i][0] || s.RangeEnd != wantRanges[i][1] {
			t.Errorf("shard %d range: got [%d,%d) want [%d,%d)", i, s.RangeStart, s.RangeEnd, wantRanges[i][0], wantRanges[i][1])
		}
		if s.NodeID != nodes[i] {
			t.Errorf("shard %d node: got %q want %q", i, s.NodeID, nodes[i])
		}
	}
	// Contiguous coverage: last shard ends at paramBytes.
	if shards[len(shards)-1].RangeEnd != 10 {
		t.Errorf("shards should cover all 10 bytes, last end=%d", shards[len(shards)-1].RangeEnd)
	}

	// No nodes → no shards.
	emptyPlanner, _ := circleai.NewEvenSplitModelShardPlanner(func(string) []string { return nil })
	empty, _ := emptyPlanner.Plan(ctx, "m", 100)
	if len(empty) != 0 {
		t.Errorf("no nodes should yield no shards, got %d", len(empty))
	}

	// Guards.
	if _, err := planner.Plan(ctx, "", 10); err == nil {
		t.Error("empty modelId should error")
	}
	if _, err := planner.Plan(ctx, "m", 0); err == nil {
		t.Error("zero paramBytes should error")
	}
}

func TestPolicyCrossTierOffload(t *testing.T) {
	ctx := context.Background()
	o, err := circleai.NewPolicyCrossTierOffload(2048, "farm-node-1")
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	// Top-tier caller never offloads.
	d, _ := o.ShouldOffload(ctx, "m", 9999, circleai.ServerTierServerFarm)
	if d.ShouldOffload {
		t.Errorf("top-tier should not offload: %+v", d)
	}

	// Fits locally → no offload.
	d, _ = o.ShouldOffload(ctx, "m", 1000, circleai.ServerTierSingleNode)
	if d.ShouldOffload {
		t.Errorf("small prompt should fit locally: %+v", d)
	}

	// Exceeds ceiling → offload to the farm node.
	d, _ = o.ShouldOffload(ctx, "m", 4096, circleai.ServerTierServer)
	if !d.ShouldOffload || d.TargetNodeID != "farm-node-1" {
		t.Errorf("large prompt should offload to farm node: %+v", d)
	}

	// Guards.
	if _, err := circleai.NewPolicyCrossTierOffload(0, ""); err == nil {
		t.Error("zero ceiling should error")
	}
	if _, err := o.ShouldOffload(ctx, "", 100, circleai.ServerTierServer); err == nil {
		t.Error("empty modelId should error")
	}
}

func TestNullEnterpriseImpls(t *testing.T) {
	ctx := context.Background()

	if _, ok, _ := circleai.NullTenantRouterInstance.ChooseNode(ctx, circleai.TenantContext{TenantID: "t"}, "m"); ok {
		t.Error("null router should pick no node")
	}
	if circleai.NullTenantRouterInstance.BackendID() != "null" {
		t.Error("null router backend id")
	}

	slot, _ := circleai.NullBatchSchedulerInstance.Reserve(ctx, "m", 10, time.Second)
	if slot.ModelID != "m" {
		t.Errorf("null scheduler slot should echo modelId, got %+v", slot)
	}

	shards, _ := circleai.NullModelShardPlannerInstance.Plan(ctx, "m", 100)
	if len(shards) != 0 {
		t.Error("null planner should yield no shards")
	}

	d, _ := circleai.NullCrossTierOffloadInstance.ShouldOffload(ctx, "m", 999999, circleai.ServerTierSingleNode)
	if d.ShouldOffload {
		t.Error("null offload should never offload")
	}
}
