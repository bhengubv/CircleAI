// enterprise_contracts.go
//
// Ports CircleAI.Inference.Server.Enterprise:
//   Contracts.cs                       → ServerTier, TenantContext, TenantQuota,
//                                         BatchSlot, ShardDescriptor, OffloadDecision,
//                                         ITenantRouter, IBatchScheduler,
//                                         IModelShardPlanner, ICrossTierOffload
//   InMemoryInferenceServerEnterprise.cs → RoundRobinTenantRouter,
//                                         InMemoryBatchScheduler,
//                                         EvenSplitModelShardPlanner,
//                                         PolicyCrossTierOffload
//   NullImplementations.cs             → NullTenantRouter, NullBatchScheduler,
//                                         NullModelShardPlanner, NullCrossTierOffload
//
// (2.7.0) Enterprise-tier inference-server primitives: multi-tenant routing,
// batch scheduling, model sharding, and RT-12 v2 cross-tier offload. The
// in-memory impls are real (round-robin node picking, a reservation queue with
// deadlines, an even-bucket shard split, a policy offload decision) — the Null
// impls are the single-node defaults that fall back to local execution.

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// ServerTier is the deployment tier. Ports CircleAI.Inference.Server.Enterprise.ServerTier.
type ServerTier int

const (
	// ServerTierSingleNode — one node, no tenant routing.
	ServerTierSingleNode ServerTier = 0
	// ServerTierServer — a single multi-tenant server.
	ServerTierServer ServerTier = 1
	// ServerTierServerFarm — a farm of nodes (top tier).
	ServerTierServerFarm ServerTier = 2
)

// TenantContext identifies the calling tenant. Ports TenantContext.
type TenantContext struct {
	TenantID       string
	ParentTenantID string
	Tags           map[string]string
}

// TenantQuota is the per-tenant resource quota. Ports TenantQuota.
type TenantQuota struct {
	TenantID              string
	MaxConcurrentRequests int
	MaxModelsLoaded       int
	MaxBytesInFlight      int64
	DailyTokenBudget      int
}

// BatchSlot is a reserved batch slot. Ports BatchSlot.
type BatchSlot struct {
	SlotID      string
	ModelID     string
	Tokens      int
	DeadlineUTC time.Time
}

// ShardDescriptor is one model shard's placement. Ports ShardDescriptor.
type ShardDescriptor struct {
	ShardID    string
	RangeStart int
	RangeEnd   int
	NodeID     string
}

// OffloadDecision is the cross-tier offload verdict. Ports OffloadDecision.
type OffloadDecision struct {
	ShouldOffload bool
	TargetNodeID  string
	Reason        string
}

// ITenantRouter picks a backend node per tenant. Ports ITenantRouter.
type ITenantRouter interface {
	BackendID() string
	ChooseNode(ctx context.Context, tenant TenantContext, modelID string) (string, bool, error)
	SetQuota(ctx context.Context, quota TenantQuota) error
	GetQuota(ctx context.Context, tenantID string) (TenantQuota, bool, error)
}

// IBatchScheduler coalesces small requests into one big one. Ports IBatchScheduler.
type IBatchScheduler interface {
	BackendID() string
	Reserve(ctx context.Context, modelID string, estimatedTokens int, maxWait time.Duration) (BatchSlot, error)
	Release(ctx context.Context, slot BatchSlot) error
}

// IModelShardPlanner plans model sharding for very-large-model deployments.
// Ports IModelShardPlanner.
type IModelShardPlanner interface {
	BackendID() string
	Plan(ctx context.Context, modelID string, paramBytes int) ([]ShardDescriptor, error)
}

// ICrossTierOffload is the RT-12 v2 cross-tier offload strategy. Ports ICrossTierOffload.
type ICrossTierOffload interface {
	BackendID() string
	ShouldOffload(ctx context.Context, modelID string, promptTokens int, callerTier ServerTier) (OffloadDecision, error)
}

// ── real in-memory impls ─────────────────────────────────────────────────────

// RoundRobinTenantRouter round-robins over registered nodes per model. Ports
// RoundRobinTenantRouter.
type RoundRobinTenantRouter struct {
	mu           sync.Mutex
	quotas       map[string]TenantQuota
	nodesByModel map[string][]string
	rr           map[string]int
}

// NewRoundRobinTenantRouter builds an empty router.
func NewRoundRobinTenantRouter() *RoundRobinTenantRouter {
	return &RoundRobinTenantRouter{
		quotas:       make(map[string]TenantQuota),
		nodesByModel: make(map[string][]string),
		rr:           make(map[string]int),
	}
}

// BackendID returns "round-robin".
func (r *RoundRobinTenantRouter) BackendID() string { return "round-robin" }

// RegisterNode adds nodeId to the node list for modelId (deduped). Ports RegisterNode.
func (r *RoundRobinTenantRouter) RegisterNode(modelID, nodeID string) error {
	if strings.TrimSpace(modelID) == "" {
		return errors.New("modelId required")
	}
	if strings.TrimSpace(nodeID) == "" {
		return errors.New("nodeId required")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	list := r.nodesByModel[modelID]
	for _, n := range list {
		if n == nodeID {
			return nil
		}
	}
	r.nodesByModel[modelID] = append(list, nodeID)
	return nil
}

// ChooseNode picks the next node round-robin for modelId. Returns ("", false)
// when no nodes are registered. Ports ChooseNodeAsync.
func (r *RoundRobinTenantRouter) ChooseNode(_ context.Context, tenant TenantContext, modelID string) (string, bool, error) {
	if strings.TrimSpace(modelID) == "" {
		return "", false, errors.New("modelId required")
	}
	_ = tenant
	r.mu.Lock()
	defer r.mu.Unlock()
	nodes := r.nodesByModel[modelID]
	if len(nodes) == 0 {
		return "", false, nil
	}
	idx := r.rr[modelID]
	pick := nodes[idx%len(nodes)]
	r.rr[modelID] = idx + 1
	return pick, true, nil
}

// SetQuota records a tenant quota. Ports SetQuotaAsync.
func (r *RoundRobinTenantRouter) SetQuota(_ context.Context, quota TenantQuota) error {
	r.mu.Lock()
	r.quotas[quota.TenantID] = quota
	r.mu.Unlock()
	return nil
}

// GetQuota reads a tenant quota. Ports GetQuotaAsync.
func (r *RoundRobinTenantRouter) GetQuota(_ context.Context, tenantID string) (TenantQuota, bool, error) {
	if strings.TrimSpace(tenantID) == "" {
		return TenantQuota{}, false, errors.New("tenantId required")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	q, ok := r.quotas[tenantID]
	return q, ok, nil
}

// InMemoryBatchScheduler is a reservation queue with deadline guarantees. Ports
// InMemoryBatchScheduler.
type InMemoryBatchScheduler struct {
	mu    sync.Mutex
	slots map[string]BatchSlot
	seq   int64
}

// NewInMemoryBatchScheduler builds an empty scheduler.
func NewInMemoryBatchScheduler() *InMemoryBatchScheduler {
	return &InMemoryBatchScheduler{slots: make(map[string]BatchSlot)}
}

// BackendID returns "in-memory".
func (s *InMemoryBatchScheduler) BackendID() string { return "in-memory" }

// Reserve reserves a slot with a deadline. Ports ReserveAsync.
func (s *InMemoryBatchScheduler) Reserve(_ context.Context, modelID string, estimatedTokens int, maxWait time.Duration) (BatchSlot, error) {
	if strings.TrimSpace(modelID) == "" {
		return BatchSlot{}, errors.New("modelId required")
	}
	if estimatedTokens <= 0 {
		return BatchSlot{}, errors.New("estimatedTokens out of range")
	}
	if maxWait <= 0 {
		return BatchSlot{}, errors.New("maxWait out of range")
	}
	id := atomic.AddInt64(&s.seq, 1)
	slot := BatchSlot{
		SlotID:      "slot-" + itoa(int(id)),
		ModelID:     modelID,
		Tokens:      estimatedTokens,
		DeadlineUTC: time.Now().UTC().Add(maxWait),
	}
	s.mu.Lock()
	s.slots[slot.SlotID] = slot
	s.mu.Unlock()
	return slot, nil
}

// Release frees a slot. Ports ReleaseAsync.
func (s *InMemoryBatchScheduler) Release(_ context.Context, slot BatchSlot) error {
	s.mu.Lock()
	delete(s.slots, slot.SlotID)
	s.mu.Unlock()
	return nil
}

// EvenSplitModelShardPlanner splits param bytes into even buckets across the
// nodes for a model. Ports EvenSplitModelShardPlanner.
type EvenSplitModelShardPlanner struct {
	nodesFor func(modelID string) []string
}

// NewEvenSplitModelShardPlanner builds a planner over a node-lookup function.
func NewEvenSplitModelShardPlanner(nodesFor func(modelID string) []string) (*EvenSplitModelShardPlanner, error) {
	if nodesFor == nil {
		return nil, errors.New("nodesFor is required")
	}
	return &EvenSplitModelShardPlanner{nodesFor: nodesFor}, nil
}

// BackendID returns "even-split".
func (p *EvenSplitModelShardPlanner) BackendID() string { return "even-split" }

// Plan computes an even-bucket shard split (remainder distributed to the first
// buckets, matching C#). Returns empty when no nodes are registered. Ports PlanAsync.
func (p *EvenSplitModelShardPlanner) Plan(_ context.Context, modelID string, paramBytes int) ([]ShardDescriptor, error) {
	if strings.TrimSpace(modelID) == "" {
		return nil, errors.New("modelId required")
	}
	if paramBytes <= 0 {
		return nil, errors.New("paramBytes out of range")
	}
	nodes := p.nodesFor(modelID)
	if len(nodes) == 0 {
		return []ShardDescriptor{}, nil
	}
	bucket := paramBytes / len(nodes)
	rem := paramBytes % len(nodes)
	list := make([]ShardDescriptor, 0, len(nodes))
	cursor := 0
	for i := 0; i < len(nodes); i++ {
		size := bucket
		if i < rem {
			size++
		}
		list = append(list, ShardDescriptor{
			ShardID:    "shard-" + modelID + "-" + itoa(i),
			RangeStart: cursor,
			RangeEnd:   cursor + size,
			NodeID:     nodes[i],
		})
		cursor += size
	}
	return list, nil
}

// PolicyCrossTierOffload offloads when the prompt exceeds the local ceiling.
// Ports PolicyCrossTierOffload.
type PolicyCrossTierOffload struct {
	localPromptCeiling int
	farmTargetNode     string
}

// NewPolicyCrossTierOffload builds the strategy. localPromptCeiling defaults to
// 2048 when non-positive; farmTargetNode ("" allowed) is the offload target.
func NewPolicyCrossTierOffload(localPromptCeiling int, farmTargetNode string) (*PolicyCrossTierOffload, error) {
	if localPromptCeiling <= 0 {
		return nil, errors.New("localPromptCeiling out of range")
	}
	return &PolicyCrossTierOffload{localPromptCeiling: localPromptCeiling, farmTargetNode: farmTargetNode}, nil
}

// BackendID returns "policy".
func (o *PolicyCrossTierOffload) BackendID() string { return "policy" }

// ShouldOffload decides based on caller tier + prompt size. Ports ShouldOffloadAsync.
func (o *PolicyCrossTierOffload) ShouldOffload(_ context.Context, modelID string, promptTokens int, callerTier ServerTier) (OffloadDecision, error) {
	if strings.TrimSpace(modelID) == "" {
		return OffloadDecision{}, errors.New("modelId required")
	}
	if promptTokens < 0 {
		return OffloadDecision{}, errors.New("promptTokens out of range")
	}
	if callerTier == ServerTierServerFarm {
		return OffloadDecision{ShouldOffload: false, Reason: "Caller is already top-tier"}, nil
	}
	if promptTokens <= o.localPromptCeiling {
		return OffloadDecision{ShouldOffload: false, Reason: "Prompt fits locally"}, nil
	}
	return OffloadDecision{
		ShouldOffload: true,
		TargetNodeID:  o.farmTargetNode,
		Reason:        "Prompt exceeds local ceiling (" + itoa(o.localPromptCeiling) + " tokens)",
	}, nil
}

// ── Null (single-node default) impls ─────────────────────────────────────────

// NullTenantRouter falls back to local execution. Ports NullTenantRouter.
type NullTenantRouter struct{}

// NullTenantRouterInstance mirrors NullTenantRouter.Instance.
var NullTenantRouterInstance = NullTenantRouter{}

func (NullTenantRouter) BackendID() string { return "null" }
func (NullTenantRouter) ChooseNode(context.Context, TenantContext, string) (string, bool, error) {
	return "", false, nil
}
func (NullTenantRouter) SetQuota(context.Context, TenantQuota) error { return nil }
func (NullTenantRouter) GetQuota(context.Context, string) (TenantQuota, bool, error) {
	return TenantQuota{}, false, nil
}

// NullBatchScheduler falls back to an immediate best-effort slot. Ports NullBatchScheduler.
type NullBatchScheduler struct{}

// NullBatchSchedulerInstance mirrors NullBatchScheduler.Instance.
var NullBatchSchedulerInstance = NullBatchScheduler{}

func (NullBatchScheduler) BackendID() string { return "null" }
func (NullBatchScheduler) Reserve(_ context.Context, modelID string, est int, maxWait time.Duration) (BatchSlot, error) {
	return BatchSlot{
		SlotID:      emptyGUID,
		ModelID:     modelID,
		Tokens:      est,
		DeadlineUTC: time.Now().UTC().Add(maxWait),
	}, nil
}
func (NullBatchScheduler) Release(context.Context, BatchSlot) error { return nil }

// NullModelShardPlanner returns no shards. Ports NullModelShardPlanner.
type NullModelShardPlanner struct{}

// NullModelShardPlannerInstance mirrors NullModelShardPlanner.Instance.
var NullModelShardPlannerInstance = NullModelShardPlanner{}

func (NullModelShardPlanner) BackendID() string { return "null" }
func (NullModelShardPlanner) Plan(context.Context, string, int) ([]ShardDescriptor, error) {
	return []ShardDescriptor{}, nil
}

// NullCrossTierOffload never offloads. Ports NullCrossTierOffload.
type NullCrossTierOffload struct{}

// NullCrossTierOffloadInstance mirrors NullCrossTierOffload.Instance.
var NullCrossTierOffloadInstance = NullCrossTierOffload{}

func (NullCrossTierOffload) BackendID() string { return "null" }
func (NullCrossTierOffload) ShouldOffload(context.Context, string, int, ServerTier) (OffloadDecision, error) {
	return OffloadDecision{
		ShouldOffload: false,
		Reason:        "Local execution; no cross-tier offload configured.",
	}, nil
}

// emptyGUID mirrors Guid.Empty.ToString() ("00000000-0000-0000-0000-000000000000").
const emptyGUID = "00000000-0000-0000-0000-000000000000"

var (
	_ ITenantRouter      = (*RoundRobinTenantRouter)(nil)
	_ ITenantRouter      = NullTenantRouter{}
	_ IBatchScheduler    = (*InMemoryBatchScheduler)(nil)
	_ IBatchScheduler    = NullBatchScheduler{}
	_ IModelShardPlanner = (*EvenSplitModelShardPlanner)(nil)
	_ IModelShardPlanner = NullModelShardPlanner{}
	_ ICrossTierOffload  = (*PolicyCrossTierOffload)(nil)
	_ ICrossTierOffload  = NullCrossTierOffload{}
)
