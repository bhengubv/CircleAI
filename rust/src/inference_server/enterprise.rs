//! enterprise.rs
//!
//! Enterprise-tier inference-server contracts + implementations, ported from
//! `CircleAI.Inference.Server.Enterprise/` (`Contracts.cs`,
//! `InMemoryInferenceServerEnterprise.cs`, `NullImplementations.cs`).
//!
//! (2.7.0) Multi-tenant routing + batch scheduling + model sharding + cross-tier
//! offload (RT-12 v2). The C# `ValueTask`-returning async methods are ported as
//! sync (per crate convention); the real in-memory backends and the single-node
//! null defaults reproduce the exact logic.

use std::collections::BTreeMap;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Mutex;

use chrono::{DateTime, Utc};
use uuid::Uuid;

/// Server tier — single node, server, or server farm. Mirrors `ServerTier`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ServerTier {
    SingleNode,
    Server,
    ServerFarm,
}

/// Tenant identity + hierarchy context. Mirrors `TenantContext`.
#[derive(Debug, Clone, PartialEq)]
pub struct TenantContext {
    pub tenant_id: String,
    pub parent_tenant_id: Option<String>,
    pub tags: Option<BTreeMap<String, String>>,
}

impl TenantContext {
    /// Constructs a context for `tenant_id` with no parent / tags.
    pub fn new(tenant_id: impl Into<String>) -> Self {
        Self {
            tenant_id: tenant_id.into(),
            parent_tenant_id: None,
            tags: None,
        }
    }
}

/// Per-tenant resource quota. Mirrors `TenantQuota`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TenantQuota {
    pub tenant_id: String,
    pub max_concurrent_requests: i32,
    pub max_models_loaded: i32,
    pub max_bytes_in_flight: i64,
    pub daily_token_budget: i32,
}

/// (2.7.0) Multi-tenant routing — pick a backend node per tenant. Sync port of
/// `ITenantRouter`.
pub trait ITenantRouter: Send + Sync {
    /// Backend identifier.
    fn backend_id(&self) -> &str;
    /// Choose a node for `(tenant, model_id)`, or `None` when none registered.
    fn choose_node(&self, tenant: &TenantContext, model_id: &str) -> Option<String>;
    /// Set a tenant's quota.
    fn set_quota(&self, quota: TenantQuota);
    /// Get a tenant's quota, or `None`.
    fn get_quota(&self, tenant_id: &str) -> Option<TenantQuota>;
}

/// A reserved batch slot. Mirrors `BatchSlot`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BatchSlot {
    pub slot_id: String,
    pub model_id: String,
    pub tokens: i32,
    pub deadline_utc: DateTime<Utc>,
}

/// (2.7.0) Batch scheduler — coalesce small requests into one big one. Sync port
/// of `IBatchScheduler`.
pub trait IBatchScheduler: Send + Sync {
    /// Backend identifier.
    fn backend_id(&self) -> &str;
    /// Reserve a slot for `model_id` with an estimated token count + a max wait.
    fn reserve(&self, model_id: &str, estimated_tokens: i32, max_wait: chrono::Duration)
        -> BatchSlot;
    /// Release a previously reserved slot.
    fn release(&self, slot: &BatchSlot);
}

/// A single shard's placement. Mirrors `ShardDescriptor`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ShardDescriptor {
    pub shard_id: String,
    pub range_start: i32,
    pub range_end: i32,
    pub node_id: String,
}

/// (2.7.0) Model-sharding plan for very-large-model deployments. Sync port of
/// `IModelShardPlanner`.
pub trait IModelShardPlanner: Send + Sync {
    /// Backend identifier.
    fn backend_id(&self) -> &str;
    /// Plan the shards for `model_id` over `param_bytes`.
    fn plan(&self, model_id: &str, param_bytes: i32) -> Vec<ShardDescriptor>;
}

/// A cross-tier offload decision. Mirrors `OffloadDecision`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OffloadDecision {
    pub should_offload: bool,
    pub target_node_id: Option<String>,
    pub reason: Option<String>,
}

/// (2.7.0) RT-12 v2 cross-tier offload — phone borrows server brain. Sync port of
/// `ICrossTierOffload`.
pub trait ICrossTierOffload: Send + Sync {
    /// Backend identifier.
    fn backend_id(&self) -> &str;
    /// Decide whether to offload a `(model_id, prompt_tokens)` from `caller_tier`.
    fn should_offload(
        &self,
        model_id: &str,
        prompt_tokens: i32,
        caller_tier: ServerTier,
    ) -> OffloadDecision;
}

// ─────────────────────────────────────────────────────────────────────────────
// Real in-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/// Round-robin tenant router over registered nodes per model. Mirrors
/// `RoundRobinTenantRouter`.
#[derive(Default)]
pub struct RoundRobinTenantRouter {
    quotas: Mutex<BTreeMap<String, TenantQuota>>,
    nodes_by_model: Mutex<BTreeMap<String, Vec<String>>>,
    rr: Mutex<BTreeMap<String, usize>>,
}

impl RoundRobinTenantRouter {
    /// Constructs an empty router.
    pub fn new() -> Self {
        Self::default()
    }

    /// Register a node that can serve `model_id`. Idempotent per (model, node).
    pub fn register_node(&self, model_id: &str, node_id: &str) {
        assert!(!model_id.trim().is_empty(), "modelId required");
        assert!(!node_id.trim().is_empty(), "nodeId required");
        let mut map = self.nodes_by_model.lock().unwrap();
        let list = map.entry(model_id.to_string()).or_default();
        if !list.iter().any(|n| n == node_id) {
            list.push(node_id.to_string());
        }
    }
}

impl ITenantRouter for RoundRobinTenantRouter {
    fn backend_id(&self) -> &str {
        "round-robin"
    }

    fn choose_node(&self, _tenant: &TenantContext, model_id: &str) -> Option<String> {
        assert!(!model_id.trim().is_empty(), "modelId required");
        let nodes_map = self.nodes_by_model.lock().unwrap();
        let nodes = nodes_map.get(model_id)?;
        if nodes.is_empty() {
            return None;
        }
        let mut rr = self.rr.lock().unwrap();
        let idx = *rr.get(model_id).unwrap_or(&0);
        let pick = nodes[idx % nodes.len()].clone();
        rr.insert(model_id.to_string(), idx + 1);
        Some(pick)
    }

    fn set_quota(&self, quota: TenantQuota) {
        self.quotas
            .lock()
            .unwrap()
            .insert(quota.tenant_id.clone(), quota);
    }

    fn get_quota(&self, tenant_id: &str) -> Option<TenantQuota> {
        assert!(!tenant_id.trim().is_empty(), "tenantId required");
        self.quotas.lock().unwrap().get(tenant_id).cloned()
    }
}

/// In-memory batch scheduler with a real reservation registry + deadline. Mirrors
/// `InMemoryBatchScheduler`.
#[derive(Default)]
pub struct InMemoryBatchScheduler {
    slots: Mutex<BTreeMap<String, BatchSlot>>,
    seq: AtomicI64,
}

impl InMemoryBatchScheduler {
    /// Constructs an empty scheduler.
    pub fn new() -> Self {
        Self::default()
    }

    /// Number of currently reserved slots (test helper).
    pub fn reserved_count(&self) -> usize {
        self.slots.lock().unwrap().len()
    }
}

impl IBatchScheduler for InMemoryBatchScheduler {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    fn reserve(
        &self,
        model_id: &str,
        estimated_tokens: i32,
        max_wait: chrono::Duration,
    ) -> BatchSlot {
        assert!(!model_id.trim().is_empty(), "modelId required");
        assert!(estimated_tokens > 0, "estimatedTokens out of range");
        assert!(max_wait > chrono::Duration::zero(), "maxWait out of range");
        let n = self.seq.fetch_add(1, Ordering::SeqCst) + 1;
        let slot = BatchSlot {
            slot_id: format!("slot-{n}"),
            model_id: model_id.to_string(),
            tokens: estimated_tokens,
            deadline_utc: Utc::now() + max_wait,
        };
        self.slots
            .lock()
            .unwrap()
            .insert(slot.slot_id.clone(), slot.clone());
        slot
    }

    fn release(&self, slot: &BatchSlot) {
        self.slots.lock().unwrap().remove(&slot.slot_id);
    }
}

/// Even-bucket model shard planner over nodes supplied by a closure. Mirrors
/// `EvenSplitModelShardPlanner`.
pub struct EvenSplitModelShardPlanner {
    nodes_for: Box<dyn Fn(&str) -> Vec<String> + Send + Sync>,
}

impl EvenSplitModelShardPlanner {
    /// Constructs the planner over a `model_id -> nodes` resolver.
    pub fn new<F>(nodes_for: F) -> Self
    where
        F: Fn(&str) -> Vec<String> + Send + Sync + 'static,
    {
        Self {
            nodes_for: Box::new(nodes_for),
        }
    }
}

impl IModelShardPlanner for EvenSplitModelShardPlanner {
    fn backend_id(&self) -> &str {
        "even-split"
    }

    fn plan(&self, model_id: &str, param_bytes: i32) -> Vec<ShardDescriptor> {
        assert!(!model_id.trim().is_empty(), "modelId required");
        assert!(param_bytes > 0, "paramBytes out of range");

        let nodes = (self.nodes_for)(model_id);
        if nodes.is_empty() {
            return Vec::new();
        }

        let n = nodes.len() as i32;
        let bucket = param_bytes / n;
        let rem = param_bytes % n;
        let mut list = Vec::with_capacity(nodes.len());
        let mut cursor = 0;
        for (i, node) in nodes.iter().enumerate() {
            let size = bucket + if (i as i32) < rem { 1 } else { 0 };
            list.push(ShardDescriptor {
                shard_id: format!("shard-{model_id}-{i}"),
                range_start: cursor,
                range_end: cursor + size,
                node_id: node.clone(),
            });
            cursor += size;
        }
        list
    }
}

/// Policy-based cross-tier offload — offload when the prompt exceeds the local
/// ceiling. Mirrors `PolicyCrossTierOffload`.
pub struct PolicyCrossTierOffload {
    local_prompt_ceiling: i32,
    farm_target_node: Option<String>,
}

impl PolicyCrossTierOffload {
    /// Constructs the policy. `local_prompt_ceiling` must be > 0 (default 2048).
    pub fn new(local_prompt_ceiling: i32, farm_target_node: Option<String>) -> Self {
        assert!(local_prompt_ceiling > 0, "localPromptCeiling out of range");
        Self {
            local_prompt_ceiling,
            farm_target_node,
        }
    }

    /// Constructs the policy with the default ceiling (2048) and no target node.
    pub fn default_policy() -> Self {
        Self::new(2048, None)
    }
}

impl ICrossTierOffload for PolicyCrossTierOffload {
    fn backend_id(&self) -> &str {
        "policy"
    }

    fn should_offload(
        &self,
        model_id: &str,
        prompt_tokens: i32,
        caller_tier: ServerTier,
    ) -> OffloadDecision {
        assert!(!model_id.trim().is_empty(), "modelId required");
        assert!(prompt_tokens >= 0, "promptTokens out of range");
        if caller_tier == ServerTier::ServerFarm {
            return OffloadDecision {
                should_offload: false,
                target_node_id: None,
                reason: Some("Caller is already top-tier".to_string()),
            };
        }
        if prompt_tokens <= self.local_prompt_ceiling {
            return OffloadDecision {
                should_offload: false,
                target_node_id: None,
                reason: Some("Prompt fits locally".to_string()),
            };
        }
        OffloadDecision {
            should_offload: true,
            target_node_id: self.farm_target_node.clone(),
            reason: Some(format!(
                "Prompt exceeds local ceiling ({} tokens)",
                self.local_prompt_ceiling
            )),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Single-node null defaults
// ─────────────────────────────────────────────────────────────────────────────

/// Null tenant router — no nodes, no quotas. Mirrors `NullTenantRouter`.
#[derive(Debug, Default, Clone)]
pub struct NullTenantRouter;

impl ITenantRouter for NullTenantRouter {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn choose_node(&self, _tenant: &TenantContext, _model_id: &str) -> Option<String> {
        None
    }
    fn set_quota(&self, _quota: TenantQuota) {}
    fn get_quota(&self, _tenant_id: &str) -> Option<TenantQuota> {
        None
    }
}

/// Null batch scheduler — hands back an empty-id slot with the requested wait.
/// Mirrors `NullBatchScheduler`.
#[derive(Debug, Default, Clone)]
pub struct NullBatchScheduler;

impl IBatchScheduler for NullBatchScheduler {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn reserve(
        &self,
        model_id: &str,
        estimated_tokens: i32,
        max_wait: chrono::Duration,
    ) -> BatchSlot {
        BatchSlot {
            slot_id: Uuid::nil().to_string(),
            model_id: model_id.to_string(),
            tokens: estimated_tokens,
            deadline_utc: Utc::now() + max_wait,
        }
    }
    fn release(&self, _slot: &BatchSlot) {}
}

/// Null shard planner — no shards. Mirrors `NullModelShardPlanner`.
#[derive(Debug, Default, Clone)]
pub struct NullModelShardPlanner;

impl IModelShardPlanner for NullModelShardPlanner {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn plan(&self, _model_id: &str, _param_bytes: i32) -> Vec<ShardDescriptor> {
        Vec::new()
    }
}

/// Null cross-tier offload — always local. Mirrors `NullCrossTierOffload`.
#[derive(Debug, Default, Clone)]
pub struct NullCrossTierOffload;

impl ICrossTierOffload for NullCrossTierOffload {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn should_offload(
        &self,
        _model_id: &str,
        _prompt_tokens: i32,
        _caller_tier: ServerTier,
    ) -> OffloadDecision {
        OffloadDecision {
            should_offload: false,
            target_node_id: None,
            reason: Some("Local execution; no cross-tier offload configured.".to_string()),
        }
    }
}
