//! legal_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the Legal vertical — Rust
//! port of `src/CircleAI.Legal/LegalPrimitives.cs`: matters, contracts,
//! deadlines, clause library.
//!
//! `DateTimeOffset OpenedAtUtc` → [`DateTime<Utc>`]; the calendar-date fields
//! (`EffectiveDate`, `ExpiryDate`, `DueOn`) are C# `DateTime` used date-wise, so
//! they map to [`NaiveDate`]. The C# `ConcurrentDictionary<string, T>` collapses
//! to `Mutex`-guarded `HashMap`s; ordering queries reproduce the .NET
//! `OrderBy`/`OrderByDescending` (stable).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, NaiveDate, Utc};

/// (3.3.0) A legal matter.
///
/// Mirrors `sealed record Matter(string MatterId, string Title,
/// string Jurisdiction, string Client, DateTimeOffset OpenedAtUtc, bool Open)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Matter {
    pub matter_id: String,
    pub title: String,
    pub jurisdiction: String,
    pub client: String,
    pub opened_at_utc: DateTime<Utc>,
    pub open: bool,
}

impl Matter {
    /// Constructs a matter, mirroring the positional C# record constructor.
    pub fn new(
        matter_id: impl Into<String>,
        title: impl Into<String>,
        jurisdiction: impl Into<String>,
        client: impl Into<String>,
        opened_at_utc: DateTime<Utc>,
        open: bool,
    ) -> Self {
        Self {
            matter_id: matter_id.into(),
            title: title.into(),
            jurisdiction: jurisdiction.into(),
            client: client.into(),
            opened_at_utc,
            open,
        }
    }
}

/// (3.3.0) A contract attached to a matter.
///
/// Mirrors `sealed record Contract(string ContractId, string MatterId,
/// string Title, DateTime EffectiveDate, DateTime? ExpiryDate,
/// IReadOnlyList<string> Counterparties)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Contract {
    pub contract_id: String,
    pub matter_id: String,
    pub title: String,
    pub effective_date: NaiveDate,
    pub expiry_date: Option<NaiveDate>,
    pub counterparties: Vec<String>,
}

impl Contract {
    /// Constructs a contract, mirroring the positional C# record constructor.
    pub fn new(
        contract_id: impl Into<String>,
        matter_id: impl Into<String>,
        title: impl Into<String>,
        effective_date: NaiveDate,
        expiry_date: Option<NaiveDate>,
        counterparties: Vec<String>,
    ) -> Self {
        Self {
            contract_id: contract_id.into(),
            matter_id: matter_id.into(),
            title: title.into(),
            effective_date,
            expiry_date,
            counterparties,
        }
    }
}

/// (3.3.0) A legal deadline.
///
/// Mirrors `sealed record LegalDeadline(string DeadlineId, string MatterId,
/// string Description, DateTime DueOn)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LegalDeadline {
    pub deadline_id: String,
    pub matter_id: String,
    pub description: String,
    pub due_on: NaiveDate,
}

impl LegalDeadline {
    /// Constructs a deadline, mirroring the positional C# record constructor.
    pub fn new(
        deadline_id: impl Into<String>,
        matter_id: impl Into<String>,
        description: impl Into<String>,
        due_on: NaiveDate,
    ) -> Self {
        Self {
            deadline_id: deadline_id.into(),
            matter_id: matter_id.into(),
            description: description.into(),
            due_on,
        }
    }
}

/// (3.3.0) A reusable clause.
///
/// Mirrors `sealed record Clause(string ClauseId, string Title, string Body,
/// IReadOnlyList<string> Tags)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Clause {
    pub clause_id: String,
    pub title: String,
    pub body: String,
    pub tags: Vec<String>,
}

impl Clause {
    /// Constructs a clause, mirroring the positional C# record constructor.
    pub fn new(
        clause_id: impl Into<String>,
        title: impl Into<String>,
        body: impl Into<String>,
        tags: Vec<String>,
    ) -> Self {
        Self {
            clause_id: clause_id.into(),
            title: title.into(),
            body: body.into(),
            tags,
        }
    }
}

/// (3.3.0) The Legal board contract.
///
/// Mirrors `interface ILegalBoard`. The `ActiveMatters` getter becomes
/// [`active_matters`](ILegalBoard::active_matters).
pub trait ILegalBoard {
    /// Opens (or overwrites) a matter.
    fn open(&self, m: Matter);
    /// Marks a matter closed. Panics on an unknown id (C#
    /// `InvalidOperationException`).
    fn close(&self, matter_id: &str);
    /// Looks up a matter by id.
    fn get_matter(&self, id: &str) -> Option<Matter>;
    /// Open matters, newest-opened first.
    fn active_matters(&self) -> Vec<Matter>;
    /// Adds (or overwrites) a contract.
    fn add_contract(&self, c: Contract);
    /// Contracts with an expiry date at or before `date`, soonest-expiry first.
    fn contracts_expiring_before(&self, date: NaiveDate) -> Vec<Contract>;
    /// Adds (or overwrites) a deadline.
    fn add(&self, d: LegalDeadline);
    /// Deadlines due at or after `now`, soonest-first.
    fn upcoming_deadlines(&self, now: NaiveDate) -> Vec<LegalDeadline>;
    /// Adds (or overwrites) a clause.
    fn add_clause(&self, c: Clause);
    /// Clauses tagged `tag` (case-insensitive). Panics on a blank tag (C#
    /// `ArgumentException`).
    fn clauses_by_tag(&self, tag: &str) -> Vec<Clause>;
}

/// (3.3.0) In-memory [`ILegalBoard`].
pub struct InMemoryLegalBoard {
    matters: Mutex<HashMap<String, Matter>>,
    contracts: Mutex<HashMap<String, Contract>>,
    deadlines: Mutex<HashMap<String, LegalDeadline>>,
    clauses: Mutex<HashMap<String, Clause>>,
}

impl InMemoryLegalBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            matters: Mutex::new(HashMap::new()),
            contracts: Mutex::new(HashMap::new()),
            deadlines: Mutex::new(HashMap::new()),
            clauses: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryLegalBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ILegalBoard for InMemoryLegalBoard {
    fn open(&self, m: Matter) {
        self.matters.lock().unwrap().insert(m.matter_id.clone(), m);
    }

    fn close(&self, matter_id: &str) {
        let mut matters = self.matters.lock().unwrap();
        match matters.get(matter_id) {
            Some(m) => {
                let updated = Matter {
                    open: false,
                    ..m.clone()
                };
                matters.insert(matter_id.to_string(), updated);
            }
            None => panic!("Unknown matter {matter_id}"),
        }
    }

    fn get_matter(&self, id: &str) -> Option<Matter> {
        self.matters.lock().unwrap().get(id).cloned()
    }

    fn active_matters(&self) -> Vec<Matter> {
        let mut out: Vec<Matter> = self
            .matters
            .lock()
            .unwrap()
            .values()
            .filter(|m| m.open)
            .cloned()
            .collect();
        out.sort_by(|a, b| b.opened_at_utc.cmp(&a.opened_at_utc));
        out
    }

    fn add_contract(&self, c: Contract) {
        self.contracts
            .lock()
            .unwrap()
            .insert(c.contract_id.clone(), c);
    }

    fn contracts_expiring_before(&self, date: NaiveDate) -> Vec<Contract> {
        let mut out: Vec<Contract> = self
            .contracts
            .lock()
            .unwrap()
            .values()
            .filter(|c| c.expiry_date.map(|e| e <= date).unwrap_or(false))
            .cloned()
            .collect();
        // OrderBy(c => c.ExpiryDate) — all filtered rows have Some(expiry).
        out.sort_by(|a, b| a.expiry_date.cmp(&b.expiry_date));
        out
    }

    fn add(&self, d: LegalDeadline) {
        self.deadlines
            .lock()
            .unwrap()
            .insert(d.deadline_id.clone(), d);
    }

    fn upcoming_deadlines(&self, now: NaiveDate) -> Vec<LegalDeadline> {
        let mut out: Vec<LegalDeadline> = self
            .deadlines
            .lock()
            .unwrap()
            .values()
            .filter(|d| d.due_on >= now)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.due_on.cmp(&b.due_on));
        out
    }

    fn add_clause(&self, c: Clause) {
        self.clauses.lock().unwrap().insert(c.clause_id.clone(), c);
    }

    fn clauses_by_tag(&self, tag: &str) -> Vec<Clause> {
        if tag.trim().is_empty() {
            panic!("tag required");
        }
        self.clauses
            .lock()
            .unwrap()
            .values()
            .filter(|c| c.tags.iter().any(|t| t.eq_ignore_ascii_case(tag)))
            .cloned()
            .collect()
    }
}
