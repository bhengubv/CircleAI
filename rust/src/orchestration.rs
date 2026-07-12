//! orchestration.rs
//!
//! Port of `CircleAI.Orchestration/` — the host-side agent-swarm orchestrator.
//!
//!   * [`AgentRole`] / [`AgentPriority`] / [`AgentStatus`] — the swarm enums.
//!   * [`AgentTask`] — one unit of work; [`AgentTask::create`] stamps a fresh id + time.
//!   * [`SwarmResult`] — an agent handler's outcome for one task.
//!   * [`QualityGateResult`] — the gate verdict (`passed` + blockers/warnings).
//!   * [`AgentSwarmConfig`] — concurrency / timeout / gate-enforcement knobs.
//!   * [`IAgentDispatcher`] + [`LocalAgentDispatcher`] — route tasks to per-role
//!     handler closures and evaluate the deterministic quality gate.
//!   * [`LokiOrchestrator`] — the semaphore-bounded swarm runner + quality gate.
//!     `RunSwarmAsync` (an `IAsyncEnumerable` in C#) is realised as
//!     [`LokiOrchestrator::run_swarm`], which returns the per-task results in
//!     completion order after enforcing the gate.
//!   * [`IncidentTrigger`] — maps an [`EpisodicMemoryEntry`] or an
//!     [`AnomalySignal`] into the agent tasks that should fire.
//!   * [`SecurityOrchestrationBridge`] — wraps an [`ISecurityWatchdog`] so every
//!     anomaly also dispatches an ops-security task (runtime response never blocks
//!     on the swarm).
//!
//! C# async maps to `#[async_trait]`. The C# `SemaphoreSlim` + per-task
//! `CancellationTokenSource.CancelAfter` maps to a `tokio::sync::Semaphore` +
//! `tokio::time::timeout`. Handlers are `Arc`-shared boxed async closures.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::memory::EpisodicMemoryEntry;
use crate::security::{
    AnomalySignal, ISecurityWatchdog, SecurityCheckpoint, SecurityResponse, ThreatVector,
};

// ─────────────────────────────────────────────────────────────────────────────
// AgentRole / AgentPriority / AgentStatus
// ─────────────────────────────────────────────────────────────────────────────

/// Categorises the domain responsibility of an agent in a swarm.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AgentRole {
    /// Responsible for writing, reviewing, and fixing code.
    Engineering,
    /// Responsible for infrastructure, deployments, and incident response.
    Operations,
    /// Responsible for quality review, testing, and acceptance criteria.
    Review,
    /// Responsible for security analysis and vulnerability assessment.
    Security,
}

/// Execution priority of an agent task. Lower numeric value = higher urgency.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
#[repr(u8)]
pub enum AgentPriority {
    /// Immediate — blocks all other work until resolved.
    Critical = 0,
    /// Urgent — should be addressed in the current session.
    High = 1,
    /// Standard — processed in arrival order.
    Normal = 2,
    /// Best-effort — processed only when no higher-priority work is pending.
    Low = 3,
}

/// Lifecycle status of an agent task or swarm result.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AgentStatus {
    /// Task has been created but not yet dispatched.
    Pending,
    /// Task is currently being executed by a handler.
    Running,
    /// Task completed and all quality gates passed.
    Passed,
    /// Task completed but produced an error or exception.
    Failed,
    /// Task was halted by a quality gate or missing handler.
    Blocked,
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentTask
// ─────────────────────────────────────────────────────────────────────────────

/// A single unit of work dispatched to an agent swarm. 1:1 with the C#
/// `sealed record AgentTask`.
#[derive(Debug, Clone)]
pub struct AgentTask {
    /// Stable unique identifier for this task.
    pub id: Uuid,
    /// The agent domain responsible for handling the task.
    pub role: AgentRole,
    /// Human-readable description of the work to be performed.
    pub description: String,
    /// Execution urgency; lower numeric value = higher urgency.
    pub priority: AgentPriority,
    /// Arbitrary key-value inputs provided to the agent handler.
    pub inputs: HashMap<String, String>,
    /// UTC timestamp at which the task was created.
    pub created_at: DateTime<Utc>,
}

impl AgentTask {
    /// Factory that stamps a new [`AgentTask`] with a fresh id and `Utc::now()`.
    /// Pass `None` for an empty input set.
    pub fn create(
        role: AgentRole,
        description: impl Into<String>,
        priority: AgentPriority,
        inputs: Option<HashMap<String, String>>,
    ) -> Self {
        Self {
            id: Uuid::new_v4(),
            role,
            description: description.into(),
            priority,
            inputs: inputs.unwrap_or_default(),
            created_at: Utc::now(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SwarmResult / QualityGateResult
// ─────────────────────────────────────────────────────────────────────────────

/// The outcome produced by an agent handler for a single [`AgentTask`].
#[derive(Debug, Clone)]
pub struct SwarmResult {
    /// The [`AgentTask::id`] this result belongs to.
    pub task_id: Uuid,
    /// The [`AgentRole`] that produced this result.
    pub role: AgentRole,
    /// Final lifecycle status of the task.
    pub status: AgentStatus,
    /// Human-readable output produced by the agent.
    pub output: String,
    /// Zero or more issue strings. Prefix with `[CRITICAL]` or `[HIGH]` to
    /// trigger quality-gate blocking; any other prefix is treated as a warning.
    pub issues: Vec<String>,
    /// UTC timestamp at which the handler returned.
    pub completed_at: DateTime<Utc>,
}

impl SwarmResult {
    /// Creates a new [`SwarmResult`].
    pub fn new(
        task_id: Uuid,
        role: AgentRole,
        status: AgentStatus,
        output: impl Into<String>,
        issues: Vec<String>,
    ) -> Self {
        Self {
            task_id,
            role,
            status,
            output: output.into(),
            issues,
            completed_at: Utc::now(),
        }
    }
}

/// The verdict produced by [`IAgentDispatcher::run_quality_gate`] after
/// evaluating a [`SwarmResult`].
#[derive(Debug, Clone)]
pub struct QualityGateResult {
    /// `true` when there are no [`QualityGateResult::blockers`].
    pub passed: bool,
    /// Critical or high-severity issues that must be resolved before deploy.
    pub blockers: Vec<String>,
    /// Low-severity or cosmetic issues surfaced for visibility only.
    pub warnings: Vec<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentSwarmConfig
// ─────────────────────────────────────────────────────────────────────────────

/// Tuning parameters that govern how [`LokiOrchestrator`] schedules and enforces
/// quality gates across a swarm. 1:1 with the C# `sealed record AgentSwarmConfig`.
#[derive(Debug, Clone, Copy)]
pub struct AgentSwarmConfig {
    /// Maximum number of tasks that may execute simultaneously. Defaults to `4`.
    pub max_concurrency: usize,
    /// Maximum wall-clock time allowed for a single task before it is cancelled
    /// and marked [`AgentStatus::Failed`]. Defaults to 5 minutes.
    pub task_timeout: Duration,
    /// When `true`, a failed [`AgentRole::Review`] gate blocks downstream deploy.
    pub require_review_pass_before_deploy: bool,
    /// When `true`, a failed [`AgentRole::Security`] gate blocks downstream deploy.
    pub require_security_pass_before_deploy: bool,
}

impl AgentSwarmConfig {
    /// Production-safe defaults: 4 concurrent tasks, 5-minute timeout, both
    /// review and security gates enforced.
    pub fn default_config() -> Self {
        Self {
            max_concurrency: 4,
            task_timeout: Duration::from_secs(5 * 60),
            require_review_pass_before_deploy: true,
            require_security_pass_before_deploy: true,
        }
    }
}

impl Default for AgentSwarmConfig {
    fn default() -> Self {
        Self::default_config()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IAgentDispatcher / LocalAgentDispatcher
// ─────────────────────────────────────────────────────────────────────────────

/// Boxed async handler closure for one [`AgentRole`]. Receives the task and
/// returns its [`SwarmResult`]. `Send + Sync` so the swarm can spawn it.
pub type AgentHandler = Arc<
    dyn Fn(
            AgentTask,
        ) -> std::pin::Pin<Box<dyn std::future::Future<Output = SwarmResult> + Send>>
        + Send
        + Sync,
>;

/// Routes agent tasks to their handlers and evaluates quality gates on results.
#[async_trait]
pub trait IAgentDispatcher: Send + Sync {
    /// Dispatches `task` to the appropriate agent handler and returns the result
    /// once the handler completes.
    async fn dispatch(&self, task: AgentTask) -> SwarmResult;

    /// Evaluates the quality of a completed [`SwarmResult`] and determines whether
    /// it passes the deployment gate.
    async fn run_quality_gate(&self, result: &SwarmResult) -> QualityGateResult;
}

/// In-process agent dispatcher. Routes tasks to handler closures registered per
/// [`AgentRole`]. No external network calls. Tasks dispatched to roles without a
/// registered handler return [`AgentStatus::Blocked`] immediately.
#[derive(Default)]
pub struct LocalAgentDispatcher {
    handlers: std::sync::Mutex<HashMap<AgentRole, AgentHandler>>,
}

impl LocalAgentDispatcher {
    /// Creates a dispatcher with no handlers registered.
    pub fn new() -> Self {
        Self {
            handlers: std::sync::Mutex::new(HashMap::new()),
        }
    }

    /// Registers an async handler closure for the given `role`, replacing any
    /// previously registered handler for that role.
    pub fn register_handler(&self, role: AgentRole, handler: AgentHandler) {
        self.handlers.lock().unwrap().insert(role, handler);
    }
}

#[async_trait]
impl IAgentDispatcher for LocalAgentDispatcher {
    async fn dispatch(&self, task: AgentTask) -> SwarmResult {
        let handler = self.handlers.lock().unwrap().get(&task.role).cloned();
        if let Some(handler) = handler {
            return handler(task).await;
        }

        // No handler registered — surface a blocked result with an actionable message.
        SwarmResult::new(
            task.id,
            task.role,
            AgentStatus::Blocked,
            format!("No handler registered for role {:?}.", task.role),
            vec![format!(
                "Register a handler for AgentRole::{:?} before dispatching.",
                task.role
            )],
        )
    }

    /// Deterministic gate: any issue prefixed with `[CRITICAL]` or `[HIGH]`
    /// (case-insensitive) is a blocker; all other issues are demoted to warnings.
    async fn run_quality_gate(&self, result: &SwarmResult) -> QualityGateResult {
        let is_blocker = |i: &String| {
            let upper = i.to_ascii_uppercase();
            upper.starts_with("[CRITICAL]") || upper.starts_with("[HIGH]")
        };

        let blockers: Vec<String> = result.issues.iter().filter(|i| is_blocker(i)).cloned().collect();
        let warnings: Vec<String> = result
            .issues
            .iter()
            .filter(|i| !is_blocker(i))
            .cloned()
            .collect();

        QualityGateResult {
            passed: blockers.is_empty(),
            blockers,
            warnings,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// LokiOrchestrator
// ─────────────────────────────────────────────────────────────────────────────

/// Host-side orchestrator. Accepts [`AgentTask`] items, dispatches them through
/// an [`IAgentDispatcher`] up to [`AgentSwarmConfig::max_concurrency`], enforces
/// the quality gate after each task, and returns the results.
pub struct LokiOrchestrator {
    dispatcher: Arc<dyn IAgentDispatcher>,
    config: AgentSwarmConfig,
}

impl LokiOrchestrator {
    /// Initialises a new orchestrator with the given dispatcher and optional
    /// configuration (defaults to [`AgentSwarmConfig::default_config`]).
    pub fn new(dispatcher: Arc<dyn IAgentDispatcher>, config: Option<AgentSwarmConfig>) -> Self {
        Self {
            dispatcher,
            config: config.unwrap_or_default(),
        }
    }

    /// Runs a swarm of tasks concurrently up to
    /// [`AgentSwarmConfig::max_concurrency`]. For each completed task the quality
    /// gate is evaluated; gate failures are re-emitted as [`AgentStatus::Blocked`]
    /// results with the gate's blocker messages appended to
    /// [`SwarmResult::issues`]. Results are returned in completion (submission)
    /// order — the C# `IAsyncEnumerable` is materialised into a `Vec`.
    pub async fn run_swarm(&self, tasks: impl IntoIterator<Item = AgentTask>) -> Vec<SwarmResult> {
        let semaphore = Arc::new(tokio::sync::Semaphore::new(self.config.max_concurrency));
        let pending: Vec<AgentTask> = tasks.into_iter().collect();
        let mut running = Vec::with_capacity(pending.len());

        for task in pending {
            // Bound the number of in-flight tasks — acquire before spawning.
            let permit = semaphore
                .clone()
                .acquire_owned()
                .await
                .expect("swarm semaphore closed unexpectedly");
            let dispatcher = self.dispatcher.clone();
            let timeout = self.config.task_timeout;
            running.push(tokio::spawn(async move {
                let result = run_one(dispatcher, task, timeout).await;
                drop(permit);
                result
            }));
        }

        let mut out = Vec::with_capacity(running.len());
        for handle in running {
            // A JoinError (panic in the spawned task) is unexpected; propagate a
            // failed result rather than aborting the whole swarm.
            let result = match handle.await {
                Ok(r) => r,
                Err(join_err) => {
                    // We lost the task identity on panic — emit a synthetic failure.
                    SwarmResult::new(
                        Uuid::nil(),
                        AgentRole::Operations,
                        AgentStatus::Failed,
                        format!("Swarm task panicked: {join_err}"),
                        vec![format!("[HIGH] Swarm task panicked: {join_err}")],
                    )
                }
            };

            let gate = self.dispatcher.run_quality_gate(&result).await;

            if !gate.passed
                && (self.config.require_review_pass_before_deploy
                    || self.config.require_security_pass_before_deploy)
            {
                let mut issues = result.issues.clone();
                issues.extend(gate.blockers);
                out.push(SwarmResult {
                    status: AgentStatus::Blocked,
                    issues,
                    ..result
                });
            } else {
                out.push(result);
            }
        }

        out
    }
}

/// Dispatches a single task with a wall-clock timeout, converting a timeout or a
/// handler error into a failed [`SwarmResult`] so the rest of the swarm still
/// surfaces to the caller. Mirrors `LokiOrchestrator.RunOneAsync`.
async fn run_one(
    dispatcher: Arc<dyn IAgentDispatcher>,
    task: AgentTask,
    timeout: Duration,
) -> SwarmResult {
    let task_id = task.id;
    let role = task.role;
    match tokio::time::timeout(timeout, dispatcher.dispatch(task)).await {
        Ok(result) => result,
        Err(_elapsed) => SwarmResult::new(
            task_id,
            role,
            AgentStatus::Failed,
            "Task timed out.",
            vec!["[HIGH] Task exceeded configured timeout.".to_string()],
        ),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IncidentTrigger
// ─────────────────────────────────────────────────────────────────────────────

/// Maps a recorded [`EpisodicMemoryEntry`] or an [`AnomalySignal`] into the agent
/// tasks that should be triggered. All-static; mirrors the C# static class.
pub struct IncidentTrigger;

impl IncidentTrigger {
    /// Tag keys that identify an entry as a crash / unhandled-error incident.
    fn crash_tags() -> HashSet<&'static str> {
        ["crash", "exception", "unhandled_error", "oom", "null_reference"]
            .into_iter()
            .collect()
    }

    /// Tag keys that, in addition to a crash signal, warrant a security investigation.
    fn security_tags() -> HashSet<&'static str> {
        ["auth_failure", "permission_denied", "token_expired", "injection", "overflow"]
            .into_iter()
            .collect()
    }

    /// Inspects an episodic memory entry and returns the agent tasks that should
    /// be triggered. Returns an empty vec when the entry is not an incident.
    ///
    /// Tag matching is case-insensitive, mirroring the C#
    /// `StringComparer.OrdinalIgnoreCase` sets.
    pub fn from_memory_entry(entry: &EpisodicMemoryEntry) -> Vec<AgentTask> {
        let empty = HashMap::new();
        let tags = entry.tags.as_ref().unwrap_or(&empty);

        let crash = Self::crash_tags();
        let is_crash = tags.keys().any(|k| crash.contains(k.to_ascii_lowercase().as_str()));
        if !is_crash {
            return Vec::new();
        }

        let mut tasks = Vec::new();

        // Always dispatch an ops-incident task for every crash entry.
        let mut ops_inputs = HashMap::new();
        ops_inputs.insert("episode_id".to_string(), entry.id.to_string());
        ops_inputs.insert("user_text".to_string(), entry.user_text.clone());
        ops_inputs.insert("assistant_text".to_string(), entry.assistant_text.clone());
        ops_inputs.insert(
            "app_context".to_string(),
            entry.app_context.clone().unwrap_or_default(),
        );
        tasks.push(AgentTask::create(
            AgentRole::Operations,
            format!(
                "ops-incident: diagnose crash recorded at {}",
                entry.recorded_at_utc.to_rfc3339()
            ),
            AgentPriority::High,
            Some(ops_inputs),
        ));

        // When security indicators are also present, escalate to a security agent.
        let sec = Self::security_tags();
        let is_security = tags.keys().any(|k| sec.contains(k.to_ascii_lowercase().as_str()));
        if is_security {
            let mut sec_inputs = HashMap::new();
            sec_inputs.insert("episode_id".to_string(), entry.id.to_string());
            sec_inputs.insert(
                "app_context".to_string(),
                entry.app_context.clone().unwrap_or_default(),
            );
            let mut keys: Vec<&String> = tags.keys().collect();
            keys.sort();
            sec_inputs.insert(
                "tags".to_string(),
                keys.iter().map(|k| k.as_str()).collect::<Vec<_>>().join(","),
            );
            tasks.push(AgentTask::create(
                AgentRole::Security,
                format!("ops-security: investigate security incident from episode {}", entry.id),
                AgentPriority::Critical,
                Some(sec_inputs),
            ));
        }

        tasks
    }

    /// Maps a confirmed [`AnomalySignal`] into an [`AgentTask`] for an ops-security
    /// agent. Returns `None` for signals below `dispatch_threshold` (default 0.30,
    /// matching the default watchdog's rotation threshold).
    pub fn from_anomaly_signal(signal: &AnomalySignal, dispatch_threshold: f32) -> Option<AgentTask> {
        if signal.confidence < dispatch_threshold {
            return None;
        }

        // Confidence drives priority — high-severity vectors are bumped one rank.
        let mut priority = if signal.confidence >= 0.85 {
            AgentPriority::Critical
        } else if signal.confidence >= 0.60 {
            AgentPriority::High
        } else {
            AgentPriority::Normal
        };

        let is_high_severity_vector = matches!(
            signal.vector,
            ThreatVector::ControlFlowDrift
                | ThreatVector::PrivilegeEscalation
                | ThreatVector::NetworkPivot
                | ThreatVector::StateCorruption
        );

        // priority ordering: Critical=0 < High=1 < Normal=2 < Low=3; "bumping one
        // rank" means decreasing the numeric value (never below Critical).
        if is_high_severity_vector && priority > AgentPriority::Critical {
            let bumped = std::cmp::max(AgentPriority::Critical as u8, (priority as u8).saturating_sub(1));
            priority = match bumped {
                0 => AgentPriority::Critical,
                1 => AgentPriority::High,
                2 => AgentPriority::Normal,
                _ => AgentPriority::Low,
            };
        }

        let mut inputs: HashMap<String, String> = signal.evidence.clone();
        inputs.insert("signal_id".to_string(), signal.id.to_string());
        inputs.insert("vector".to_string(), format!("{:?}", signal.vector));
        inputs.insert("confidence".to_string(), format!("{:.3}", signal.confidence));
        inputs.insert("affected_module".to_string(), signal.affected_module.clone());
        inputs.insert("description".to_string(), signal.description.clone());
        inputs.insert("detected_at".to_string(), signal.detected_at.to_rfc3339());

        let percent = (signal.confidence * 100.0).round() as i64;
        Some(AgentTask::create(
            AgentRole::Security,
            format!(
                "ops-security: anomaly {:?} in {} (confidence {}%)",
                signal.vector, signal.affected_module, percent
            ),
            priority,
            Some(inputs),
        ))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SecurityOrchestrationBridge
// ─────────────────────────────────────────────────────────────────────────────

/// Wraps an [`ISecurityWatchdog`] so that every anomaly signal also dispatches an
/// ops-security [`AgentTask`] to a [`LokiOrchestrator`]. The runtime response and
/// the agent dispatch proceed in parallel; neither blocks the other.
///
/// The C# reference fires the agent path fire-and-forget while awaiting only the
/// watchdog. The Rust port keeps the same contract: [`Self::on_anomaly_detected`]
/// runs the (synchronous, in-process) watchdog immediately, then spawns the agent
/// swarm so it never blocks the caller, and returns the runtime response.
pub struct SecurityOrchestrationBridge {
    inner: Arc<dyn ISecurityWatchdog>,
    orchestrator: Arc<LokiOrchestrator>,
    dispatch_threshold: f32,
}

impl SecurityOrchestrationBridge {
    /// Creates a bridge that delegates immune-system responses to `inner` and
    /// dispatches ops-security agents via `orchestrator`. `dispatch_threshold`
    /// defaults to 0.30 (matching the inner watchdog's rotation threshold).
    pub fn new(
        inner: Arc<dyn ISecurityWatchdog>,
        orchestrator: Arc<LokiOrchestrator>,
        dispatch_threshold: f32,
    ) -> Self {
        Self {
            inner,
            orchestrator,
            dispatch_threshold,
        }
    }

    /// Runs the immediate immune-system response, spawns the agent dispatch in
    /// parallel (fire-and-forget so the runtime response is never blocked by the
    /// swarm, which may take minutes), and returns the watchdog's response.
    pub fn on_anomaly_detected(
        &self,
        signal: &AnomalySignal,
        checkpoint: Option<&SecurityCheckpoint>,
    ) -> SecurityResponse {
        // Runtime response — fast path, in-process, returned to the caller.
        let response = self.inner.on_anomaly_detected(signal, checkpoint);

        // Agent path — spawned so it does not block the caller. Failures are
        // intentionally swallowed: agent failures must not crash the runtime.
        if let Some(task) = IncidentTrigger::from_anomaly_signal(signal, self.dispatch_threshold) {
            let orchestrator = self.orchestrator.clone();
            tokio::spawn(async move {
                // Drain the swarm — typically a single task -> single result.
                let _ = orchestrator.run_swarm([task]).await;
            });
        }

        response
    }

    /// Drains every [`AnomalySignal`] observed by the inner watchdog since the
    /// last drain (mirrors the C# `StreamSignalsAsync` delegation).
    pub fn stream_signals(&self) -> Vec<AnomalySignal> {
        self.inner.stream_signals()
    }
}
