//! tool_catalog.rs
//!
//! (2.0.3) The searchable registry of every tool the host knows about. Ported
//! from `Tools/IToolDescriptor.cs`, `Tools/IToolCatalog.cs`, and
//! `Tools/InMemoryToolCatalog.cs`. Providers register their descriptors here at
//! startup; executions route through [`IToolExecutor`].
//!
//! The C# catalog methods are a mix of async (`Upsert`/`Remove`/`Get`) and sync
//! (`List`/`Search`/`ListByProvider`); the whole port is synchronous.

use std::collections::HashMap;
use std::sync::Mutex;

/// Describes one tool callable by an LLM. Data-only — execution lives in
/// [`IToolExecutor`]. 1:1 with the C# `ToolDescriptor` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolDescriptor {
    /// Stable identifier, e.g. `"gmail.send"`. Unique within a catalog.
    pub name: String,
    /// One- or two-line summary the model reads to decide whether to call.
    pub description: String,
    /// Plug-in id that owns this tool, e.g. `"gmail"` / `"github"` / `"local"`.
    pub provider: String,
    /// JSON Schema for the argument object. Empty string when arg-less.
    pub json_schema: String,
    /// How auth is brokered: `"none"`, `"oauth2"`, `"api-key"`, `"host"`.
    pub auth_scheme: String,
    /// Free-form tags for filtering.
    pub tags: Option<Vec<String>>,
    /// Optional natural-language examples surfaced during search.
    pub examples: Option<Vec<String>>,
}

impl ToolDescriptor {
    /// Constructs with the C# record defaults (`json_schema = ""`,
    /// `auth_scheme = "none"`, `tags = None`, `examples = None`).
    pub fn new(
        name: impl Into<String>,
        description: impl Into<String>,
        provider: impl Into<String>,
    ) -> Self {
        Self {
            name: name.into(),
            description: description.into(),
            provider: provider.into(),
            json_schema: String::new(),
            auth_scheme: "none".to_string(),
            tags: None,
            examples: None,
        }
    }

    pub fn with_json_schema(mut self, schema: impl Into<String>) -> Self {
        self.json_schema = schema.into();
        self
    }

    pub fn with_auth_scheme(mut self, scheme: impl Into<String>) -> Self {
        self.auth_scheme = scheme.into();
        self
    }

    pub fn with_tags(mut self, tags: Vec<String>) -> Self {
        self.tags = Some(tags);
        self
    }

    pub fn with_examples(mut self, examples: Vec<String>) -> Self {
        self.examples = Some(examples);
        self
    }
}

/// Result of one tool execution. 1:1 with the C# `ToolExecutionResult` record.
#[derive(Debug, Clone, PartialEq)]
pub struct ToolExecutionResult {
    /// Whether the underlying call succeeded.
    pub success: bool,
    /// The result payload on success (JSON value).
    pub result: Option<serde_json::Value>,
    /// The failure reason when `success` is false.
    pub error: Option<String>,
    /// Wall-clock duration in milliseconds.
    pub duration_ms: i64,
}

impl ToolExecutionResult {
    pub fn ok(result: Option<serde_json::Value>, duration_ms: i64) -> Self {
        Self {
            success: true,
            result,
            error: None,
            duration_ms,
        }
    }

    pub fn failure(error: impl Into<String>, duration_ms: i64) -> Self {
        Self {
            success: false,
            result: None,
            error: Some(error.into()),
            duration_ms,
        }
    }
}

/// (2.0.3) The CircleAI tool catalog. Searchable by name, tag, and
/// natural-language query. 1:1 with the C# `IToolCatalog`.
pub trait IToolCatalog: Send + Sync {
    /// How many tools are currently registered.
    fn count(&self) -> usize;

    /// Register or replace one tool. Idempotent for the same name.
    fn upsert(&self, descriptor: ToolDescriptor);

    /// Remove a tool by name. Returns whether one was removed.
    fn remove(&self, name: &str) -> bool;

    /// Get exactly one descriptor by name, or `None` when unknown.
    fn get(&self, name: &str) -> Option<ToolDescriptor>;

    /// Enumerate every registered descriptor (stable order within a process).
    fn list(&self) -> Vec<ToolDescriptor>;

    /// Free-form keyword-substring search over name + description + tags.
    fn search(&self, query: &str, top_k: usize) -> Vec<ToolDescriptor>;

    /// Filter by provider id (exact match, case-insensitive).
    fn list_by_provider(&self, provider: &str) -> Vec<ToolDescriptor>;
}

/// A source of tools — vendored integrations, MCP server, AetherNet peer. 1:1
/// with the C# `IToolProvider`.
pub trait IToolProvider: Send + Sync {
    /// Stable provider id, e.g. `"local"` / `"composio"` / `"mcp"`.
    fn provider_id(&self) -> &str;

    /// Discover every tool this provider exposes.
    fn discover(&self) -> Vec<ToolDescriptor>;

    /// Cheap availability probe.
    fn is_available(&self) -> bool;
}

/// Sandboxed execution surface. 1:1 with the C# `IToolExecutor`.
pub trait IToolExecutor: Send + Sync {
    /// Execute one tool call. `arguments_json` is the model-emitted JSON object;
    /// the executor validates against [`ToolDescriptor::json_schema`] before
    /// dispatch.
    fn execute(&self, tool: &ToolDescriptor, arguments_json: &str) -> ToolExecutionResult;
}

/// (2.0.3) Default [`IToolCatalog`] — in-memory + keyword-substring search.
/// 1:1 with the C# `InMemoryToolCatalog` (ordinal-case-insensitive keys).
#[derive(Default)]
pub struct InMemoryToolCatalog {
    by_name: Mutex<HashMap<String, ToolDescriptor>>,
}

impl InMemoryToolCatalog {
    /// Returns an empty catalog.
    pub fn new() -> Self {
        Self::default()
    }

    fn score_match(d: &ToolDescriptor, terms: &[&str]) -> i32 {
        let name = d.name.to_lowercase();
        let desc = d.description.to_lowercase();
        let tag_blob = d
            .tags
            .as_ref()
            .map(|t| t.join(" "))
            .unwrap_or_default()
            .to_lowercase();

        let mut score = 0;
        for t in terms {
            let tl = t.to_lowercase();
            if name.contains(&tl) {
                score += 5;
            }
            if desc.contains(&tl) {
                score += 2;
            }
            if tag_blob.contains(&tl) {
                score += 3;
            }
        }
        score
    }
}

/// Case-insensitive ordinal comparison for the secondary name sort.
fn ci_ordinal(a: &str, b: &str) -> std::cmp::Ordering {
    a.to_lowercase().cmp(&b.to_lowercase())
}

impl IToolCatalog for InMemoryToolCatalog {
    fn count(&self) -> usize {
        self.by_name.lock().unwrap().len()
    }

    fn upsert(&self, descriptor: ToolDescriptor) {
        assert!(!descriptor.name.trim().is_empty(), "descriptor.Name required");
        self.by_name
            .lock()
            .unwrap()
            .insert(descriptor.name.to_lowercase(), descriptor);
    }

    fn remove(&self, name: &str) -> bool {
        assert!(!name.trim().is_empty(), "name required");
        self.by_name
            .lock()
            .unwrap()
            .remove(&name.to_lowercase())
            .is_some()
    }

    fn get(&self, name: &str) -> Option<ToolDescriptor> {
        if name.trim().is_empty() {
            return None;
        }
        self.by_name.lock().unwrap().get(&name.to_lowercase()).cloned()
    }

    fn list(&self) -> Vec<ToolDescriptor> {
        let mut v: Vec<ToolDescriptor> = self.by_name.lock().unwrap().values().cloned().collect();
        v.sort_by(|a, b| ci_ordinal(&a.name, &b.name));
        v
    }

    fn search(&self, query: &str, top_k: usize) -> Vec<ToolDescriptor> {
        if query.trim().is_empty() || top_k == 0 {
            return Vec::new();
        }
        let terms: Vec<&str> = query.split(' ').map(|s| s.trim()).filter(|s| !s.is_empty()).collect();

        let mut scored: Vec<(i32, ToolDescriptor)> = self
            .by_name
            .lock()
            .unwrap()
            .values()
            .map(|d| (Self::score_match(d, &terms), d.clone()))
            .filter(|(s, _)| *s > 0)
            .collect();

        // Order by score desc, then name asc (ordinal case-insensitive).
        scored.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| ci_ordinal(&a.1.name, &b.1.name)));
        scored.into_iter().take(top_k).map(|(_, d)| d).collect()
    }

    fn list_by_provider(&self, provider: &str) -> Vec<ToolDescriptor> {
        assert!(!provider.trim().is_empty(), "provider required");
        let mut v: Vec<ToolDescriptor> = self
            .by_name
            .lock()
            .unwrap()
            .values()
            .filter(|d| d.provider.eq_ignore_ascii_case(provider))
            .cloned()
            .collect();
        v.sort_by(|a, b| ci_ordinal(&a.name, &b.name));
        v
    }
}

/// Discover and import every tool from `provider` into `catalog`. Returns how
/// many were imported. 1:1 with the C# `ToolCatalogExtensions.ImportFromAsync`.
pub fn import_from(catalog: &dyn IToolCatalog, provider: &dyn IToolProvider) -> usize {
    let tools = provider.discover();
    let count = tools.len();
    for tool in tools {
        catalog.upsert(tool);
    }
    count
}
