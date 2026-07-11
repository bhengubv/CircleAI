//! workflows.rs
//!
//! Port of `CircleAI.Workflows/` — the durable-workflow contract surface plus
//! the `paca`-derived project/task/board/agent/doc/plugin/realtime/MCP/deploy
//! runtime.
//!
//! C# → Rust map:
//!   * `Contracts.cs` (durable workflows) → [`WorkflowPhase`], [`WorkflowDefinition`],
//!     [`WorkflowExecution`], [`CheckpointPayload`] + the `#[async_trait]`
//!     [`IWorkflowDefinitionStore`] / [`IWorkflowRunner`] / [`IWorkflowState`]
//!     (C# is `ValueTask`-based), each with an associated
//!     `Error: std::error::Error`, and the `Null*` fail-closed defaults.
//!   * `PacaProjects.cs` → [`PacaProject`] / [`PacaTask`] / [`InMemoryPacaStore`].
//!   * `PacaAgents.cs` → member/agent records + [`AgentTemplates`] + [`InMemoryPacaMemberStore`].
//!   * `PacaBoards.cs` → sprint/column/board records + [`PacaBoard`].
//!   * `PacaDocs.cs` → doc records + [`PacaDocService`].
//!   * `PacaConversations.cs` → conversation state machine + [`IConversationExecutor`].
//!   * `PacaRealtime.cs` → realtime event union + [`PacaRealtimeHub`] + [`IRealtimeBroadcaster`].
//!   * `PacaMcp.cs` → MCP server + [`PacaCoreMcpTools`].
//!   * `PacaPlugins.cs` → plugin lifecycle + [`PacaPluginRegistry`] + [`IPluginRuntimeHost`].
//!   * `PacaSkills.cs` → [`PacaSkill`] / [`PacaSkillLibrary`] / [`SkillTemplates`] / [`PacaSkillInstaller`].
//!   * `PacaAuth.cs` → [`HmacJwtAuthenticator`] / [`PacaApiKeyAuthenticator`].
//!   * `PacaDeploy.cs` → [`PacaDeployer`] (compose/env/script generation).
//!
//! Notes on constructs that did not map 1:1:
//!   * `decimal` money is absent here. `DateTimeOffset`/`TimeSpan` →
//!     `chrono::DateTime<Utc>`/`chrono::Duration`.
//!   * `ConcurrentDictionary` → `Mutex<HashMap>` (the sync stores) — the async
//!     runner surface is trait-only so it stays lock-agnostic.
//!   * `PacaConversationRuntime` in the C# spawns background `Task`s that emit
//!     step callbacks; the Rust [`PacaConversationRuntime`] keeps the identical
//!     state machine but the executor is invoked inline via `#[async_trait]`
//!     [`IConversationExecutor`] (the boundary a host wires to Docker/OpenHands).
//!     Cancellation is expressed as an [`Aborted`](ConversationError::Aborted)
//!     result rather than a `CancellationTokenSource`.
//!   * `PacaSkillInstaller` uses real filesystem IO (`Directory`/`File`), ported
//!     directly onto `std::fs`.
//!   * `HmacJwtAuthenticator`/`PacaApiKeyAuthenticator` use `HMACSHA256` /
//!     `SHA256` / `RandomNumberGenerator` from `System.Security.Cryptography`.
//!     The crate carries no crypto dependency, so — exactly like the Distribution
//!     hashing submodule — a self-contained SHA-256 + HMAC-SHA256 is ported
//!     verbatim into the private [`crypto`] submodule, and the random-secret
//!     source is the injectable [`ISecureRandom`] trait (shipped default
//!     [`CounterSecureRandom`] is deterministic; a host wires an OS CSPRNG).

use std::collections::HashMap;
use std::convert::Infallible;
use std::fmt;
use std::fs;
use std::path::Path;
use std::sync::{Mutex, OnceLock};

use async_trait::async_trait;
use chrono::{DateTime, Duration, Utc};
use regex::Regex;

// ═════════════════════════════════════════════════════════════════════════════
// Durable workflow contracts (Contracts.cs + NullImplementations.cs)
// ═════════════════════════════════════════════════════════════════════════════

/// Lifecycle phase of a durable workflow run.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WorkflowPhase {
    Pending,
    Running,
    Suspended,
    Completed,
    Failed,
}

/// A registered workflow definition.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkflowDefinition {
    pub definition_id: String,
    pub name: String,
    pub version: String,
    pub description: String,
}

/// One execution of a workflow definition.
#[derive(Debug, Clone, PartialEq)]
pub struct WorkflowExecution {
    pub run_id: String,
    pub definition_id: String,
    pub phase: WorkflowPhase,
    pub start_utc: DateTime<Utc>,
    pub failure_reason: Option<String>,
}

/// A durable checkpoint of one step's state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckpointPayload {
    pub run_id: String,
    pub step_id: String,
    pub state_blob: Vec<u8>,
}

/// Persists workflow definitions.
#[async_trait]
pub trait IWorkflowDefinitionStore {
    type Error: std::error::Error;
    fn backend_id(&self) -> &str;
    async fn upsert(&self, d: WorkflowDefinition) -> Result<(), Self::Error>;
    async fn get(&self, id: &str) -> Result<Option<WorkflowDefinition>, Self::Error>;
}

/// Starts / queries / cancels workflow runs.
#[async_trait]
pub trait IWorkflowRunner {
    type Error: std::error::Error;
    fn backend_id(&self) -> &str;
    async fn start(
        &self,
        definition_id: &str,
        inputs: Option<HashMap<String, String>>,
    ) -> Result<WorkflowExecution, Self::Error>;
    async fn get(&self, run_id: &str) -> Result<Option<WorkflowExecution>, Self::Error>;
    async fn cancel(&self, run_id: &str) -> Result<(), Self::Error>;
}

/// Durable per-step checkpoint store.
#[async_trait]
pub trait IWorkflowState {
    type Error: std::error::Error;
    fn backend_id(&self) -> &str;
    async fn checkpoint(&self, payload: CheckpointPayload) -> Result<(), Self::Error>;
    async fn load(&self, run_id: &str, step_id: &str) -> Result<Option<CheckpointPayload>, Self::Error>;
}

/// Fail-closed definition store — accepts writes silently, returns nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullWorkflowDefinitionStore;

#[async_trait]
impl IWorkflowDefinitionStore for NullWorkflowDefinitionStore {
    type Error = Infallible;
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn upsert(&self, _d: WorkflowDefinition) -> Result<(), Infallible> {
        Ok(())
    }
    async fn get(&self, _id: &str) -> Result<Option<WorkflowDefinition>, Infallible> {
        Ok(None)
    }
}

/// Fail-closed runner — every start immediately fails.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullWorkflowRunner;

#[async_trait]
impl IWorkflowRunner for NullWorkflowRunner {
    type Error = Infallible;
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn start(
        &self,
        definition_id: &str,
        _inputs: Option<HashMap<String, String>>,
    ) -> Result<WorkflowExecution, Infallible> {
        Ok(WorkflowExecution {
            run_id: uuid::Uuid::nil().to_string(),
            definition_id: definition_id.to_owned(),
            phase: WorkflowPhase::Failed,
            start_utc: DateTime::<Utc>::MIN_UTC,
            failure_reason: Some("NullWorkflowRunner".to_owned()),
        })
    }
    async fn get(&self, _run_id: &str) -> Result<Option<WorkflowExecution>, Infallible> {
        Ok(None)
    }
    async fn cancel(&self, _run_id: &str) -> Result<(), Infallible> {
        Ok(())
    }
}

/// Fail-closed checkpoint store — accepts writes silently, loads nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullWorkflowState;

#[async_trait]
impl IWorkflowState for NullWorkflowState {
    type Error = Infallible;
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn checkpoint(&self, _p: CheckpointPayload) -> Result<(), Infallible> {
        Ok(())
    }
    async fn load(&self, _run_id: &str, _step_id: &str) -> Result<Option<CheckpointPayload>, Infallible> {
        Ok(None)
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// WorkflowError — the shared failure surface for the paca in-memory runtime.
// ═════════════════════════════════════════════════════════════════════════════

/// Failure surface for the paca stores + auth. Covers the C#
/// `ArgumentException`/`InvalidOperationException` guard rails plus filesystem
/// IO for the skill installer.
#[derive(Debug)]
pub enum WorkflowError {
    /// A required argument was null / empty / whitespace, or otherwise invalid.
    InvalidArgument(String),
    /// The requested entity was not found or is in the wrong state.
    InvalidOperation(String),
    /// Underlying filesystem failure (skill installer).
    Io(std::io::Error),
}

impl fmt::Display for WorkflowError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            WorkflowError::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            WorkflowError::InvalidOperation(m) => write!(f, "invalid operation: {m}"),
            WorkflowError::Io(e) => write!(f, "io error: {e}"),
        }
    }
}

impl std::error::Error for WorkflowError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            WorkflowError::Io(e) => Some(e),
            _ => None,
        }
    }
}

impl From<std::io::Error> for WorkflowError {
    fn from(e: std::io::Error) -> Self {
        WorkflowError::Io(e)
    }
}

fn require_non_blank(value: &str, name: &str) -> Result<(), WorkflowError> {
    if value.trim().is_empty() {
        Err(WorkflowError::InvalidArgument(format!("{name} required")))
    } else {
        Ok(())
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaProjects.cs — projects + tasks + InMemoryPacaStore
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) A workspace that contains tasks.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PacaProject {
    pub id: String,
    pub name: String,
    pub prefix: String,
    pub settings_json: String,
    pub created_at_utc: DateTime<Utc>,
    pub deleted_at_utc: Option<DateTime<Utc>>,
}

/// (3.3.0) A unit of work inside a project.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PacaTask {
    pub project_id: String,
    pub number: i32,
    pub title: String,
    pub description_json: String,
    pub status: String,
    pub created_at_utc: DateTime<Utc>,
    pub deleted_at_utc: Option<DateTime<Utc>>,
}

impl PacaTask {
    /// The `PREFIX-N` reference string.
    pub fn reference(&self, prefix: &str) -> String {
        format!("{prefix}-{}", self.number)
    }
}

struct PacaStoreInner {
    projects: HashMap<String, PacaProject>,
    tasks_by_project: HashMap<String, Vec<PacaTask>>,
    next_number: HashMap<String, i32>,
}

/// (3.3.0) In-memory project + task store. Replace for production storage.
pub struct InMemoryPacaStore {
    inner: Mutex<PacaStoreInner>,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl InMemoryPacaStore {
    /// Creates a store with the system UTC clock.
    pub fn new() -> Self {
        Self::with_clock(Box::new(Utc::now))
    }

    /// Creates a store with an injected clock (mirrors the C# `Func<DateTimeOffset>`).
    pub fn with_clock(clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self {
            inner: Mutex::new(PacaStoreInner {
                projects: HashMap::new(),
                tasks_by_project: HashMap::new(),
                next_number: HashMap::new(),
            }),
            clock,
        }
    }

    /// (3.3.0) Create a new project. Errors if the id already exists.
    pub fn create_project(
        &self,
        id: &str,
        name: &str,
        prefix: &str,
        settings_json: Option<&str>,
    ) -> Result<PacaProject, WorkflowError> {
        require_non_blank(id, "id")?;
        require_non_blank(name, "name")?;
        require_non_blank(prefix, "prefix")?;

        let project = PacaProject {
            id: id.to_owned(),
            name: name.to_owned(),
            prefix: prefix.to_owned(),
            settings_json: settings_json.unwrap_or("{}").to_owned(),
            created_at_utc: (self.clock)(),
            deleted_at_utc: None,
        };

        let mut inner = self.inner.lock().unwrap();
        if inner.projects.contains_key(id) {
            return Err(WorkflowError::InvalidOperation(format!(
                "Project '{id}' already exists."
            )));
        }
        inner.projects.insert(id.to_owned(), project.clone());
        inner.tasks_by_project.insert(id.to_owned(), Vec::new());
        inner.next_number.insert(id.to_owned(), 1);
        Ok(project)
    }

    /// (3.3.0) Get a live project by id (excludes soft-deleted).
    pub fn get_project(&self, id: &str) -> Option<PacaProject> {
        let inner = self.inner.lock().unwrap();
        inner
            .projects
            .get(id)
            .filter(|p| p.deleted_at_utc.is_none())
            .cloned()
    }

    /// (3.3.0) Soft-delete a project. Idempotent.
    pub fn delete_project(&self, id: &str) {
        let now = (self.clock)();
        let mut inner = self.inner.lock().unwrap();
        if let Some(existing) = inner.projects.get_mut(id) {
            if existing.deleted_at_utc.is_none() {
                existing.deleted_at_utc = Some(now);
            }
        }
    }

    /// (3.3.0) Update the JSON settings bag on a project.
    pub fn update_project_settings(
        &self,
        project_id: &str,
        new_settings_json: &str,
    ) -> Result<PacaProject, WorkflowError> {
        let mut inner = self.inner.lock().unwrap();
        let existing = inner
            .projects
            .get_mut(project_id)
            .filter(|p| p.deleted_at_utc.is_none())
            .ok_or_else(|| {
                WorkflowError::InvalidOperation(format!("Project '{project_id}' not found."))
            })?;
        existing.settings_json = if new_settings_json.is_empty() {
            "{}".to_owned()
        } else {
            new_settings_json.to_owned()
        };
        Ok(existing.clone())
    }

    /// (3.3.0) Add a task to a project. Auto-numbers it.
    pub fn add_task(
        &self,
        project_id: &str,
        title: &str,
        description_json: Option<&str>,
        status: &str,
    ) -> Result<PacaTask, WorkflowError> {
        let now = (self.clock)();
        let mut inner = self.inner.lock().unwrap();
        let is_live = inner
            .projects
            .get(project_id)
            .map(|p| p.deleted_at_utc.is_none())
            .unwrap_or(false);
        if !is_live {
            return Err(WorkflowError::InvalidOperation(format!(
                "Project '{project_id}' not found."
            )));
        }
        let number = {
            let slot = inner.next_number.entry(project_id.to_owned()).or_insert(1);
            let n = *slot;
            *slot = n + 1;
            n
        };
        let task = PacaTask {
            project_id: project_id.to_owned(),
            number,
            title: title.to_owned(),
            description_json: if description_json.unwrap_or("").is_empty() {
                "{}".to_owned()
            } else {
                description_json.unwrap().to_owned()
            },
            status: if status.is_empty() {
                "todo".to_owned()
            } else {
                status.to_owned()
            },
            created_at_utc: now,
            deleted_at_utc: None,
        };
        inner
            .tasks_by_project
            .entry(project_id.to_owned())
            .or_default()
            .push(task.clone());
        Ok(task)
    }

    /// (3.3.0) List live tasks for a project, ordered by number ascending.
    pub fn list_tasks(&self, project_id: &str) -> Vec<PacaTask> {
        let inner = self.inner.lock().unwrap();
        let mut live: Vec<PacaTask> = match inner.tasks_by_project.get(project_id) {
            Some(list) => list
                .iter()
                .filter(|t| t.deleted_at_utc.is_none())
                .cloned()
                .collect(),
            None => Vec::new(),
        };
        live.sort_by_key(|t| t.number);
        live
    }

    /// (3.3.0) Find one task by reference like "PACA-3".
    pub fn get_task_by_reference(&self, project_id: &str, reference: &str) -> Option<PacaTask> {
        let inner = self.inner.lock().unwrap();
        let project = inner
            .projects
            .get(project_id)
            .filter(|p| p.deleted_at_utc.is_none())?;
        let expected_prefix = format!("{}-", project.prefix);
        if reference.len() < expected_prefix.len()
            || !reference[..expected_prefix.len()].eq_ignore_ascii_case(&expected_prefix)
        {
            return None;
        }
        let n: i32 = reference[expected_prefix.len()..].parse().ok()?;
        inner
            .tasks_by_project
            .get(project_id)?
            .iter()
            .find(|t| t.number == n && t.deleted_at_utc.is_none())
            .cloned()
    }

    /// (3.3.0) Update a task in place. Caller mutates via struct copy.
    pub fn update_task(&self, updated: PacaTask) {
        let mut inner = self.inner.lock().unwrap();
        if let Some(list) = inner.tasks_by_project.get_mut(&updated.project_id) {
            for slot in list.iter_mut() {
                if slot.number == updated.number {
                    *slot = updated;
                    return;
                }
            }
        }
    }

    /// (3.3.0) Soft-delete a task.
    pub fn delete_task(&self, project_id: &str, number: i32) {
        let now = (self.clock)();
        let mut inner = self.inner.lock().unwrap();
        if let Some(list) = inner.tasks_by_project.get_mut(project_id) {
            for slot in list.iter_mut() {
                if slot.number == number {
                    slot.deleted_at_utc = Some(now);
                    return;
                }
            }
        }
    }
}

impl Default for InMemoryPacaStore {
    fn default() -> Self {
        Self::new()
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaAgents.cs — members + agent profiles + templates + member store
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Member kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MemberKind {
    Human,
    Agent,
}

/// (3.3.0) Shared identity for humans + agents in a project.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProjectMember {
    pub id: String,
    pub project_id: String,
    pub kind: MemberKind,
    pub display_name: String,
    pub handle: String,
    pub role: String,
    pub avatar_url: Option<String>,
    pub created_at_utc: DateTime<Utc>,
    pub deleted_at_utc: Option<DateTime<Utc>>,
}

/// (3.3.0) Per-agent LLM config.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentLlmConfig {
    pub provider: String,
    pub model: String,
    pub api_key: Option<String>,
    pub base_address: Option<String>,
}

/// (3.3.0) Per-agent context-specific system prompts.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentSystemPrompts {
    pub task_prompt: Option<String>,
    pub doc_prompt: Option<String>,
    pub chat_prompt: Option<String>,
}

/// (3.3.0) Capability flags an agent is permitted to do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AgentCapabilities {
    pub can_clone_repos: bool,
    pub can_create_prs: bool,
    pub can_write_files: bool,
    pub can_call_external_tools: bool,
}

/// (3.3.0) Runtime limits an agent must respect.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AgentLimits {
    pub max_iterations: i32,
    pub timeout: Duration,
}

/// (3.3.0) Git identity an agent uses when committing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentGitIdentity {
    pub name: String,
    pub email: String,
}

/// (3.3.0) Trigger keywords that wake the agent for each event class.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentTriggers {
    pub task_created: Option<String>,
    pub chat_mention: Option<String>,
    pub doc_edit: Option<String>,
    pub direct_mention: Option<String>,
}

/// (3.3.0) Full agent profile.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentProfile {
    pub member_id: String,
    pub llm: AgentLlmConfig,
    pub prompts: AgentSystemPrompts,
    pub capabilities: AgentCapabilities,
    pub limits: AgentLimits,
    pub git_identity: AgentGitIdentity,
    pub triggers: AgentTriggers,
}

/// (3.3.0) Five preset agent templates from paca.
pub struct AgentTemplates;

impl AgentTemplates {
    pub fn development_agent(member_id: &str, api_key: &str, base_address: Option<&str>) -> AgentProfile {
        AgentProfile {
            member_id: member_id.to_owned(),
            llm: AgentLlmConfig {
                provider: "openai".into(),
                model: "gpt-4o-mini".into(),
                api_key: Some(api_key.to_owned()),
                base_address: base_address.map(str::to_owned),
            },
            prompts: AgentSystemPrompts {
                task_prompt: Some(
                    "You are a senior developer. Implement requested changes, write tests, open PRs."
                        .into(),
                ),
                doc_prompt: Some("You write engineering docs that are precise and example-driven.".into()),
                chat_prompt: Some("You answer engineering questions with concrete code samples.".into()),
            },
            capabilities: AgentCapabilities {
                can_clone_repos: true,
                can_create_prs: true,
                can_write_files: true,
                can_call_external_tools: true,
            },
            limits: AgentLimits {
                max_iterations: 25,
                timeout: Duration::minutes(10),
            },
            git_identity: AgentGitIdentity {
                name: "CircleAI Dev Agent".into(),
                email: "dev-agent@circleai.local".into(),
            },
            triggers: AgentTriggers {
                task_created: Some("dev".into()),
                chat_mention: Some("@dev".into()),
                doc_edit: None,
                direct_mention: Some("dev".into()),
            },
        }
    }

    pub fn product_manager_agent(member_id: &str, api_key: &str) -> AgentProfile {
        AgentProfile {
            member_id: member_id.to_owned(),
            llm: AgentLlmConfig {
                provider: "openai".into(),
                model: "gpt-4o-mini".into(),
                api_key: Some(api_key.to_owned()),
                base_address: None,
            },
            prompts: AgentSystemPrompts {
                task_prompt: Some(
                    "You are a product manager. Triage tasks, break them down, assign owners.".into(),
                ),
                doc_prompt: Some("You write product specs and PRDs.".into()),
                chat_prompt: Some("You answer product/priority questions.".into()),
            },
            capabilities: AgentCapabilities {
                can_clone_repos: false,
                can_create_prs: false,
                can_write_files: true,
                can_call_external_tools: true,
            },
            limits: AgentLimits {
                max_iterations: 15,
                timeout: Duration::minutes(5),
            },
            git_identity: AgentGitIdentity {
                name: "CircleAI PM Agent".into(),
                email: "pm-agent@circleai.local".into(),
            },
            triggers: AgentTriggers {
                task_created: Some("pm".into()),
                chat_mention: Some("@pm".into()),
                doc_edit: Some("@pm".into()),
                direct_mention: Some("pm".into()),
            },
        }
    }

    pub fn designer_agent(member_id: &str, api_key: &str) -> AgentProfile {
        AgentProfile {
            member_id: member_id.to_owned(),
            llm: AgentLlmConfig {
                provider: "openai".into(),
                model: "gpt-4o-mini".into(),
                api_key: Some(api_key.to_owned()),
                base_address: None,
            },
            prompts: AgentSystemPrompts {
                task_prompt: Some("You are a designer. Sketch UI ideas, write copy, propose flows.".into()),
                doc_prompt: Some("You write design memos.".into()),
                chat_prompt: Some("You answer design questions and propose concepts.".into()),
            },
            capabilities: AgentCapabilities {
                can_clone_repos: false,
                can_create_prs: false,
                can_write_files: true,
                can_call_external_tools: false,
            },
            limits: AgentLimits {
                max_iterations: 10,
                timeout: Duration::minutes(5),
            },
            git_identity: AgentGitIdentity {
                name: "CircleAI Design Agent".into(),
                email: "design-agent@circleai.local".into(),
            },
            triggers: AgentTriggers {
                task_created: Some("design".into()),
                chat_mention: Some("@design".into()),
                doc_edit: Some("@design".into()),
                direct_mention: Some("design".into()),
            },
        }
    }

    pub fn qa_agent(member_id: &str, api_key: &str) -> AgentProfile {
        AgentProfile {
            member_id: member_id.to_owned(),
            llm: AgentLlmConfig {
                provider: "openai".into(),
                model: "gpt-4o-mini".into(),
                api_key: Some(api_key.to_owned()),
                base_address: None,
            },
            prompts: AgentSystemPrompts {
                task_prompt: Some(
                    "You are a QA engineer. Write test plans, generate test cases, validate against AC."
                        .into(),
                ),
                doc_prompt: Some("You write QA reports.".into()),
                chat_prompt: Some("You answer QA questions and propose test strategies.".into()),
            },
            capabilities: AgentCapabilities {
                can_clone_repos: true,
                can_create_prs: false,
                can_write_files: true,
                can_call_external_tools: true,
            },
            limits: AgentLimits {
                max_iterations: 20,
                timeout: Duration::minutes(7),
            },
            git_identity: AgentGitIdentity {
                name: "CircleAI QA Agent".into(),
                email: "qa-agent@circleai.local".into(),
            },
            triggers: AgentTriggers {
                task_created: Some("qa".into()),
                chat_mention: Some("@qa".into()),
                doc_edit: None,
                direct_mention: Some("qa".into()),
            },
        }
    }

    pub fn code_reviewer_agent(member_id: &str, api_key: &str) -> AgentProfile {
        AgentProfile {
            member_id: member_id.to_owned(),
            llm: AgentLlmConfig {
                provider: "openai".into(),
                model: "gpt-4o-mini".into(),
                api_key: Some(api_key.to_owned()),
                base_address: None,
            },
            prompts: AgentSystemPrompts {
                task_prompt: Some(
                    "You are a senior code reviewer. Comment for clarity, correctness, security.".into(),
                ),
                doc_prompt: Some("You write code review checklists.".into()),
                chat_prompt: Some("You answer questions about code patterns and best practices.".into()),
            },
            capabilities: AgentCapabilities {
                can_clone_repos: true,
                can_create_prs: false,
                can_write_files: false,
                can_call_external_tools: true,
            },
            limits: AgentLimits {
                max_iterations: 15,
                timeout: Duration::minutes(7),
            },
            git_identity: AgentGitIdentity {
                name: "CircleAI Reviewer Agent".into(),
                email: "reviewer-agent@circleai.local".into(),
            },
            triggers: AgentTriggers {
                task_created: None,
                chat_mention: Some("@review".into()),
                doc_edit: None,
                direct_mention: Some("review".into()),
            },
        }
    }

    /// The five preset names, in order.
    pub fn preset_names() -> Vec<&'static str> {
        vec!["development", "pm", "design", "qa", "review"]
    }
}

/// (3.3.0) In-memory store for members + agent profiles.
pub struct InMemoryPacaMemberStore {
    members: Mutex<HashMap<String, ProjectMember>>,
    profiles: Mutex<HashMap<String, AgentProfile>>,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl InMemoryPacaMemberStore {
    pub fn new() -> Self {
        Self::with_clock(Box::new(Utc::now))
    }

    pub fn with_clock(clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self {
            members: Mutex::new(HashMap::new()),
            profiles: Mutex::new(HashMap::new()),
            clock,
        }
    }

    pub fn add_human(
        &self,
        id: &str,
        project_id: &str,
        display_name: &str,
        handle: &str,
        role: &str,
        avatar: Option<&str>,
    ) -> Result<ProjectMember, WorkflowError> {
        self.add_member(id, project_id, MemberKind::Human, display_name, handle, role, avatar)
    }

    pub fn add_agent(
        &self,
        id: &str,
        project_id: &str,
        display_name: &str,
        handle: &str,
        profile: AgentProfile,
        avatar: Option<&str>,
    ) -> Result<ProjectMember, WorkflowError> {
        let member = self.add_member(id, project_id, MemberKind::Agent, display_name, handle, "agent", avatar)?;
        let mut p = profile;
        p.member_id = id.to_owned();
        self.profiles.lock().unwrap().insert(id.to_owned(), p);
        Ok(member)
    }

    fn add_member(
        &self,
        id: &str,
        project_id: &str,
        kind: MemberKind,
        display_name: &str,
        handle: &str,
        role: &str,
        avatar: Option<&str>,
    ) -> Result<ProjectMember, WorkflowError> {
        require_non_blank(id, "id")?;
        require_non_blank(project_id, "projectId")?;
        require_non_blank(display_name, "displayName")?;
        require_non_blank(handle, "handle")?;

        let member = ProjectMember {
            id: id.to_owned(),
            project_id: project_id.to_owned(),
            kind,
            display_name: display_name.to_owned(),
            handle: handle.to_owned(),
            role: role.to_owned(),
            avatar_url: avatar.map(str::to_owned),
            created_at_utc: (self.clock)(),
            deleted_at_utc: None,
        };
        let mut members = self.members.lock().unwrap();
        if members.contains_key(id) {
            return Err(WorkflowError::InvalidOperation(format!(
                "Member '{id}' already exists."
            )));
        }
        members.insert(id.to_owned(), member.clone());
        Ok(member)
    }

    pub fn get_member(&self, id: &str) -> Option<ProjectMember> {
        self.members
            .lock()
            .unwrap()
            .get(id)
            .filter(|m| m.deleted_at_utc.is_none())
            .cloned()
    }

    pub fn get_agent_profile(&self, member_id: &str) -> Option<AgentProfile> {
        self.profiles.lock().unwrap().get(member_id).cloned()
    }

    pub fn list_members(&self, project_id: &str, kind: Option<MemberKind>) -> Vec<ProjectMember> {
        let members = self.members.lock().unwrap();
        let mut list: Vec<ProjectMember> = members
            .values()
            .filter(|m| {
                m.project_id == project_id
                    && m.deleted_at_utc.is_none()
                    && kind.map(|k| m.kind == k).unwrap_or(true)
            })
            .cloned()
            .collect();
        list.sort_by(|a, b| a.display_name.cmp(&b.display_name));
        list
    }

    pub fn remove_member(&self, id: &str) {
        let now = (self.clock)();
        let mut members = self.members.lock().unwrap();
        if let Some(existing) = members.get_mut(id) {
            if existing.deleted_at_utc.is_none() {
                existing.deleted_at_utc = Some(now);
            }
        }
    }

    pub fn update_agent_profile(
        &self,
        member_id: &str,
        updated: AgentProfile,
    ) -> Result<AgentProfile, WorkflowError> {
        match self.get_member(member_id) {
            Some(m) if m.kind == MemberKind::Agent => {}
            _ => {
                return Err(WorkflowError::InvalidOperation(format!(
                    "Member '{member_id}' is not an agent."
                )))
            }
        }
        let mut p = updated;
        p.member_id = member_id.to_owned();
        self.profiles
            .lock()
            .unwrap()
            .insert(member_id.to_owned(), p.clone());
        Ok(p)
    }
}

impl Default for InMemoryPacaMemberStore {
    fn default() -> Self {
        Self::new()
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaBoards.cs — sprint / column / board metadata / views + PacaBoard
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Sprint lifecycle.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SprintState {
    Planning,
    Active,
    Completed,
}

/// (3.3.0) Status column in the workflow.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatusColumn {
    pub name: String,
    pub category: String,
    pub position: i32,
    pub collapsed: bool,
}

/// (3.3.0) Sprint.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PacaSprint {
    pub id: String,
    pub project_id: String,
    pub name: String,
    pub goal: String,
    pub start_date: DateTime<Utc>,
    pub end_date: DateTime<Utc>,
    pub state: SprintState,
}

/// (3.3.0) Extra board-only metadata on top of [`PacaTask`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskBoardMetadata {
    pub project_id: String,
    pub number: i32,
    pub story_points: i32,
    pub importance: i32,
    pub assignee_member_id: Option<String>,
    pub reporter_member_id: Option<String>,
    pub parent_task_number: Option<i32>,
    pub sprint_id: Option<String>,
    pub tags: Vec<String>,
    pub custom_fields: HashMap<String, String>,
    pub position_in_column: i32,
}

/// (3.3.0) A per-user / per-board "named view".
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BoardView {
    pub name: String,
    pub filter_tags_csv: Option<String>,
    pub filter_assignee: Option<String>,
    pub sort_by: Option<String>,
    pub sort_descending: bool,
    pub visible_columns: Vec<String>,
    pub visible_fields: Vec<String>,
}

struct BoardInner {
    columns: HashMap<String, StatusColumn>,
    sprints: HashMap<String, PacaSprint>,
    metadata: HashMap<(String, i32), TaskBoardMetadata>,
    views: HashMap<String, BoardView>,
}

/// (3.3.0) Board service over a project. Sprints + columns + per-task metadata +
/// views. Wraps a shared [`InMemoryPacaStore`].
pub struct PacaBoard<'a> {
    tasks: &'a InMemoryPacaStore,
    inner: Mutex<BoardInner>,
}

impl<'a> PacaBoard<'a> {
    /// Creates a board over the given task store, seeding the six default columns.
    pub fn new(tasks: &'a InMemoryPacaStore) -> Self {
        let mut columns = HashMap::new();
        columns.insert(
            "todo".into(),
            StatusColumn {
                name: "todo".into(),
                category: "open".into(),
                position: 0,
                collapsed: false,
            },
        );
        columns.insert(
            "in_progress".into(),
            StatusColumn {
                name: "in_progress".into(),
                category: "in-flight".into(),
                position: 1,
                collapsed: false,
            },
        );
        columns.insert(
            "in_review".into(),
            StatusColumn {
                name: "in_review".into(),
                category: "review".into(),
                position: 2,
                collapsed: false,
            },
        );
        columns.insert(
            "done".into(),
            StatusColumn {
                name: "done".into(),
                category: "closed".into(),
                position: 3,
                collapsed: false,
            },
        );
        columns.insert(
            "cancelled".into(),
            StatusColumn {
                name: "cancelled".into(),
                category: "cancelled".into(),
                position: 4,
                collapsed: false,
            },
        );
        columns.insert(
            "blocked".into(),
            StatusColumn {
                name: "blocked".into(),
                category: "blocked".into(),
                position: 5,
                collapsed: true,
            },
        );
        Self {
            tasks,
            inner: Mutex::new(BoardInner {
                columns,
                sprints: HashMap::new(),
                metadata: HashMap::new(),
                views: HashMap::new(),
            }),
        }
    }

    /// Columns ordered by position ascending.
    pub fn columns(&self) -> Vec<StatusColumn> {
        let inner = self.inner.lock().unwrap();
        let mut cols: Vec<StatusColumn> = inner.columns.values().cloned().collect();
        cols.sort_by_key(|c| c.position);
        cols
    }

    pub fn add_column(&self, col: StatusColumn) {
        self.inner.lock().unwrap().columns.insert(col.name.clone(), col);
    }

    pub fn collapse_column(&self, name: &str, collapsed: bool) {
        let mut inner = self.inner.lock().unwrap();
        if let Some(col) = inner.columns.get_mut(name) {
            col.collapsed = collapsed;
        }
    }

    /// (3.3.0) Move a task between status columns, updating its in-column position.
    pub fn move_task(
        &self,
        project_id: &str,
        number: i32,
        new_status: &str,
        new_position: i32,
    ) -> Result<(), WorkflowError> {
        let task = self
            .tasks
            .get_task_by_reference(project_id, &format!("{project_id}-{number}"))
            .or_else(|| {
                self.tasks
                    .list_tasks(project_id)
                    .into_iter()
                    .find(|t| t.number == number)
            })
            .ok_or_else(|| WorkflowError::InvalidOperation("Task not found.".into()))?;

        {
            let inner = self.inner.lock().unwrap();
            if !inner.columns.contains_key(new_status) {
                return Err(WorkflowError::InvalidArgument(format!(
                    "Unknown status '{new_status}'."
                )));
            }
        }

        self.tasks.update_task(PacaTask {
            status: new_status.to_owned(),
            ..task
        });

        let mut meta = self.get_or_create_metadata(project_id, number);
        meta.position_in_column = new_position;
        self.inner
            .lock()
            .unwrap()
            .metadata
            .insert((project_id.to_owned(), number), meta);
        Ok(())
    }

    /// (3.3.0) Attach board metadata to an existing task.
    pub fn set_task_metadata(&self, metadata: TaskBoardMetadata) {
        let key = (metadata.project_id.clone(), metadata.number);
        self.inner.lock().unwrap().metadata.insert(key, metadata);
    }

    pub fn get_task_metadata(&self, project_id: &str, number: i32) -> Option<TaskBoardMetadata> {
        self.inner
            .lock()
            .unwrap()
            .metadata
            .get(&(project_id.to_owned(), number))
            .cloned()
    }

    /// (3.3.0) Paginated column read for lazy loading.
    pub fn tasks_in_column(
        &self,
        project_id: &str,
        status: &str,
        skip: usize,
        take: usize,
    ) -> Vec<PacaTask> {
        let mut live: Vec<PacaTask> = self
            .tasks
            .list_tasks(project_id)
            .into_iter()
            .filter(|t| t.status == status)
            .collect();
        live.sort_by_key(|t| {
            self.get_or_create_metadata(&t.project_id, t.number)
                .position_in_column
        });
        live.into_iter().skip(skip).take(take).collect()
    }

    /// (3.3.0) Tasks bucketed by sprint, useful for the Scrumban board.
    pub fn tasks_in_sprint(&self, sprint_id: &str) -> Vec<PacaTask> {
        let metas: Vec<(String, i32)> = {
            let inner = self.inner.lock().unwrap();
            inner
                .metadata
                .values()
                .filter(|m| m.sprint_id.as_deref() == Some(sprint_id))
                .map(|m| (m.project_id.clone(), m.number))
                .collect()
        };
        metas
            .into_iter()
            .filter_map(|(pid, number)| {
                self.tasks
                    .list_tasks(&pid)
                    .into_iter()
                    .find(|t| t.number == number)
            })
            .collect()
    }

    /// (3.3.0) Create a sprint in Planning.
    pub fn create_sprint(
        &self,
        id: &str,
        project_id: &str,
        name: &str,
        goal: &str,
        start: DateTime<Utc>,
        end: DateTime<Utc>,
    ) -> PacaSprint {
        let s = PacaSprint {
            id: id.to_owned(),
            project_id: project_id.to_owned(),
            name: name.to_owned(),
            goal: goal.to_owned(),
            start_date: start,
            end_date: end,
            state: SprintState::Planning,
        };
        self.inner.lock().unwrap().sprints.insert(id.to_owned(), s.clone());
        s
    }

    pub fn get_sprint(&self, id: &str) -> Option<PacaSprint> {
        self.inner.lock().unwrap().sprints.get(id).cloned()
    }

    pub fn start_sprint(&self, id: &str) -> Result<PacaSprint, WorkflowError> {
        self.transition(id, SprintState::Active)
    }

    pub fn complete_sprint(&self, id: &str) -> Result<PacaSprint, WorkflowError> {
        self.transition(id, SprintState::Completed)
    }

    fn transition(&self, id: &str, to: SprintState) -> Result<PacaSprint, WorkflowError> {
        let mut inner = self.inner.lock().unwrap();
        let sprint = inner
            .sprints
            .get_mut(id)
            .ok_or_else(|| WorkflowError::InvalidOperation(format!("Sprint '{id}' not found.")))?;
        sprint.state = to;
        Ok(sprint.clone())
    }

    /// (3.3.0) Save a named view (filters + sort + visible fields).
    pub fn save_view(&self, view: BoardView) {
        self.inner.lock().unwrap().views.insert(view.name.clone(), view);
    }

    pub fn get_view(&self, name: &str) -> Option<BoardView> {
        self.inner.lock().unwrap().views.get(name).cloned()
    }

    pub fn list_views(&self) -> Vec<BoardView> {
        let inner = self.inner.lock().unwrap();
        let mut list: Vec<BoardView> = inner.views.values().cloned().collect();
        list.sort_by(|a, b| a.name.cmp(&b.name));
        list
    }

    fn get_or_create_metadata(&self, project_id: &str, number: i32) -> TaskBoardMetadata {
        let mut inner = self.inner.lock().unwrap();
        inner
            .metadata
            .entry((project_id.to_owned(), number))
            .or_insert_with(|| TaskBoardMetadata {
                project_id: project_id.to_owned(),
                number,
                story_points: 0,
                importance: 3,
                assignee_member_id: None,
                reporter_member_id: None,
                parent_task_number: None,
                sprint_id: None,
                tags: Vec::new(),
                custom_fields: HashMap::new(),
                position_in_column: 0,
            })
            .clone()
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaDocs.cs — living documents + versions + activity + links
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) A doc node (folder OR document).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DocNode {
    pub id: String,
    pub project_id: String,
    pub parent_id: Option<String>,
    pub is_folder: bool,
    pub title: String,
    pub content_json: String,
    pub created_at_utc: DateTime<Utc>,
    pub deleted_at_utc: Option<DateTime<Utc>>,
}

/// (3.3.0) One immutable snapshot of a doc.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DocVersion {
    pub version_id: String,
    pub doc_id: String,
    pub content_json: String,
    pub saved_at_utc: DateTime<Utc>,
    pub author_member_id: String,
}

/// (3.3.0) One document-activity event.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DocActivity {
    pub activity_id: String,
    pub doc_id: String,
    pub author_member_id: String,
    pub action: String,
    pub detail: Option<String>,
    pub at: DateTime<Utc>,
}

/// (3.3.0) Link between a doc section and a task / epic.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DocLink {
    pub link_id: String,
    pub doc_id: String,
    pub section_anchor: String,
    pub project_id: String,
    pub task_number: i32,
}

struct DocInner {
    nodes: HashMap<String, DocNode>,
    versions: HashMap<String, Vec<DocVersion>>,
    activity: HashMap<String, Vec<DocActivity>>,
    links: HashMap<String, Vec<DocLink>>,
}

/// (3.3.0) In-memory doc service.
pub struct PacaDocService {
    inner: Mutex<DocInner>,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
    mention_pattern: Regex,
}

impl PacaDocService {
    pub fn new() -> Self {
        Self::with_clock(Box::new(Utc::now))
    }

    pub fn with_clock(clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self {
            inner: Mutex::new(DocInner {
                nodes: HashMap::new(),
                versions: HashMap::new(),
                activity: HashMap::new(),
                links: HashMap::new(),
            }),
            clock,
            mention_pattern: Regex::new(r"@([a-zA-Z0-9_\-]+)").unwrap(),
        }
    }

    pub fn create_folder(
        &self,
        id: &str,
        project_id: &str,
        parent_id: Option<&str>,
        title: &str,
    ) -> Result<DocNode, WorkflowError> {
        self.create(id, project_id, parent_id, true, title, "{}", "system")
    }

    pub fn create_document(
        &self,
        id: &str,
        project_id: &str,
        parent_id: Option<&str>,
        title: &str,
        content_json: &str,
        author_member_id: &str,
    ) -> Result<DocNode, WorkflowError> {
        self.create(id, project_id, parent_id, false, title, content_json, author_member_id)
    }

    #[allow(clippy::too_many_arguments)]
    fn create(
        &self,
        id: &str,
        project_id: &str,
        parent_id: Option<&str>,
        is_folder: bool,
        title: &str,
        content_json: &str,
        author_member_id: &str,
    ) -> Result<DocNode, WorkflowError> {
        require_non_blank(id, "id")?;
        require_non_blank(project_id, "projectId")?;
        let now = (self.clock)();
        let node = DocNode {
            id: id.to_owned(),
            project_id: project_id.to_owned(),
            parent_id: parent_id.map(str::to_owned),
            is_folder,
            title: title.to_owned(),
            content_json: if content_json.is_empty() {
                "{}".to_owned()
            } else {
                content_json.to_owned()
            },
            created_at_utc: now,
            deleted_at_utc: None,
        };
        let mut inner = self.inner.lock().unwrap();
        if inner.nodes.contains_key(id) {
            return Err(WorkflowError::InvalidOperation(format!(
                "Doc '{id}' already exists."
            )));
        }
        inner.nodes.insert(id.to_owned(), node.clone());
        if !is_folder {
            inner.versions.insert(id.to_owned(), Vec::new());
            inner.activity.insert(
                id.to_owned(),
                vec![DocActivity {
                    activity_id: new_id(),
                    doc_id: id.to_owned(),
                    author_member_id: author_member_id.to_owned(),
                    action: "created".into(),
                    detail: None,
                    at: now,
                }],
            );
        }
        Ok(node)
    }

    pub fn get(&self, id: &str) -> Option<DocNode> {
        self.inner
            .lock()
            .unwrap()
            .nodes
            .get(id)
            .filter(|n| n.deleted_at_utc.is_none())
            .cloned()
    }

    pub fn list_children(&self, project_id: &str, parent_id: Option<&str>) -> Vec<DocNode> {
        let inner = self.inner.lock().unwrap();
        let mut list: Vec<DocNode> = inner
            .nodes
            .values()
            .filter(|n| {
                n.project_id == project_id
                    && n.parent_id.as_deref() == parent_id
                    && n.deleted_at_utc.is_none()
            })
            .cloned()
            .collect();
        list.sort_by(|a, b| a.title.cmp(&b.title));
        list
    }

    /// (3.3.0) Edit a document: writes a new version + activity entry, returns
    /// mentioned handles.
    pub fn edit(
        &self,
        id: &str,
        new_content_json: &str,
        author_member_id: &str,
        is_ai_edit: bool,
    ) -> Result<Vec<String>, WorkflowError> {
        let now = (self.clock)();
        let new_content = if new_content_json.is_empty() {
            "{}".to_owned()
        } else {
            new_content_json.to_owned()
        };
        let mut inner = self.inner.lock().unwrap();
        let node = inner.nodes.get(id).cloned();
        let node = match node {
            Some(n) if !n.is_folder && n.deleted_at_utc.is_none() => n,
            _ => {
                return Err(WorkflowError::InvalidOperation(format!(
                    "Doc '{id}' is not editable."
                )))
            }
        };
        let prior_content = node.content_json.clone();
        inner.nodes.insert(
            id.to_owned(),
            DocNode {
                content_json: new_content,
                ..node
            },
        );
        inner.versions.entry(id.to_owned()).or_default().push(DocVersion {
            version_id: new_id(),
            doc_id: id.to_owned(),
            content_json: prior_content,
            saved_at_utc: now,
            author_member_id: author_member_id.to_owned(),
        });
        inner.activity.entry(id.to_owned()).or_default().push(DocActivity {
            activity_id: new_id(),
            doc_id: id.to_owned(),
            author_member_id: author_member_id.to_owned(),
            action: if is_ai_edit { "ai-edited".into() } else { "edited".into() },
            detail: None,
            at: now,
        });
        Ok(self.extract_mentions(new_content_json))
    }

    pub fn versions(&self, doc_id: &str) -> Vec<DocVersion> {
        self.inner
            .lock()
            .unwrap()
            .versions
            .get(doc_id)
            .cloned()
            .unwrap_or_default()
    }

    /// (3.3.0) Cheap diff between two versions — returns added + removed text lines.
    pub fn diff_lines(&self, before: &str, after: &str) -> (Vec<String>, Vec<String>) {
        use std::collections::HashSet;
        let b: HashSet<&str> = before.split('\n').collect();
        let a: HashSet<&str> = after.split('\n').collect();
        let added: Vec<String> = a.difference(&b).map(|s| s.to_string()).collect();
        let removed: Vec<String> = b.difference(&a).map(|s| s.to_string()).collect();
        (added, removed)
    }

    pub fn activity(&self, doc_id: &str) -> Vec<DocActivity> {
        self.inner
            .lock()
            .unwrap()
            .activity
            .get(doc_id)
            .cloned()
            .unwrap_or_default()
    }

    pub fn link(&self, doc_id: &str, section_anchor: &str, project_id: &str, task_number: i32) -> DocLink {
        let now = (self.clock)();
        let link = DocLink {
            link_id: new_id(),
            doc_id: doc_id.to_owned(),
            section_anchor: section_anchor.to_owned(),
            project_id: project_id.to_owned(),
            task_number,
        };
        let mut inner = self.inner.lock().unwrap();
        inner.links.entry(doc_id.to_owned()).or_default().push(link.clone());
        inner.activity.entry(doc_id.to_owned()).or_default().push(DocActivity {
            activity_id: new_id(),
            doc_id: doc_id.to_owned(),
            author_member_id: "system".into(),
            action: "linked".into(),
            detail: Some(format!("{project_id}-{task_number}@{section_anchor}")),
            at: now,
        });
        link
    }

    pub fn links(&self, doc_id: &str) -> Vec<DocLink> {
        self.inner
            .lock()
            .unwrap()
            .links
            .get(doc_id)
            .cloned()
            .unwrap_or_default()
    }

    fn extract_mentions(&self, content: &str) -> Vec<String> {
        use std::collections::HashSet;
        let mut seen: HashSet<String> = HashSet::new();
        let mut out = Vec::new();
        for caps in self.mention_pattern.captures_iter(content) {
            let handle = caps[1].to_string();
            let lower = handle.to_lowercase();
            if seen.insert(lower) {
                out.push(handle);
            }
        }
        out
    }
}

impl Default for PacaDocService {
    fn default() -> Self {
        Self::new()
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaConversations.cs — conversation state machine + executor boundary
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Conversation state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConversationState {
    Queued,
    Running,
    Finished,
    Failed,
    Stopped,
}

/// (3.3.0) One conversation between a human + an agent (or multiple agents).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentConversation {
    pub id: String,
    pub project_id: String,
    pub agent_member_id: String,
    pub human_member_id: Option<String>,
    pub opening_prompt: String,
    pub state: ConversationState,
    pub queued_at_utc: DateTime<Utc>,
    pub started_at_utc: Option<DateTime<Utc>>,
    pub finished_at_utc: Option<DateTime<Utc>>,
    pub result_json: Option<String>,
    pub failure_reason: Option<String>,
}

/// (3.3.0) One executed step in a conversation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConversationStep {
    pub conversation_id: String,
    pub order: i32,
    pub speaker: String,
    pub content_json: String,
    pub at: DateTime<Utc>,
}

/// (3.3.0) Permission flag set required to run risky actions.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ConversationPermissions {
    pub allow_clone_repos: bool,
    pub allow_create_pr: bool,
}

/// (3.3.0) Terminal outcome an [`IConversationExecutor`] reports.
#[derive(Debug)]
pub enum ConversationError {
    /// The run was cancelled (maps to the C# `OperationCanceledException` →
    /// `Stopped`).
    Aborted,
    /// The executor failed with the given message (maps to a generic exception →
    /// `Failed`).
    Failed(String),
}

impl fmt::Display for ConversationError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ConversationError::Aborted => write!(f, "conversation aborted"),
            ConversationError::Failed(m) => write!(f, "conversation failed: {m}"),
        }
    }
}

impl std::error::Error for ConversationError {}

/// (3.3.0) Sink an [`IConversationExecutor`] pushes steps into. Mirrors the C#
/// `Action<ConversationStep>` callback.
pub trait ConversationStepSink: Send {
    fn on_step(&self, step: ConversationStep);
}

/// (3.3.0) Host-supplied executor — invokes OpenHands SDK / Docker container per
/// conversation.
#[async_trait]
pub trait IConversationExecutor {
    /// Start a conversation; emit [`ConversationStep`] values into `sink` as work
    /// progresses.
    async fn run(
        &self,
        conversation: &AgentConversation,
        permissions: ConversationPermissions,
        sink: &(dyn ConversationStepSink + Sync),
    ) -> Result<(), ConversationError>;
}

struct ConversationInner {
    conversations: HashMap<String, AgentConversation>,
    steps: HashMap<String, Vec<ConversationStep>>,
}

/// A [`ConversationStepSink`] that appends into the runtime's step log.
struct RuntimeStepSink<'a> {
    id: String,
    inner: &'a Mutex<ConversationInner>,
}

impl ConversationStepSink for RuntimeStepSink<'_> {
    fn on_step(&self, step: ConversationStep) {
        let mut inner = self.inner.lock().unwrap();
        inner.steps.entry(self.id.clone()).or_default().push(step);
    }
}

/// (3.3.0) Conversation registry + state machine.
///
/// The C# runs the executor on a background task with a `CancellationTokenSource`
/// per conversation; the Rust port keeps the identical state transitions and
/// invokes the executor via [`start`](Self::start), returning once the run
/// reaches a terminal state. Cancellation is modelled by the executor returning
/// [`ConversationError::Aborted`].
pub struct PacaConversationRuntime<E: IConversationExecutor> {
    inner: Mutex<ConversationInner>,
    executor: E,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl<E: IConversationExecutor> PacaConversationRuntime<E> {
    pub fn new(executor: E) -> Self {
        Self::with_clock(executor, Box::new(Utc::now))
    }

    pub fn with_clock(executor: E, clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self {
            inner: Mutex::new(ConversationInner {
                conversations: HashMap::new(),
                steps: HashMap::new(),
            }),
            executor,
            clock,
        }
    }

    pub fn queue(
        &self,
        id: &str,
        project_id: &str,
        agent_member_id: &str,
        opening_prompt: &str,
        human_member_id: Option<&str>,
    ) -> Result<AgentConversation, WorkflowError> {
        let c = AgentConversation {
            id: id.to_owned(),
            project_id: project_id.to_owned(),
            agent_member_id: agent_member_id.to_owned(),
            human_member_id: human_member_id.map(str::to_owned),
            opening_prompt: opening_prompt.to_owned(),
            state: ConversationState::Queued,
            queued_at_utc: (self.clock)(),
            started_at_utc: None,
            finished_at_utc: None,
            result_json: None,
            failure_reason: None,
        };
        let mut inner = self.inner.lock().unwrap();
        if inner.conversations.contains_key(id) {
            return Err(WorkflowError::InvalidOperation(format!(
                "Conversation '{id}' already exists."
            )));
        }
        inner.conversations.insert(id.to_owned(), c.clone());
        inner.steps.insert(id.to_owned(), Vec::new());
        Ok(c)
    }

    pub fn get(&self, id: &str) -> Option<AgentConversation> {
        self.inner.lock().unwrap().conversations.get(id).cloned()
    }

    pub fn steps(&self, id: &str) -> Vec<ConversationStep> {
        self.inner
            .lock()
            .unwrap()
            .steps
            .get(id)
            .cloned()
            .unwrap_or_default()
    }

    /// (3.3.0) Execute the conversation, driving the Queued → Running → terminal
    /// state machine.
    pub async fn start(
        &self,
        id: &str,
        permissions: ConversationPermissions,
    ) -> Result<(), WorkflowError> {
        let started = {
            let mut inner = self.inner.lock().unwrap();
            let current = inner.conversations.get(id).cloned();
            match current {
                Some(c) if c.state == ConversationState::Queued => {
                    let started = AgentConversation {
                        state: ConversationState::Running,
                        started_at_utc: Some((self.clock)()),
                        ..c
                    };
                    inner.conversations.insert(id.to_owned(), started.clone());
                    started
                }
                _ => {
                    return Err(WorkflowError::InvalidOperation(format!(
                        "Conversation '{id}' is not in Queued state."
                    )))
                }
            }
        };

        let sink = RuntimeStepSink {
            id: id.to_owned(),
            inner: &self.inner,
        };
        let outcome = self.executor.run(&started, permissions, &sink).await;

        let mut inner = self.inner.lock().unwrap();
        let now = (self.clock)();
        let finished = match outcome {
            Ok(()) => AgentConversation {
                state: ConversationState::Finished,
                finished_at_utc: Some(now),
                result_json: Some("{}".into()),
                ..started
            },
            Err(ConversationError::Aborted) => AgentConversation {
                state: ConversationState::Stopped,
                finished_at_utc: Some(now),
                ..started
            },
            Err(ConversationError::Failed(msg)) => AgentConversation {
                state: ConversationState::Failed,
                finished_at_utc: Some(now),
                failure_reason: Some(msg),
                ..started
            },
        };
        inner.conversations.insert(id.to_owned(), finished);
        Ok(())
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaRealtime.cs — realtime event union + hub + broadcaster boundary
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Realtime event union (the C# abstract `RealtimePacaEvent` + records).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RealtimePacaEvent {
    TaskUpdated {
        project_id: String,
        at: DateTime<Utc>,
        task_number: i32,
    },
    QueryInvalidation {
        project_id: String,
        at: DateTime<Utc>,
        query_key: String,
    },
    DocCursorMove {
        project_id: String,
        at: DateTime<Utc>,
        doc_id: String,
        member_id: String,
        cursor_offset: i32,
    },
    AgentActivity {
        project_id: String,
        at: DateTime<Utc>,
        agent_member_id: String,
        action: String,
        detail_json: String,
    },
    ConversationStepEvent {
        project_id: String,
        at: DateTime<Utc>,
        conversation_id: String,
        step: ConversationStep,
    },
}

impl RealtimePacaEvent {
    /// The owning project id (the C# `RealtimePacaEvent.ProjectId`).
    pub fn project_id(&self) -> &str {
        match self {
            RealtimePacaEvent::TaskUpdated { project_id, .. }
            | RealtimePacaEvent::QueryInvalidation { project_id, .. }
            | RealtimePacaEvent::DocCursorMove { project_id, .. }
            | RealtimePacaEvent::AgentActivity { project_id, .. }
            | RealtimePacaEvent::ConversationStepEvent { project_id, .. } => project_id,
        }
    }
}

/// (3.3.0) Host-supplied broadcaster (Socket.IO / Valkey Streams / etc.).
#[async_trait]
pub trait IRealtimeBroadcaster {
    type Error: std::error::Error;
    async fn broadcast(&self, room: &str, ev: &RealtimePacaEvent) -> Result<(), Self::Error>;
}

/// (3.3.0) Permission check — returns true if the member may join the room.
#[async_trait]
pub trait IPermissionCheck {
    async fn allow(&self, member_id: &str, room: &str) -> bool;
}

/// A permission check that allows everyone (the C# default delegate).
#[derive(Debug, Default, Clone, Copy)]
pub struct AllowAllPermissionCheck;

#[async_trait]
impl IPermissionCheck for AllowAllPermissionCheck {
    async fn allow(&self, _member_id: &str, _room: &str) -> bool {
        true
    }
}

/// (3.3.0) Realtime hub: routes events into rooms, gates joins with a permission
/// check.
pub struct PacaRealtimeHub<B: IRealtimeBroadcaster, P: IPermissionCheck = AllowAllPermissionCheck> {
    broadcaster: B,
    permission: P,
    members_by_room: Mutex<HashMap<String, Vec<String>>>,
}

impl<B: IRealtimeBroadcaster> PacaRealtimeHub<B, AllowAllPermissionCheck> {
    /// Creates a hub whose permission check allows every join.
    pub fn new(broadcaster: B) -> Self {
        Self::with_permission(broadcaster, AllowAllPermissionCheck)
    }
}

impl<B: IRealtimeBroadcaster, P: IPermissionCheck> PacaRealtimeHub<B, P> {
    pub fn with_permission(broadcaster: B, permission: P) -> Self {
        Self {
            broadcaster,
            permission,
            members_by_room: Mutex::new(HashMap::new()),
        }
    }

    /// (3.3.0) Member tries to join a room. Returns true if permission allowed.
    pub async fn join(&self, member_id: &str, room: &str) -> bool {
        if !self.permission.allow(member_id, room).await {
            return false;
        }
        let mut rooms = self.members_by_room.lock().unwrap();
        let bucket = rooms.entry(room.to_owned()).or_default();
        if !bucket.iter().any(|m| m == member_id) {
            bucket.push(member_id.to_owned());
        }
        true
    }

    pub fn leave(&self, member_id: &str, room: &str) {
        let mut rooms = self.members_by_room.lock().unwrap();
        if let Some(bucket) = rooms.get_mut(room) {
            bucket.retain(|m| m != member_id);
        }
    }

    pub fn members(&self, room: &str) -> Vec<String> {
        self.members_by_room
            .lock()
            .unwrap()
            .get(room)
            .cloned()
            .unwrap_or_default()
    }

    /// (3.3.0) Publish an event to the project's main room.
    pub async fn publish(&self, ev: &RealtimePacaEvent) -> Result<(), B::Error> {
        let room = format!("project:{}", ev.project_id());
        self.broadcaster.broadcast(&room, ev).await
    }

    /// (3.3.0) Publish to a doc collaboration sub-room.
    pub async fn publish_to_doc(&self, doc_id: &str, ev: &RealtimePacaEvent) -> Result<(), B::Error> {
        self.broadcaster.broadcast(&format!("doc:{doc_id}"), ev).await
    }
}

/// (3.3.0) Helper that maps known events to query-invalidation keys for client UIs.
pub struct QueryInvalidation;

impl QueryInvalidation {
    pub fn keys_for(ev: &RealtimePacaEvent) -> Vec<String> {
        match ev {
            RealtimePacaEvent::TaskUpdated {
                project_id,
                task_number,
                ..
            } => vec![
                format!("tasks/{project_id}"),
                format!("task/{project_id}/{task_number}"),
            ],
            RealtimePacaEvent::AgentActivity {
                project_id,
                agent_member_id,
                ..
            } => vec![
                format!("activity/{project_id}"),
                format!("agent/{agent_member_id}"),
            ],
            RealtimePacaEvent::ConversationStepEvent {
                project_id,
                conversation_id,
                ..
            } => vec![
                format!("conversation/{conversation_id}"),
                format!("conversations/{project_id}"),
            ],
            RealtimePacaEvent::DocCursorMove { doc_id, .. } => vec![format!("doc/{doc_id}/cursors")],
            RealtimePacaEvent::QueryInvalidation { query_key, .. } => vec![query_key.clone()],
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaMcp.cs — MCP server + built-in tools
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) MCP transport types.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum McpTransportKind {
    Stdio,
    ServerSentEvents,
    Http,
}

/// (3.3.0) Per-agent MCP server config.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentMcpConfig {
    pub agent_member_id: String,
    pub transports: Vec<McpTransportKind>,
    pub enabled_tools: Vec<String>,
    pub tool_settings: HashMap<String, String>,
}

/// (3.3.0) MCP tool descriptor.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PacaMcpTool {
    pub name: String,
    pub description: String,
    pub input_schema: String,
}

/// (3.3.0) MCP tool handler signature.
#[async_trait]
pub trait PacaMcpHandler: Send + Sync {
    async fn invoke(&self, arguments_json: &str) -> Result<String, WorkflowError>;
}

struct McpServerInner {
    tools: HashMap<String, (PacaMcpTool, Box<dyn PacaMcpHandler>)>,
    agent_configs: HashMap<String, AgentMcpConfig>,
}

/// (3.3.0) Paca's MCP server: registers built-in workflow tools + plugin tools.
pub struct PacaMcpServer {
    inner: Mutex<McpServerInner>,
}

impl PacaMcpServer {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(McpServerInner {
                tools: HashMap::new(),
                agent_configs: HashMap::new(),
            }),
        }
    }

    pub fn tools(&self) -> Vec<PacaMcpTool> {
        self.inner
            .lock()
            .unwrap()
            .tools
            .values()
            .map(|(t, _)| t.clone())
            .collect()
    }

    pub fn register_tool(&self, tool: PacaMcpTool, handler: Box<dyn PacaMcpHandler>) {
        let key = tool.name.to_lowercase();
        self.inner.lock().unwrap().tools.insert(key, (tool, handler));
    }

    /// (3.3.0) Configure a per-agent toolset.
    pub fn configure_agent(&self, config: AgentMcpConfig) {
        let key = config.agent_member_id.clone();
        self.inner.lock().unwrap().agent_configs.insert(key, config);
    }

    pub fn get_agent_config(&self, agent_member_id: &str) -> Option<AgentMcpConfig> {
        self.inner
            .lock()
            .unwrap()
            .agent_configs
            .get(agent_member_id)
            .cloned()
    }

    /// (3.3.0) Invoke a tool for a specific agent — enforces the agent's
    /// enabled-tool list. Returns the handler's JSON, or a `{"error":…}` envelope.
    pub async fn invoke(&self, agent_member_id: &str, tool_name: &str, arguments_json: &str) -> String {
        // Look up the handler + config under the lock, then release it before
        // awaiting (the C# `ConcurrentDictionary` does the same effectively).
        let tool_key = tool_name.to_lowercase();
        let has_tool;
        let gate_denied;
        {
            let inner = self.inner.lock().unwrap();
            has_tool = inner.tools.contains_key(&tool_key);
            gate_denied = match inner.agent_configs.get(agent_member_id) {
                Some(cfg) => {
                    !cfg.enabled_tools.is_empty()
                        && !cfg
                            .enabled_tools
                            .iter()
                            .any(|t| t.eq_ignore_ascii_case(tool_name))
                }
                None => false,
            };
        }
        if !has_tool {
            return wrap_error(&format!("Unknown tool '{tool_name}'."));
        }
        if gate_denied {
            return wrap_error(&format!(
                "Tool '{tool_name}' is not enabled for agent '{agent_member_id}'."
            ));
        }

        // Invoke under a fresh short-lived lock scope to clone the handler call.
        // Because handlers are trait objects held in the map, we perform the call
        // while holding a guard — safe since the handler is `Send + Sync` and the
        // future does not re-enter the server.
        let result = {
            let inner = self.inner.lock().unwrap();
            match inner.tools.get(&tool_key) {
                Some((_, handler)) => handler.invoke(arguments_json).await,
                None => return wrap_error(&format!("Unknown tool '{tool_name}'.")),
            }
        };
        match result {
            Ok(json) => json,
            Err(e) => wrap_error(&e.to_string()),
        }
    }

    /// (3.3.0) JSON-RPC `tools/list` response payload (raw, minimally escaped).
    pub fn tools_list_json(&self) -> String {
        let inner = self.inner.lock().unwrap();
        let entries: Vec<String> = inner
            .tools
            .values()
            .map(|(t, _)| {
                format!(
                    "{{\"name\":{},\"description\":{},\"inputSchema\":{}}}",
                    json_string(&t.name),
                    json_string(&t.description),
                    t.input_schema
                )
            })
            .collect();
        format!("{{\"tools\":[{}]}}", entries.join(","))
    }
}

impl Default for PacaMcpServer {
    fn default() -> Self {
        Self::new()
    }
}

fn wrap_error(message: &str) -> String {
    format!("{{\"error\":{{\"message\":{}}}}}", json_string(message))
}

/// (3.3.0) Built-in workflow tools.
pub struct PacaCoreMcpTools;

impl PacaCoreMcpTools {
    pub fn create_task() -> PacaMcpTool {
        PacaMcpTool {
            name: "create_task".into(),
            description: "Create a new task in a project.".into(),
            input_schema: r#"{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"}},"required":["project_id","title"]}"#.into(),
        }
    }

    pub fn list_tasks() -> PacaMcpTool {
        PacaMcpTool {
            name: "list_tasks".into(),
            description: "List live tasks in a project.".into(),
            input_schema: r#"{"type":"object","properties":{"project_id":{"type":"string"}},"required":["project_id"]}"#.into(),
        }
    }

    pub fn edit_task() -> PacaMcpTool {
        PacaMcpTool {
            name: "edit_task".into(),
            description: "Edit a task (title, description, status).".into(),
            input_schema: r#"{"type":"object","properties":{"project_id":{"type":"string"},"number":{"type":"integer"},"title":{"type":"string"},"description":{"type":"string"},"status":{"type":"string"}},"required":["project_id","number"]}"#.into(),
        }
    }

    pub fn create_doc() -> PacaMcpTool {
        PacaMcpTool {
            name: "create_doc".into(),
            description: "Create a doc in the project's doc tree.".into(),
            input_schema: r#"{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"parent_id":{"type":"string","nullable":true},"content_json":{"type":"string"}},"required":["project_id","title","content_json"]}"#.into(),
        }
    }

    pub fn link_doc_to_task() -> PacaMcpTool {
        PacaMcpTool {
            name: "link_doc_to_task".into(),
            description: "Link a doc section to a task.".into(),
            input_schema: r#"{"type":"object","properties":{"doc_id":{"type":"string"},"section_anchor":{"type":"string"},"project_id":{"type":"string"},"task_number":{"type":"integer"}},"required":["doc_id","section_anchor","project_id","task_number"]}"#.into(),
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaPlugins.cs — plugin lifecycle + manifest validation + registry
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Plugin extension points supported by the marketplace.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PluginExtensionPoint {
    Sidebar,
    TaskDetail,
    Settings,
    CustomView,
    Route,
    Event,
    McpTool,
}

/// (3.3.0) Per-plugin resource limits.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PluginResourceLimits {
    /// Max wall-clock time for one host call, in ms. Default 5000.
    pub call_timeout_ms: i32,
    /// Max memory the WASM instance may allocate, in bytes. Default 64 MiB.
    pub memory_ceiling_bytes: i64,
}

impl Default for PluginResourceLimits {
    fn default() -> Self {
        Self {
            call_timeout_ms: 5000,
            memory_ceiling_bytes: 64 * 1024 * 1024,
        }
    }
}

/// (3.3.0) Plugin manifest from `plugin.json`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginManifest {
    /// reverse-DNS, e.g. "com.paca.bdd".
    pub name: String,
    pub display_name: String,
    /// SemVer.
    pub version: String,
    pub description: String,
    pub artifact_wasm_url: Option<String>,
    pub frontend_module_url: Option<String>,
    pub extension_points: Vec<PluginExtensionPoint>,
    pub mcp_tools: Vec<String>,
    pub sql_migration_files: Vec<String>,
    pub limits: PluginResourceLimits,
}

/// (3.3.0) Installed instance.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstalledPlugin {
    /// matches manifest.name.
    pub id: String,
    pub manifest: PluginManifest,
    pub installed_from_catalog: String,
    pub installed_at_utc: DateTime<Utc>,
    pub enabled: bool,
}

/// (3.3.0) Plugin runtime host (wazero-style). Provided by the deploy.
#[async_trait]
pub trait IPluginRuntimeHost {
    type Error: std::error::Error;
    /// Install + initialise. Run SQL migrations + cache the WASM artifact.
    async fn install(&self, plugin: &InstalledPlugin) -> Result<(), Self::Error>;
    /// Uninstall — drop WASM + clean artifacts; do NOT roll back data unless asked.
    async fn uninstall(&self, plugin_id: &str, drop_artifacts: bool) -> Result<(), Self::Error>;
    /// Hot-swap to a new version (semver upgrade).
    async fn upgrade(&self, from: &InstalledPlugin, to: &InstalledPlugin) -> Result<(), Self::Error>;
}

/// (3.3.0) Plugin lifecycle manager. Installs / upgrades / uninstalls / enables /
/// disables.
pub struct PacaPluginRegistry<H: IPluginRuntimeHost> {
    installed: Mutex<HashMap<String, InstalledPlugin>>,
    runtime: H,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
    reverse_dns: Regex,
}

impl<H: IPluginRuntimeHost> PacaPluginRegistry<H> {
    pub fn new(runtime: H) -> Self {
        Self::with_clock(runtime, Box::new(Utc::now))
    }

    pub fn with_clock(runtime: H, clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self {
            installed: Mutex::new(HashMap::new()),
            runtime,
            clock,
            reverse_dns: Regex::new(r"^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+$").unwrap(),
        }
    }

    pub fn list_installed(&self) -> Vec<InstalledPlugin> {
        self.installed.lock().unwrap().values().cloned().collect()
    }

    pub fn get(&self, id: &str) -> Option<InstalledPlugin> {
        self.installed.lock().unwrap().get(id).cloned()
    }

    /// (3.3.0) Validate a manifest before install / upgrade.
    pub fn validate_manifest(&self, manifest: &PluginManifest) -> Result<(), WorkflowError> {
        if !self.reverse_dns.is_match(&manifest.name) {
            return Err(WorkflowError::InvalidArgument(format!(
                "Plugin name '{}' must be reverse-DNS (e.g. com.paca.bdd).",
                manifest.name
            )));
        }
        if parse_semver(&strip_prerelease(&manifest.version)).is_none() {
            return Err(WorkflowError::InvalidArgument(format!(
                "Plugin version '{}' is not parseable SemVer.",
                manifest.version
            )));
        }
        if manifest.limits.call_timeout_ms <= 0 {
            return Err(WorkflowError::InvalidArgument(
                "CallTimeoutMs must be positive.".into(),
            ));
        }
        if manifest.limits.memory_ceiling_bytes <= 0 {
            return Err(WorkflowError::InvalidArgument(
                "MemoryCeilingBytes must be positive.".into(),
            ));
        }
        Ok(())
    }

    /// (3.3.0) Install plugin from the supplied manifest.
    pub async fn install(
        &self,
        manifest: PluginManifest,
        catalog: &str,
    ) -> Result<InstalledPlugin, PluginRegistryError<H::Error>> {
        self.validate_manifest(&manifest).map_err(PluginRegistryError::Workflow)?;
        {
            let installed = self.installed.lock().unwrap();
            if installed.contains_key(&manifest.name) {
                return Err(PluginRegistryError::Workflow(WorkflowError::InvalidOperation(
                    format!(
                        "Plugin '{}' is already installed; use UpgradeAsync.",
                        manifest.name
                    ),
                )));
            }
        }
        let installed = InstalledPlugin {
            id: manifest.name.clone(),
            manifest: manifest.clone(),
            installed_from_catalog: catalog.to_owned(),
            installed_at_utc: (self.clock)(),
            enabled: true,
        };
        self.runtime
            .install(&installed)
            .await
            .map_err(PluginRegistryError::Runtime)?;
        self.installed
            .lock()
            .unwrap()
            .insert(manifest.name.clone(), installed.clone());
        Ok(installed)
    }

    /// (3.3.0) Upgrade if `new_manifest`'s SemVer is strictly newer.
    pub async fn upgrade(
        &self,
        new_manifest: PluginManifest,
        catalog: &str,
    ) -> Result<InstalledPlugin, PluginRegistryError<H::Error>> {
        self.validate_manifest(&new_manifest)
            .map_err(PluginRegistryError::Workflow)?;
        let current = {
            let installed = self.installed.lock().unwrap();
            installed.get(&new_manifest.name).cloned()
        };
        let current = current.ok_or_else(|| {
            PluginRegistryError::Workflow(WorkflowError::InvalidOperation(format!(
                "Plugin '{}' is not installed.",
                new_manifest.name
            )))
        })?;
        if compare_semver(&new_manifest.version, &current.manifest.version) <= 0 {
            return Err(PluginRegistryError::Workflow(WorkflowError::InvalidOperation(
                format!(
                    "Version {} is not newer than {}.",
                    new_manifest.version, current.manifest.version
                ),
            )));
        }
        let next = InstalledPlugin {
            id: new_manifest.name.clone(),
            manifest: new_manifest.clone(),
            installed_from_catalog: catalog.to_owned(),
            installed_at_utc: (self.clock)(),
            enabled: current.enabled,
        };
        self.runtime
            .upgrade(&current, &next)
            .await
            .map_err(PluginRegistryError::Runtime)?;
        self.installed
            .lock()
            .unwrap()
            .insert(new_manifest.name.clone(), next.clone());
        Ok(next)
    }

    pub async fn uninstall(
        &self,
        id: &str,
        drop_artifacts: bool,
    ) -> Result<(), PluginRegistryError<H::Error>> {
        let removed = self.installed.lock().unwrap().remove(id).is_some();
        if !removed {
            return Ok(());
        }
        self.runtime
            .uninstall(id, drop_artifacts)
            .await
            .map_err(PluginRegistryError::Runtime)
    }

    pub fn set_enabled(&self, id: &str, enabled: bool) {
        let mut installed = self.installed.lock().unwrap();
        if let Some(current) = installed.get_mut(id) {
            current.enabled = enabled;
        }
    }
}

/// Error surface for [`PacaPluginRegistry`] — either a validation/state error or
/// the runtime host's own error.
#[derive(Debug)]
pub enum PluginRegistryError<E> {
    Workflow(WorkflowError),
    Runtime(E),
}

impl<E: fmt::Display> fmt::Display for PluginRegistryError<E> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            PluginRegistryError::Workflow(e) => write!(f, "{e}"),
            PluginRegistryError::Runtime(e) => write!(f, "plugin runtime error: {e}"),
        }
    }
}

impl<E: std::error::Error> std::error::Error for PluginRegistryError<E> {}

/// (3.3.0) Compare SemVer-ish strings: returns `<0` / `0` / `>0`. Public free
/// function mirroring the C# `PacaPluginRegistry.CompareSemver`.
pub fn compare_semver(a: &str, b: &str) -> i32 {
    let va = parse_semver(&strip_prerelease(a)).unwrap_or((0, 0, 0, 0));
    let vb = parse_semver(&strip_prerelease(b)).unwrap_or((0, 0, 0, 0));
    match va.cmp(&vb) {
        std::cmp::Ordering::Less => -1,
        std::cmp::Ordering::Equal => 0,
        std::cmp::Ordering::Greater => 1,
    }
}

fn strip_prerelease(v: &str) -> String {
    v.split(['-', '+']).next().unwrap_or("").to_owned()
}

/// Parses a `.NET System.Version`-style dotted number string into a 4-tuple,
/// zero-filling missing components (matching `Version.Parse`, which requires at
/// least major.minor). Returns `None` when fewer than two components are present
/// or any component is non-numeric.
fn parse_semver(v: &str) -> Option<(u64, u64, u64, u64)> {
    let parts: Vec<&str> = v.split('.').collect();
    if parts.len() < 2 {
        return None;
    }
    let mut nums = [0u64; 4];
    for (i, p) in parts.iter().enumerate().take(4) {
        nums[i] = p.parse().ok()?;
    }
    Some((nums[0], nums[1], nums[2], nums[3]))
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaSkills.cs — built-in skills + templates + installer
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) A skill definition: frontmatter metadata + body.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PacaSkill {
    pub name: String,
    pub description: String,
    pub body: String,
}

impl PacaSkill {
    pub fn new(name: &str, description: &str, body: &str) -> Self {
        Self {
            name: name.to_owned(),
            description: description.to_owned(),
            body: body.to_owned(),
        }
    }

    /// (3.3.0) Render as a Claude-Code-compatible markdown file with frontmatter.
    pub fn to_markdown(&self) -> String {
        format!(
            "---\nname: {}\ndescription: {}\n---\n\n{}",
            self.name, self.description, self.body
        )
    }

    /// (3.3.0) Render as the bare body (frontmatter stripped) for the installer.
    pub fn to_body_only(&self) -> String {
        self.body.clone()
    }
}

/// (3.3.0) The nine creator-skill templates (markdown body).
pub struct SkillTemplates;

impl SkillTemplates {
    pub const EPIC: &'static str = "You are running paca-epic. Use only the paca MCP tools. Output structure: title, problem statement, success criteria, scope, out-of-scope, risks.";
    pub const BREAKDOWN: &'static str = "You are running paca-breakdown. Use only the paca MCP tools. Take the supplied epic and produce a numbered list of tasks with title + acceptance criteria.";
    pub const CLARIFY: &'static str = "You are running paca-clarify. Pose the smallest set of clarifying questions needed to estimate the supplied task.";
    pub const SPRINT: &'static str = "You are running paca-sprint. Use the create_sprint / start_sprint / complete_sprint MCP tools.";
    pub const ESTIMATE: &'static str = "You are running paca-estimate. For each task, propose story points (1-13). Cite assumptions.";
    pub const PRIORITIZE: &'static str = "You are running paca-prioritize. Reorder the backlog by importance (0-5). Cite reasoning.";
    pub const DO: &'static str = "You are running paca-do. Pick the next-best ready task, mark in_progress, execute, then mark done.";
    pub const TEST: &'static str = "You are running paca-test. Write and run unit + integration tests for the current change.";
    pub const DOC: &'static str = "You are running paca-doc. Update the living document with the smallest accurate diff.";
}

/// (3.3.0) The eleven built-in paca skills.
pub struct PacaSkillLibrary;

impl PacaSkillLibrary {
    /// Returns the full set, in the C# order.
    pub fn all() -> Vec<PacaSkill> {
        vec![
            PacaSkill::new(
                "paca",
                "Run the paca workflow on the current ask.",
                "Use the paca MCP tools to plan and execute the user's request.",
            ),
            PacaSkill::new("paca-epic", "Capture a large initiative as a paca epic.", SkillTemplates::EPIC),
            PacaSkill::new(
                "paca-breakdown",
                "Break a paca epic into actionable tasks.",
                SkillTemplates::BREAKDOWN,
            ),
            PacaSkill::new(
                "paca-clarify",
                "Ask the right clarifying questions before estimating.",
                SkillTemplates::CLARIFY,
            ),
            PacaSkill::new(
                "paca-sprint",
                "Form / close a sprint with the paca sprint surface.",
                SkillTemplates::SPRINT,
            ),
            PacaSkill::new(
                "paca-estimate",
                "Estimate story points for a set of tasks.",
                SkillTemplates::ESTIMATE,
            ),
            PacaSkill::new(
                "paca-prioritize",
                "Reorder the backlog by importance.",
                SkillTemplates::PRIORITIZE,
            ),
            PacaSkill::new("paca-do", "Pick the next-best task and start it.", SkillTemplates::DO),
            PacaSkill::new(
                "paca-test",
                "Generate and run tests for the current change.",
                SkillTemplates::TEST,
            ),
            PacaSkill::new(
                "paca-doc",
                "Update the project's living doc to reflect the latest change.",
                SkillTemplates::DOC,
            ),
            PacaSkill::new(
                "paca-setup",
                "First-run setup: pick project, configure agents, install plugins.",
                "Walk the user through paca first-run setup.",
            ),
        ]
    }

    pub fn find(name: &str) -> Option<PacaSkill> {
        Self::all().into_iter().find(|s| s.name.eq_ignore_ascii_case(name))
    }
}

/// (3.3.0) Installer that drops bare skill bodies into ~/.claude/commands/.
pub struct PacaSkillInstaller {
    commands_dir: String,
}

impl PacaSkillInstaller {
    pub fn new(commands_dir: &str) -> Result<Self, WorkflowError> {
        require_non_blank(commands_dir, "commandsDir")?;
        Ok(Self {
            commands_dir: commands_dir.to_owned(),
        })
    }

    /// (3.3.0) Install all built-in skills.
    pub fn install_all(&self) -> Result<Vec<String>, WorkflowError> {
        self.install_each(&PacaSkillLibrary::all())
    }

    /// (3.3.0) Install a custom set of skills.
    pub fn install_each(&self, skills: &[PacaSkill]) -> Result<Vec<String>, WorkflowError> {
        fs::create_dir_all(&self.commands_dir)?;
        let mut installed = Vec::new();
        for skill in skills {
            let path = Path::new(&self.commands_dir).join(format!("{}.md", skill.name));
            let body = Self::strip_frontmatter(&skill.to_markdown());
            fs::write(&path, body.as_bytes())?;
            installed.push(path.to_string_lossy().into_owned());
        }
        Ok(installed)
    }

    /// (3.3.0) Uninstall a set of skills by name.
    pub fn uninstall_by_name(&self, names: &[&str]) -> Result<i32, WorkflowError> {
        let mut count = 0;
        for name in names {
            let path = Path::new(&self.commands_dir).join(format!("{name}.md"));
            if path.exists() {
                fs::remove_file(&path)?;
                count += 1;
            }
        }
        Ok(count)
    }

    /// (3.3.0) Strip the frontmatter block from a markdown skill file.
    pub fn strip_frontmatter(markdown: &str) -> String {
        if markdown.is_empty() {
            return String::new();
        }
        // `(?s)` = singleline (`.` matches `\n`), mirroring RegexOptions.Singleline.
        // The pattern is anchored at `^`; a match therefore starts at index 0.
        let re = frontmatter_regex();
        if let Some(m) = re.find(markdown) {
            if m.start() == 0 {
                return markdown[m.end()..].trim_start().to_owned();
            }
        }
        markdown.trim_start().to_owned()
    }
}

/// Lazily-built shared frontmatter regex for the self-less `strip_frontmatter`
/// (mirrors the C# `static readonly Regex FrontmatterPattern`).
fn frontmatter_regex() -> &'static Regex {
    static CELL: OnceLock<Regex> = OnceLock::new();
    CELL.get_or_init(|| Regex::new(r"(?s)^\s*---.*?---\s*\n").unwrap())
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaDeploy.cs — compose / .env / plugin-script generation
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Deployment mode.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PacaDeployMode {
    Dev,
    Prod,
    E2E,
}

impl PacaDeployMode {
    fn as_env(&self) -> &'static str {
        match self {
            PacaDeployMode::Dev => "dev",
            PacaDeployMode::Prod => "prod",
            PacaDeployMode::E2E => "e2e",
        }
    }
}

/// (3.3.0) Optional overrides.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PacaDeployOverrides {
    /// If set, omit the bundled postgres service and write its DSN into .env.
    pub use_external_postgres: Option<String>,
    /// If set, omit MinIO and write external S3 endpoint into .env.
    pub use_external_s3: Option<String>,
    /// If true, omit the AI-runtime container (for very thin installs).
    pub skip_ai_agent: bool,
}

/// (3.3.0) Compose-file + .env pair the installer writes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PacaDeployArtifact {
    pub compose_yaml: String,
    pub env_file: String,
}

/// (3.3.0) Generates compose + .env files for the paca stack.
///
/// The random-secret source is injected via [`ISecureRandom`] so the generation
/// stays deterministic in tests; a host wires an OS CSPRNG for production. The
/// C# uses `RandomNumberGenerator.GetBytes`.
pub struct PacaDeployer;

impl PacaDeployer {
    /// (3.3.0) Build the compose + env pair for a given mode using an injected
    /// randomness source.
    pub fn build(
        mode: PacaDeployMode,
        overrides: Option<&PacaDeployOverrides>,
        rng: &mut dyn ISecureRandom,
    ) -> PacaDeployArtifact {
        let default_overrides = PacaDeployOverrides::default();
        let overrides = overrides.unwrap_or(&default_overrides);

        let mut sb = String::new();
        sb.push_str("version: '3.9'\n");
        sb.push_str("services:\n");

        sb.push_str("  paca-web:\n");
        sb.push_str(&format!(
            "    image: bhengubv/paca-web:{}\n",
            if mode == PacaDeployMode::Prod { "stable" } else { "latest" }
        ));
        sb.push_str("    env_file: [.env]\n");
        sb.push_str("    ports:\n");
        sb.push_str(&format!(
            "      - \"{}:8080\"\n",
            if mode == PacaDeployMode::Prod { 443 } else { 8080 }
        ));

        if overrides.use_external_postgres.as_deref().unwrap_or("").is_empty() {
            sb.push_str("  paca-postgres:\n");
            sb.push_str("    image: postgres:16-alpine\n");
            sb.push_str("    environment:\n");
            sb.push_str("      POSTGRES_USER:     ${PACA_PG_USER}\n");
            sb.push_str("      POSTGRES_PASSWORD: ${PACA_PG_PASSWORD}\n");
            sb.push_str("      POSTGRES_DB:       ${PACA_PG_DB}\n");
            sb.push_str("    volumes: [paca_pg_data:/var/lib/postgresql/data]\n");
        }

        sb.push_str("  paca-valkey:\n");
        sb.push_str("    image: valkey/valkey:8\n");

        if overrides.use_external_s3.as_deref().unwrap_or("").is_empty() {
            sb.push_str("  paca-minio:\n");
            sb.push_str("    image: minio/minio:latest\n");
            sb.push_str("    environment:\n");
            sb.push_str("      MINIO_ROOT_USER:     ${PACA_S3_KEY}\n");
            sb.push_str("      MINIO_ROOT_PASSWORD: ${PACA_S3_SECRET}\n");
            sb.push_str("    command: server /data\n");
        }

        sb.push_str("  paca-nginx:\n");
        sb.push_str("    image: nginx:1.27-alpine\n");

        if !overrides.skip_ai_agent {
            sb.push_str("  paca-ai:\n");
            sb.push_str("    image: bhengubv/paca-ai:latest\n");
            sb.push_str("    env_file: [.env]\n");
        }

        if overrides.use_external_postgres.as_deref().unwrap_or("").is_empty() {
            sb.push_str("volumes:\n");
            sb.push_str("  paca_pg_data: {}\n");
        }

        let env = Self::build_env_file(mode, overrides, rng);
        PacaDeployArtifact {
            compose_yaml: sb,
            env_file: env,
        }
    }

    /// (3.3.0) Build the bash install-plugin script that drives the plugin
    /// lifecycle from CLI.
    pub fn build_install_plugin_script(plugin_name: &str) -> Result<String, WorkflowError> {
        require_non_blank(plugin_name, "pluginName")?;
        Ok(format!(
            "#!/usr/bin/env bash\n\
             set -euo pipefail\n\
             echo \"[paca] Building WASM module for {plugin_name}...\"\n\
             wasm-pack build --target web ./plugins/{plugin_name}\n\
             echo \"[paca] Building frontend bundle...\"\n\
             cd ./plugins/{plugin_name}/frontend && pnpm install && pnpm build\n\
             cd -\n\
             echo \"[paca] Registering plugin with the API...\"\n\
             paca-cli plugins install ./plugins/{plugin_name}/dist\n\
             echo \"[paca] Done.\"\n"
        ))
    }

    /// (3.3.0) Bash script that uninstalls + cleans plugin artifacts.
    pub fn build_uninstall_plugin_script(plugin_name: &str) -> Result<String, WorkflowError> {
        require_non_blank(plugin_name, "pluginName")?;
        Ok(format!(
            "#!/usr/bin/env bash\n\
             set -euo pipefail\n\
             echo \"[paca] Uninstalling {plugin_name}...\"\n\
             paca-cli plugins uninstall {plugin_name}\n\
             rm -rf ./plugins/{plugin_name}/dist\n\
             echo \"[paca] Done.\"\n"
        ))
    }

    fn build_env_file(mode: PacaDeployMode, overrides: &PacaDeployOverrides, rng: &mut dyn ISecureRandom) -> String {
        let mut sb = String::new();
        sb.push_str(&format!("PACA_MODE={}\n", mode.as_env()));
        sb.push_str("PACA_PG_USER=paca\n");
        sb.push_str(&format!("PACA_PG_PASSWORD={}\n", random_secret(rng, 32)));
        sb.push_str("PACA_PG_DB=paca\n");
        if let Some(pg) = &overrides.use_external_postgres {
            if !pg.is_empty() {
                sb.push_str(&format!("PACA_PG_URL={pg}\n"));
            }
        }
        sb.push_str("PACA_VALKEY_URL=redis://paca-valkey:6379\n");
        sb.push_str(&format!("PACA_S3_KEY={}\n", random_secret(rng, 20)));
        sb.push_str(&format!("PACA_S3_SECRET={}\n", random_secret(rng, 40)));
        if let Some(s3) = &overrides.use_external_s3 {
            if !s3.is_empty() {
                sb.push_str(&format!("PACA_S3_ENDPOINT={s3}\n"));
            }
        }
        sb.push_str(&format!("PACA_JWT_SIGNING_SECRET={}\n", random_secret(rng, 48)));
        sb.push_str(&format!(
            "PACA_AI_ENABLED={}\n",
            if overrides.skip_ai_agent { "false" } else { "true" }
        ));
        sb
    }
}

/// URL-safe base64 secret of exactly `length` characters, drawn from `rng` (the
/// C# `RandomSecret`: base64 of `length` random bytes, `+/` → `-_`, padding
/// trimmed, truncated to `length`).
fn random_secret(rng: &mut dyn ISecureRandom, length: usize) -> String {
    let bytes = rng.next_bytes(length);
    let mut b64 = crypto::base64_standard(&bytes);
    b64 = b64.replace('+', "-").replace('/', "_");
    let b64 = b64.trim_end_matches('=');
    b64.chars().take(length).collect()
}

// ═════════════════════════════════════════════════════════════════════════════
// PacaAuth.cs — HMAC-SHA256 JWT + API-key authentication
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Injectable secure-randomness source. The C# uses
/// `RandomNumberGenerator.GetBytes`; a host wires an OS CSPRNG. The shipped
/// [`CounterSecureRandom`] is deterministic (test-friendly) and MUST be replaced
/// for anything that issues real secrets/keys.
pub trait ISecureRandom {
    /// Return `n` fresh random bytes.
    fn next_bytes(&mut self, n: usize) -> Vec<u8>;
}

/// Deterministic counter-based [`ISecureRandom`]. NOT cryptographically secure —
/// exists so secret/key generation is exercisable without an OS RNG dependency.
#[derive(Debug, Default, Clone)]
pub struct CounterSecureRandom {
    counter: u64,
}

impl CounterSecureRandom {
    pub fn new(seed: u64) -> Self {
        Self { counter: seed }
    }
}

impl ISecureRandom for CounterSecureRandom {
    fn next_bytes(&mut self, n: usize) -> Vec<u8> {
        // SplitMix64-style stream so successive calls differ; deterministic.
        let mut out = Vec::with_capacity(n);
        while out.len() < n {
            self.counter = self.counter.wrapping_add(0x9E37_79B9_7F4A_7C15);
            let mut z = self.counter;
            z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
            z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
            z ^= z >> 31;
            out.extend_from_slice(&z.to_le_bytes());
        }
        out.truncate(n);
        out
    }
}

/// (3.3.0) Token-shaped JWT result.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct JwtPair {
    pub access_token: String,
    pub refresh_token: String,
    pub access_expires_at_utc: DateTime<Utc>,
    pub refresh_expires_at_utc: DateTime<Utc>,
}

/// (3.3.0) Verified JWT payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct JwtPayload {
    pub subject: String,
    pub claims: HashMap<String, String>,
    pub expires_at_utc: DateTime<Utc>,
}

/// (3.3.0) HMAC-SHA256 JWT issuer + verifier. Wire format is byte-identical to
/// the C# reference (header `{"alg":"HS256","typ":"JWT"}`, base64url segments,
/// HMAC-SHA256 signature).
pub struct HmacJwtAuthenticator {
    secret: Vec<u8>,
    access_lifetime: Duration,
    refresh_lifetime: Duration,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl HmacJwtAuthenticator {
    /// C# defaults: access 15 min, refresh 7 days, system clock.
    pub fn new(signing_secret: &str) -> Result<Self, WorkflowError> {
        Self::with_options(signing_secret, None, None, Box::new(Utc::now))
    }

    pub fn with_options(
        signing_secret: &str,
        access_lifetime: Option<Duration>,
        refresh_lifetime: Option<Duration>,
        clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
    ) -> Result<Self, WorkflowError> {
        if signing_secret.trim().is_empty() || signing_secret.len() < 16 {
            return Err(WorkflowError::InvalidArgument(
                "Signing secret must be at least 16 characters.".into(),
            ));
        }
        Ok(Self {
            secret: signing_secret.as_bytes().to_vec(),
            access_lifetime: access_lifetime.unwrap_or_else(|| Duration::minutes(15)),
            refresh_lifetime: refresh_lifetime.unwrap_or_else(|| Duration::days(7)),
            clock,
        })
    }

    /// (3.3.0) Issue access + refresh tokens for `subject`.
    pub fn issue(
        &self,
        subject: &str,
        claims: Option<&HashMap<String, String>>,
    ) -> Result<JwtPair, WorkflowError> {
        require_non_blank(subject, "subject")?;
        let now = (self.clock)();
        let access_exp = now + self.access_lifetime;
        let refresh_exp = now + self.refresh_lifetime;
        let access = self.encode_token(subject, "access", access_exp, claims);
        let refresh = self.encode_token(subject, "refresh", refresh_exp, None);
        Ok(JwtPair {
            access_token: access,
            refresh_token: refresh,
            access_expires_at_utc: access_exp,
            refresh_expires_at_utc: refresh_exp,
        })
    }

    /// (3.3.0) Verify a token; returns the payload or `None` if invalid/expired.
    pub fn verify(&self, token: &str, expected_type: &str) -> Option<JwtPayload> {
        if token.trim().is_empty() {
            return None;
        }
        let parts: Vec<&str> = token.split('.').collect();
        if parts.len() != 3 {
            return None;
        }
        let (header, payload, sig) = (parts[0], parts[1], parts[2]);
        let signing = format!("{header}.{payload}");
        let expected = self.sign_base64url(&signing);
        if !fixed_time_eq(expected.as_bytes(), sig.as_bytes()) {
            return None;
        }

        let json_bytes = crypto::base64url_decode(payload)?;
        let map = parse_flat_json_object(&json_bytes)?;

        if map.get("typ").map(|v| v.as_str()) != Some(expected_type) {
            return None;
        }
        let subject = map.get("sub")?.clone();
        let exp_seconds: i64 = map.get("exp")?.parse().ok()?;
        let exp = DateTime::<Utc>::from_timestamp(exp_seconds, 0)?;
        if exp <= (self.clock)() {
            return None;
        }

        let mut extra = HashMap::new();
        for (k, v) in &map {
            if k == "typ" || k == "sub" || k == "exp" {
                continue;
            }
            extra.insert(k.clone(), v.clone());
        }
        Some(JwtPayload {
            subject,
            claims: extra,
            expires_at_utc: exp,
        })
    }

    fn encode_token(
        &self,
        subject: &str,
        typ: &str,
        expires: DateTime<Utc>,
        claims: Option<&HashMap<String, String>>,
    ) -> String {
        let header = r#"{"alg":"HS256","typ":"JWT"}"#;
        // Build the payload JSON. `sub`/`typ` are strings; `exp` is a number.
        // Extra claims are serialised as strings (matching the C# string values).
        let mut pairs: Vec<(String, String)> = vec![
            ("sub".into(), json_string(subject)),
            ("typ".into(), json_string(typ)),
            ("exp".into(), expires.timestamp().to_string()),
        ];
        if let Some(claims) = claims {
            // Deterministic order for reproducibility (the C# dictionary order is
            // insertion-based; here we sort so a given claim set always yields the
            // same token).
            let mut keys: Vec<&String> = claims.keys().collect();
            keys.sort();
            for k in keys {
                pairs.push((k.clone(), json_string(&claims[k])));
            }
        }
        let payload_json = format!(
            "{{{}}}",
            pairs
                .iter()
                .map(|(k, v)| format!("{}:{}", json_string(k), v))
                .collect::<Vec<_>>()
                .join(",")
        );

        let header_b = crypto::base64url_encode(header.as_bytes());
        let payload_b = crypto::base64url_encode(payload_json.as_bytes());
        let signing = format!("{header_b}.{payload_b}");
        let sig = self.sign_base64url(&signing);
        format!("{signing}.{sig}")
    }

    fn sign_base64url(&self, signing: &str) -> String {
        let mac = crypto::hmac_sha256(&self.secret, signing.as_bytes());
        crypto::base64url_encode(&mac)
    }
}

/// (3.3.0) Issued API key — store hashes only.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PacaApiKeyRecord {
    pub key_id: String,
    pub label: String,
    pub hashed_secret: String,
    pub created_at_utc: DateTime<Utc>,
    pub revoked_at_utc: Option<DateTime<Utc>>,
}

/// (3.3.0) API-key registry separate from JWT user auth.
pub struct PacaApiKeyAuthenticator {
    keys: Mutex<HashMap<String, PacaApiKeyRecord>>,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl PacaApiKeyAuthenticator {
    pub fn new() -> Self {
        Self::with_clock(Box::new(Utc::now))
    }

    pub fn with_clock(clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self {
            keys: Mutex::new(HashMap::new()),
            clock,
        }
    }

    /// (3.3.0) Generate a fresh key; the raw `secret` is returned ONCE for the
    /// caller to store. `rng` is the injected randomness source.
    pub fn issue(
        &self,
        label: &str,
        rng: &mut dyn ISecureRandom,
    ) -> Result<(PacaApiKeyRecord, String), WorkflowError> {
        require_non_blank(label, "label")?;
        let key_id = new_id();
        let raw = crypto::base64_standard(&rng.next_bytes(32));
        let secret = raw.trim_end_matches('=').to_owned();
        let hashed = Self::hash(&secret);
        let record = PacaApiKeyRecord {
            key_id: key_id.clone(),
            label: label.to_owned(),
            hashed_secret: hashed,
            created_at_utc: (self.clock)(),
            revoked_at_utc: None,
        };
        self.keys.lock().unwrap().insert(key_id, record.clone());
        Ok((record, secret))
    }

    /// (3.3.0) Verify an incoming key. Returns the record if valid and live.
    pub fn verify(&self, key_id: &str, presented_secret: &str) -> Option<PacaApiKeyRecord> {
        let keys = self.keys.lock().unwrap();
        let record = keys.get(key_id)?;
        if record.revoked_at_utc.is_some() {
            return None;
        }
        let hashed = Self::hash(presented_secret);
        if fixed_time_eq(hashed.as_bytes(), record.hashed_secret.as_bytes()) {
            Some(record.clone())
        } else {
            None
        }
    }

    /// (3.3.0) Revoke a key. Idempotent.
    pub fn revoke(&self, key_id: &str) {
        let now = (self.clock)();
        let mut keys = self.keys.lock().unwrap();
        if let Some(existing) = keys.get_mut(key_id) {
            if existing.revoked_at_utc.is_none() {
                existing.revoked_at_utc = Some(now);
            }
        }
    }

    fn hash(secret: &str) -> String {
        let digest = crypto::sha256(secret.as_bytes());
        crypto::base64_standard(&digest).trim_end_matches('=').to_owned()
    }
}

impl Default for PacaApiKeyAuthenticator {
    fn default() -> Self {
        Self::new()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Fresh 32-char hex-ish id (mirrors the C# `Guid.NewGuid().ToString("n")`).
fn new_id() -> String {
    uuid::Uuid::new_v4().simple().to_string()
}

/// Constant-time byte comparison (the C# `FixedTimeEquals` / `SlowEquals`).
fn fixed_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for i in 0..a.len() {
        diff |= a[i] ^ b[i];
    }
    diff == 0
}

/// Minimal JSON string encoder (escapes `"` `\` and control chars). Sufficient
/// for the ASCII tool names / claim values these types carry.
fn json_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// Parses a *flat* JSON object of the shape a JWT payload uses — string values
/// and integer values, one level deep — into a `String → String` map (integers
/// are stringified). Good enough to decode our own tokens; not a general JSON
/// parser. Returns `None` on malformed input.
fn parse_flat_json_object(bytes: &[u8]) -> Option<HashMap<String, String>> {
    let s = std::str::from_utf8(bytes).ok()?;
    let s = s.trim();
    let s = s.strip_prefix('{')?.strip_suffix('}')?;
    let mut map = HashMap::new();
    let chars: Vec<char> = s.chars().collect();
    let mut i = 0usize;
    let n = chars.len();

    let skip_ws = |i: &mut usize| {
        while *i < n && chars[*i].is_whitespace() {
            *i += 1;
        }
    };
    let parse_string = |i: &mut usize| -> Option<String> {
        if *i >= n || chars[*i] != '"' {
            return None;
        }
        *i += 1;
        let mut out = String::new();
        while *i < n {
            let c = chars[*i];
            *i += 1;
            match c {
                '"' => return Some(out),
                '\\' => {
                    if *i >= n {
                        return None;
                    }
                    let e = chars[*i];
                    *i += 1;
                    match e {
                        '"' => out.push('"'),
                        '\\' => out.push('\\'),
                        '/' => out.push('/'),
                        'n' => out.push('\n'),
                        'r' => out.push('\r'),
                        't' => out.push('\t'),
                        'u' => {
                            if *i + 4 > n {
                                return None;
                            }
                            let hex: String = chars[*i..*i + 4].iter().collect();
                            *i += 4;
                            let cp = u32::from_str_radix(&hex, 16).ok()?;
                            out.push(char::from_u32(cp)?);
                        }
                        _ => return None,
                    }
                }
                c => out.push(c),
            }
        }
        None
    };

    loop {
        skip_ws(&mut i);
        if i >= n {
            break;
        }
        let key = parse_string(&mut i)?;
        skip_ws(&mut i);
        if i >= n || chars[i] != ':' {
            return None;
        }
        i += 1;
        skip_ws(&mut i);
        if i >= n {
            return None;
        }
        let value = if chars[i] == '"' {
            parse_string(&mut i)?
        } else {
            // Number / bool / null token — read until `,` or end.
            let start = i;
            while i < n && chars[i] != ',' && !chars[i].is_whitespace() {
                i += 1;
            }
            chars[start..i].iter().collect::<String>()
        };
        map.insert(key, value);
        skip_ws(&mut i);
        if i < n && chars[i] == ',' {
            i += 1;
            continue;
        }
        break;
    }
    Some(map)
}

// ═════════════════════════════════════════════════════════════════════════════
// crypto — self-contained SHA-256 + HMAC-SHA256 + base64 (ported verbatim so the
// crate needs no crypto dependency, mirroring the Distribution hashing submodule).
// ═════════════════════════════════════════════════════════════════════════════

mod crypto {
    //! FIPS 180-4 SHA-256 + RFC 2104 HMAC-SHA256, plus base64 (standard + URL).
    //! Pure `std`, no external crates. The digest matches
    //! `System.Security.Cryptography.SHA256`/`HMACSHA256`.

    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
        0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
        0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
        0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
        0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
        0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
        0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];

    const H0: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];

    /// Compute the SHA-256 digest of `data`.
    pub fn sha256(data: &[u8]) -> [u8; 32] {
        let mut h = H0;

        // Pre-processing: pad message.
        let bit_len = (data.len() as u64).wrapping_mul(8);
        let mut msg = data.to_vec();
        msg.push(0x80);
        while msg.len() % 64 != 56 {
            msg.push(0);
        }
        msg.extend_from_slice(&bit_len.to_be_bytes());

        for chunk in msg.chunks_exact(64) {
            let mut w = [0u32; 64];
            for (i, word) in w.iter_mut().enumerate().take(16) {
                let j = i * 4;
                *word = u32::from_be_bytes([chunk[j], chunk[j + 1], chunk[j + 2], chunk[j + 3]]);
            }
            for i in 16..64 {
                let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
                let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16]
                    .wrapping_add(s0)
                    .wrapping_add(w[i - 7])
                    .wrapping_add(s1);
            }

            let (mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut hh) =
                (h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7]);

            for i in 0..64 {
                let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
                let ch = (e & f) ^ ((!e) & g);
                let t1 = hh
                    .wrapping_add(s1)
                    .wrapping_add(ch)
                    .wrapping_add(K[i])
                    .wrapping_add(w[i]);
                let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
                let maj = (a & b) ^ (a & c) ^ (b & c);
                let t2 = s0.wrapping_add(maj);
                hh = g;
                g = f;
                f = e;
                e = d.wrapping_add(t1);
                d = c;
                c = b;
                b = a;
                a = t1.wrapping_add(t2);
            }

            h[0] = h[0].wrapping_add(a);
            h[1] = h[1].wrapping_add(b);
            h[2] = h[2].wrapping_add(c);
            h[3] = h[3].wrapping_add(d);
            h[4] = h[4].wrapping_add(e);
            h[5] = h[5].wrapping_add(f);
            h[6] = h[6].wrapping_add(g);
            h[7] = h[7].wrapping_add(hh);
        }

        let mut out = [0u8; 32];
        for (i, word) in h.iter().enumerate() {
            out[i * 4..i * 4 + 4].copy_from_slice(&word.to_be_bytes());
        }
        out
    }

    /// Compute HMAC-SHA256 of `message` under `key` (RFC 2104).
    pub fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
        const BLOCK: usize = 64;
        let mut k = if key.len() > BLOCK {
            sha256(key).to_vec()
        } else {
            key.to_vec()
        };
        k.resize(BLOCK, 0);

        let mut i_pad = [0u8; BLOCK];
        let mut o_pad = [0u8; BLOCK];
        for i in 0..BLOCK {
            i_pad[i] = k[i] ^ 0x36;
            o_pad[i] = k[i] ^ 0x5c;
        }

        let mut inner = Vec::with_capacity(BLOCK + message.len());
        inner.extend_from_slice(&i_pad);
        inner.extend_from_slice(message);
        let inner_hash = sha256(&inner);

        let mut outer = Vec::with_capacity(BLOCK + 32);
        outer.extend_from_slice(&o_pad);
        outer.extend_from_slice(&inner_hash);
        sha256(&outer)
    }

    const STD: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// Standard base64 (with `+/` and `=` padding), like `Convert.ToBase64String`.
    pub fn base64_standard(data: &[u8]) -> String {
        encode_base64(data, STD, true)
    }

    /// base64url encode without padding (the C# `Base64UrlEncode`).
    pub fn base64url_encode(data: &[u8]) -> String {
        let std = base64_standard(data);
        std.trim_end_matches('=').replace('+', "-").replace('/', "_")
    }

    /// base64url decode (the C# `Base64UrlDecode`: `-_` → `+/`, re-pad, decode).
    pub fn base64url_decode(input: &str) -> Option<Vec<u8>> {
        let mut s = input.replace('-', "+").replace('_', "/");
        match s.len() % 4 {
            2 => s.push_str("=="),
            3 => s.push('='),
            _ => {}
        }
        decode_base64(&s)
    }

    fn encode_base64(data: &[u8], alphabet: &[u8; 64], pad: bool) -> String {
        let mut out = String::new();
        for chunk in data.chunks(3) {
            let b0 = chunk[0] as u32;
            let b1 = *chunk.get(1).unwrap_or(&0) as u32;
            let b2 = *chunk.get(2).unwrap_or(&0) as u32;
            let n = (b0 << 16) | (b1 << 8) | b2;
            out.push(alphabet[((n >> 18) & 63) as usize] as char);
            out.push(alphabet[((n >> 12) & 63) as usize] as char);
            if chunk.len() > 1 {
                out.push(alphabet[((n >> 6) & 63) as usize] as char);
            } else if pad {
                out.push('=');
            }
            if chunk.len() > 2 {
                out.push(alphabet[(n & 63) as usize] as char);
            } else if pad {
                out.push('=');
            }
        }
        out
    }

    fn decode_base64(s: &str) -> Option<Vec<u8>> {
        fn val(c: u8) -> Option<u32> {
            match c {
                b'A'..=b'Z' => Some((c - b'A') as u32),
                b'a'..=b'z' => Some((c - b'a' + 26) as u32),
                b'0'..=b'9' => Some((c - b'0' + 52) as u32),
                b'+' => Some(62),
                b'/' => Some(63),
                _ => None,
            }
        }
        let bytes: Vec<u8> = s.bytes().filter(|&c| c != b'=').collect();
        let mut out = Vec::with_capacity(bytes.len() * 3 / 4);
        for chunk in bytes.chunks(4) {
            let mut n = 0u32;
            let mut count = 0;
            for &c in chunk {
                n = (n << 6) | val(c)?;
                count += 1;
            }
            // Left-align the accumulated bits.
            n <<= 6 * (4 - count);
            if count >= 2 {
                out.push((n >> 16) as u8);
            }
            if count >= 3 {
                out.push((n >> 8) as u8);
            }
            if count >= 4 {
                out.push(n as u8);
            }
        }
        Some(out)
    }
}
