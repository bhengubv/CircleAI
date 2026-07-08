//! primitives.rs
//!
//! Shared shapes for the proactive scheduling surface — `ProactiveTask`,
//! `ProactiveTrigger`, `ProactiveTaskRunResult`, `ProactiveTaskLoadError`. Ported
//! 1:1 from `Primitives.cs`.
//!
//! A `ProactiveTask.payload` is opaque to the substrate (the C# `object`); it is
//! modelled here as `Arc<dyn Any + Send + Sync>` — the substrate never inspects
//! it, only the host's runner downcasts it.

use std::any::Any;
use std::sync::Arc;

/// How a task fires. Exactly one of `cron`, `on_event`, or `manual` is set.
#[derive(Clone, Default)]
pub struct ProactiveTrigger {
    /// 5-field cron expression — see [`crate::proactive::CronExpression`].
    pub cron: Option<String>,
    /// Event name (e.g. "note-saved", "task-created").
    pub on_event: Option<String>,
    /// `true` if the task only fires when explicitly invoked.
    pub manual: bool,
}

impl ProactiveTrigger {
    /// A cron trigger.
    pub fn cron(expr: impl Into<String>) -> Self {
        Self {
            cron: Some(expr.into()),
            on_event: None,
            manual: false,
        }
    }

    /// An event trigger.
    pub fn on_event(name: impl Into<String>) -> Self {
        Self {
            cron: None,
            on_event: Some(name.into()),
            manual: false,
        }
    }

    /// A manual-only trigger.
    pub fn manual() -> Self {
        Self {
            cron: None,
            on_event: None,
            manual: true,
        }
    }
}

impl std::fmt::Debug for ProactiveTrigger {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ProactiveTrigger")
            .field("cron", &self.cron)
            .field("on_event", &self.on_event)
            .field("manual", &self.manual)
            .finish()
    }
}

/// One scheduled task. Opaque from the substrate's perspective — the host's
/// [`super::IProactiveTaskRunner`] reads the `payload` and executes it.
#[derive(Clone)]
pub struct ProactiveTask {
    /// Unique task id within its source. Used for last-run tracking.
    pub id: String,
    /// Cron / event / manual trigger.
    pub trigger: ProactiveTrigger,
    /// Consumer-owned object. The substrate never inspects it.
    pub payload: Arc<dyn Any + Send + Sync>,
    /// Optional context tag (vault path, tenant id, …) so multi-tenant sources
    /// keep per-context last-run state separate.
    pub source_context: Option<String>,
}

impl ProactiveTask {
    /// Creates a task with the given payload.
    pub fn new(
        id: impl Into<String>,
        trigger: ProactiveTrigger,
        payload: Arc<dyn Any + Send + Sync>,
    ) -> Self {
        Self {
            id: id.into(),
            trigger,
            payload,
            source_context: None,
        }
    }

    /// Sets the source context (builder-style).
    pub fn with_source_context(mut self, ctx: impl Into<String>) -> Self {
        self.source_context = Some(ctx.into());
        self
    }
}

impl std::fmt::Debug for ProactiveTask {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ProactiveTask")
            .field("id", &self.id)
            .field("trigger", &self.trigger)
            .field("source_context", &self.source_context)
            .finish()
    }
}

/// One run outcome — success or failure with a message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProactiveTaskRunResult {
    pub task_id: String,
    pub success: bool,
    pub failure_message: Option<String>,
}

impl ProactiveTaskRunResult {
    /// A successful run.
    pub fn success(task_id: impl Into<String>) -> Self {
        Self {
            task_id: task_id.into(),
            success: true,
            failure_message: None,
        }
    }

    /// A failed run with a message.
    pub fn failure(task_id: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            task_id: task_id.into(),
            success: false,
            failure_message: Some(message.into()),
        }
    }
}

/// One parse failure surfaced through the source.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProactiveTaskLoadError {
    pub task_id: String,
    pub message: String,
    pub source_context: Option<String>,
}

impl ProactiveTaskLoadError {
    pub fn new(task_id: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            task_id: task_id.into(),
            message: message.into(),
            source_context: None,
        }
    }
}
