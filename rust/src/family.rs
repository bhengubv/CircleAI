//! family — CircleAI family-board primitives.
//!
//! Full Rust port of `src/CircleAI.Family/FamilyPrimitives.cs`:
//!
//! - Records ([`FamilyMember`], [`FamilyEvent`], [`SharedExpense`]) +
//!   [`IFamilyBoard`] with the deterministic in-memory [`InMemoryFamilyBoard`]
//!   (member registry, shared events per member, shared-expense totals by member
//!   and by category).
//!
//! `decimal` money maps to [`f64`]. `DateTime DateOfBirth` (offset-less in the
//! C#) maps to [`DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Family) A family member.
///
/// Mirrors `sealed record FamilyMember(string MemberId, string Name,
/// string Role, DateTime DateOfBirth)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FamilyMember {
    pub member_id: String,
    pub name: String,
    pub role: String,
    pub date_of_birth: DateTime<Utc>,
}

impl FamilyMember {
    /// Constructs a member, mirroring the positional C# record constructor.
    pub fn new(
        member_id: impl Into<String>,
        name: impl Into<String>,
        role: impl Into<String>,
        date_of_birth: DateTime<Utc>,
    ) -> Self {
        Self {
            member_id: member_id.into(),
            name: name.into(),
            role: role.into(),
            date_of_birth,
        }
    }
}

/// (Family) A shared family event.
///
/// Mirrors `sealed record FamilyEvent(string EventId, string Title,
/// DateTimeOffset AtUtc, IReadOnlyList<string> MemberIds)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FamilyEvent {
    pub event_id: String,
    pub title: String,
    pub at_utc: DateTime<Utc>,
    pub member_ids: Vec<String>,
}

impl FamilyEvent {
    /// Constructs an event, mirroring the positional C# record constructor.
    pub fn new(
        event_id: impl Into<String>,
        title: impl Into<String>,
        at_utc: DateTime<Utc>,
        member_ids: Vec<String>,
    ) -> Self {
        Self {
            event_id: event_id.into(),
            title: title.into(),
            at_utc,
            member_ids,
        }
    }
}

/// (Family) A shared expense.
///
/// Mirrors `sealed record SharedExpense(string ExpenseId, string PaidById,
/// decimal Amount, string Currency, string Category, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct SharedExpense {
    pub expense_id: String,
    pub paid_by_id: String,
    pub amount: f64,
    pub currency: String,
    pub category: String,
    pub at_utc: DateTime<Utc>,
}

impl SharedExpense {
    /// Constructs an expense, mirroring the positional C# record constructor.
    pub fn new(
        expense_id: impl Into<String>,
        paid_by_id: impl Into<String>,
        amount: f64,
        currency: impl Into<String>,
        category: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            expense_id: expense_id.into(),
            paid_by_id: paid_by_id.into(),
            amount,
            currency: currency.into(),
            category: category.into(),
            at_utc,
        }
    }
}

/// (Family) The family board contract.
///
/// Mirrors `interface IFamilyBoard`.
pub trait IFamilyBoard {
    /// Adds (or overwrites) a member.
    fn add(&self, m: FamilyMember);
    /// Looks up a member by id.
    fn get_member(&self, id: &str) -> Option<FamilyMember>;
    /// All members, ordered by name ascending.
    fn members(&self) -> Vec<FamilyMember>;
    /// Schedules (or overwrites) an event.
    fn schedule(&self, e: FamilyEvent);
    /// Events that include `member_id`, ordered by time ascending.
    fn events_for_member(&self, member_id: &str) -> Vec<FamilyEvent>;
    /// Records a shared expense.
    fn record(&self, e: SharedExpense);
    /// Total amount paid by `member_id` at/after `since`.
    fn total_paid_by(&self, member_id: &str, since: DateTime<Utc>) -> f64;
    /// Total spend in `category` (case-insensitive) at/after `since`.
    fn spend_by_category(&self, category: &str, since: DateTime<Utc>) -> f64;
}

/// (Family) In-memory [`IFamilyBoard`].
///
/// Mirrors `sealed class InMemoryFamilyBoard`.
pub struct InMemoryFamilyBoard {
    members: Mutex<HashMap<String, FamilyMember>>,
    events: Mutex<HashMap<String, FamilyEvent>>,
    expenses: Mutex<Vec<SharedExpense>>,
}

impl InMemoryFamilyBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            members: Mutex::new(HashMap::new()),
            events: Mutex::new(HashMap::new()),
            expenses: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryFamilyBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IFamilyBoard for InMemoryFamilyBoard {
    fn add(&self, m: FamilyMember) {
        self.members.lock().unwrap().insert(m.member_id.clone(), m);
    }

    fn get_member(&self, id: &str) -> Option<FamilyMember> {
        self.members.lock().unwrap().get(id).cloned()
    }

    fn members(&self) -> Vec<FamilyMember> {
        let mut out: Vec<FamilyMember> = self.members.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    fn schedule(&self, e: FamilyEvent) {
        self.events.lock().unwrap().insert(e.event_id.clone(), e);
    }

    fn events_for_member(&self, member_id: &str) -> Vec<FamilyEvent> {
        let mut out: Vec<FamilyEvent> = self
            .events
            .lock()
            .unwrap()
            .values()
            .filter(|e| e.member_ids.iter().any(|m| m == member_id))
            .cloned()
            .collect();
        out.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        out
    }

    fn record(&self, e: SharedExpense) {
        self.expenses.lock().unwrap().push(e);
    }

    fn total_paid_by(&self, member_id: &str, since: DateTime<Utc>) -> f64 {
        self.expenses
            .lock()
            .unwrap()
            .iter()
            .filter(|e| e.paid_by_id == member_id && e.at_utc >= since)
            .map(|e| e.amount)
            .sum()
    }

    fn spend_by_category(&self, category: &str, since: DateTime<Utc>) -> f64 {
        self.expenses
            .lock()
            .unwrap()
            .iter()
            .filter(|e| e.category.eq_ignore_ascii_case(category) && e.at_utc >= since)
            .map(|e| e.amount)
            .sum()
    }
}
