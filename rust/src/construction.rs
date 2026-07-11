//! construction — CircleAI construction-board primitives.
//!
//! Full Rust port of `src/CircleAI.Construction/ConstructionPrimitives.cs`:
//!
//! - Records [`Project`] / [`ConstructionTask`] / [`CostEntry`], the
//!   [`IConstructionBoard`] contract, and the deterministic in-memory
//!   [`InMemoryConstructionBoard`] (projects + tasks + cost tracking + remaining
//!   budget).
//!
//! Sync-only; `decimal Budget/Amount` → `f64`; `DateTime`/`DateTimeOffset`/
//! `DateTime?` → [`chrono::DateTime<Utc>`] / `Option<DateTime<Utc>>`. `Project`
//! is re-exported at the crate root as `ConstructionProject` to avoid clashing
//! with `real_estate` types.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Construction) A construction project.
///
/// Mirrors `sealed record Project(string ProjectId, string Name,
/// DateTime StartOn, DateTime? EndOn, decimal Budget, string Currency)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Project {
    pub project_id: String,
    pub name: String,
    pub start_on: DateTime<Utc>,
    pub end_on: Option<DateTime<Utc>>,
    pub budget: f64,
    pub currency: String,
}

impl Project {
    /// Constructs a project, mirroring the positional C# record constructor.
    pub fn new(
        project_id: impl Into<String>,
        name: impl Into<String>,
        start_on: DateTime<Utc>,
        end_on: Option<DateTime<Utc>>,
        budget: f64,
        currency: impl Into<String>,
    ) -> Self {
        Self {
            project_id: project_id.into(),
            name: name.into(),
            start_on,
            end_on,
            budget,
            currency: currency.into(),
        }
    }
}

/// (Construction) A task within a project.
///
/// Mirrors `sealed record ConstructionTask(string ConstructionTaskId,
/// string ProjectId, string Description, DateTime DueOn, bool Completed)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConstructionTask {
    pub construction_task_id: String,
    pub project_id: String,
    pub description: String,
    pub due_on: DateTime<Utc>,
    pub completed: bool,
}

impl ConstructionTask {
    /// Constructs a task, mirroring the positional C# record constructor.
    pub fn new(
        construction_task_id: impl Into<String>,
        project_id: impl Into<String>,
        description: impl Into<String>,
        due_on: DateTime<Utc>,
        completed: bool,
    ) -> Self {
        Self {
            construction_task_id: construction_task_id.into(),
            project_id: project_id.into(),
            description: description.into(),
            due_on,
            completed,
        }
    }
}

/// (Construction) A recorded cost entry.
///
/// Mirrors `sealed record CostEntry(string EntryId, string ProjectId,
/// string Category, decimal Amount, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct CostEntry {
    pub entry_id: String,
    pub project_id: String,
    pub category: String,
    pub amount: f64,
    pub at_utc: DateTime<Utc>,
}

impl CostEntry {
    /// Constructs a cost entry, mirroring the positional C# record constructor.
    pub fn new(
        entry_id: impl Into<String>,
        project_id: impl Into<String>,
        category: impl Into<String>,
        amount: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            entry_id: entry_id.into(),
            project_id: project_id.into(),
            category: category.into(),
            amount,
            at_utc,
        }
    }
}

/// (Construction) The construction-board contract.
///
/// Mirrors `interface IConstructionBoard`.
pub trait IConstructionBoard {
    /// Creates (or overwrites) a project.
    fn create(&self, p: Project);
    /// A project by id, if any.
    fn get_project(&self, id: &str) -> Option<Project>;
    /// Adds (or overwrites) a task.
    fn add(&self, t: ConstructionTask);
    /// Marks a task complete. Panics on an unknown task id (mirrors the C#
    /// `InvalidOperationException`).
    fn complete(&self, task_id: &str);
    /// Open (incomplete) tasks for a project, earliest due first.
    fn open_construction_tasks_for(&self, project_id: &str) -> Vec<ConstructionTask>;
    /// Records a cost entry.
    fn record_cost(&self, c: CostEntry);
    /// Total spend on a project.
    fn spend_for(&self, project_id: &str) -> f64;
    /// Budget minus spend for a project. Panics on an unknown project id (mirrors
    /// the C# `InvalidOperationException`).
    fn remaining_budget(&self, project_id: &str) -> f64;
}

/// (Construction) In-memory [`IConstructionBoard`].
///
/// Mirrors `sealed class InMemoryConstructionBoard`.
pub struct InMemoryConstructionBoard {
    projects: Mutex<HashMap<String, Project>>,
    tasks: Mutex<HashMap<String, ConstructionTask>>,
    costs: Mutex<Vec<CostEntry>>,
}

impl InMemoryConstructionBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            projects: Mutex::new(HashMap::new()),
            tasks: Mutex::new(HashMap::new()),
            costs: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryConstructionBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IConstructionBoard for InMemoryConstructionBoard {
    fn create(&self, p: Project) {
        self.projects.lock().unwrap().insert(p.project_id.clone(), p);
    }

    fn get_project(&self, id: &str) -> Option<Project> {
        self.projects.lock().unwrap().get(id).cloned()
    }

    fn add(&self, t: ConstructionTask) {
        self.tasks.lock().unwrap().insert(t.construction_task_id.clone(), t);
    }

    fn complete(&self, task_id: &str) {
        let mut tasks = self.tasks.lock().unwrap();
        match tasks.get(task_id) {
            Some(t) => {
                let updated = ConstructionTask {
                    completed: true,
                    ..t.clone()
                };
                tasks.insert(task_id.to_string(), updated);
            }
            None => panic!("Unknown task {task_id}"),
        }
    }

    fn open_construction_tasks_for(&self, project_id: &str) -> Vec<ConstructionTask> {
        let mut hits: Vec<ConstructionTask> = self
            .tasks
            .lock()
            .unwrap()
            .values()
            .filter(|t| t.project_id == project_id && !t.completed)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.due_on.cmp(&b.due_on));
        hits
    }

    fn record_cost(&self, c: CostEntry) {
        self.costs.lock().unwrap().push(c);
    }

    fn spend_for(&self, project_id: &str) -> f64 {
        self.costs
            .lock()
            .unwrap()
            .iter()
            .filter(|c| c.project_id == project_id)
            .map(|c| c.amount)
            .sum()
    }

    fn remaining_budget(&self, project_id: &str) -> f64 {
        let budget = {
            let projects = self.projects.lock().unwrap();
            match projects.get(project_id) {
                Some(p) => p.budget,
                None => panic!("Unknown project {project_id}"),
            }
        };
        budget - self.spend_for(project_id)
    }
}
