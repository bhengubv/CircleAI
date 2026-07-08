//! proactive — the CircleAI.Companion.Proactive scheduling substrate.
//!
//! A port of the C# `CircleAI.Companion.Proactive` project: a generic proactive
//! scheduler (`IProactiveScheduler` + `ProactiveScheduler`) that owns cron
//! parsing, last-run tracking, and event dispatch, driving a host-supplied task
//! source (`IProactiveTaskSource`) and runner (`IProactiveTaskRunner`), plus the
//! standalone 5-field `CronExpression` parser and the null / in-memory / delegate
//! default implementations.

pub mod cron;
pub mod primitives;
pub mod scheduler;

pub use cron::{CronExpression, CronParseError};
pub use primitives::{
    ProactiveTask, ProactiveTaskLoadError, ProactiveTaskRunResult, ProactiveTrigger,
};
pub use scheduler::{
    DelegateProactiveTaskRunner, IProactiveScheduler, IProactiveTaskRunner, IProactiveTaskSource,
    InMemoryProactiveTaskSource, NullProactiveTaskRunner, NullProactiveTaskSource,
    ProactiveScheduler, ProactiveSchedulerOptions, Variables,
};
