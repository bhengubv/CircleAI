//! contracts.rs
//!
//! (2.8.0) CRM contracts — Rust port of `src/CircleAI.CRM/Contracts.cs`.
//!
//! The C# `ValueTask`-returning, `CancellationToken`-parameterised interfaces
//! collapse to synchronous traits here (the workspace Rust port is sync-only).
//! `decimal` money values become [`f64`] — there is no `System.Decimal` analogue
//! in the dependency set.

use chrono::{DateTime, Utc};

/// (CRM) A contact.
///
/// Mirrors `sealed record Contact(string ContactId, string FullName,
/// string? Email, string? Phone, string? CompanyId)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Contact {
    pub contact_id: String,
    pub full_name: String,
    pub email: Option<String>,
    pub phone: Option<String>,
    pub company_id: Option<String>,
}

impl Contact {
    /// Constructs a contact, mirroring the positional C# record constructor.
    pub fn new(
        contact_id: impl Into<String>,
        full_name: impl Into<String>,
        email: Option<String>,
        phone: Option<String>,
        company_id: Option<String>,
    ) -> Self {
        Self {
            contact_id: contact_id.into(),
            full_name: full_name.into(),
            email,
            phone,
            company_id,
        }
    }
}

/// (CRM) A company.
///
/// Mirrors `sealed record Company(string CompanyId, string Name,
/// string? Industry)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Company {
    pub company_id: String,
    pub name: String,
    pub industry: Option<String>,
}

impl Company {
    /// Constructs a company, mirroring the positional C# record constructor.
    pub fn new(
        company_id: impl Into<String>,
        name: impl Into<String>,
        industry: Option<String>,
    ) -> Self {
        Self {
            company_id: company_id.into(),
            name: name.into(),
            industry,
        }
    }
}

/// (CRM) A deal.
///
/// Mirrors `sealed record Deal(string DealId, string CompanyId, string Name,
/// decimal Value, string Currency, string Stage)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Deal {
    pub deal_id: String,
    pub company_id: String,
    pub name: String,
    pub value: f64,
    pub currency: String,
    pub stage: String,
}

impl Deal {
    /// Constructs a deal, mirroring the positional C# record constructor.
    pub fn new(
        deal_id: impl Into<String>,
        company_id: impl Into<String>,
        name: impl Into<String>,
        value: f64,
        currency: impl Into<String>,
        stage: impl Into<String>,
    ) -> Self {
        Self {
            deal_id: deal_id.into(),
            company_id: company_id.into(),
            name: name.into(),
            value,
            currency: currency.into(),
            stage: stage.into(),
        }
    }
}

/// (CRM) An activity-log entry.
///
/// Mirrors `sealed record Activity(string ActivityId, string ContactId,
/// string Kind, string Body, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Activity {
    pub activity_id: String,
    pub contact_id: String,
    pub kind: String,
    pub body: String,
    pub at_utc: DateTime<Utc>,
}

impl Activity {
    /// Constructs an activity, mirroring the positional C# record constructor.
    pub fn new(
        activity_id: impl Into<String>,
        contact_id: impl Into<String>,
        kind: impl Into<String>,
        body: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            activity_id: activity_id.into(),
            contact_id: contact_id.into(),
            kind: kind.into(),
            body: body.into(),
            at_utc,
        }
    }
}

/// (CRM) Contact store.
///
/// Mirrors `interface IContactStore`.
pub trait IContactStore {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// Inserts or overwrites `c`.
    fn upsert(&self, c: Contact);
    /// Looks up a contact by id.
    fn get(&self, id: &str) -> Option<Contact>;
    /// Up to `top_k` contacts whose name or email contains `query`
    /// (case-insensitive), ordered by name ascending (case-insensitive).
    fn search(&self, query: &str, top_k: usize) -> Vec<Contact>;
}

/// (CRM) Deal pipeline.
///
/// Mirrors `interface IDealPipeline`.
pub trait IDealPipeline {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// Inserts or overwrites `d`.
    fn upsert(&self, d: Deal);
    /// Looks up a deal by id.
    fn get(&self, id: &str) -> Option<Deal>;
    /// Deals in `stage` (case-insensitive), ordered by value descending.
    fn list_by_stage(&self, stage: &str) -> Vec<Deal>;
}

/// (CRM) Activity log.
///
/// Mirrors `interface IActivityLog`.
pub trait IActivityLog {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// Appends `a` to its contact's log.
    fn append(&self, a: Activity);
    /// Up to `limit` activities for `contact_id`, newest-first.
    fn read_for_contact(&self, contact_id: &str, limit: usize) -> Vec<Activity>;
}

/// The default `top_k` in the C# `SearchAsync(..., int topK = 20, ...)`.
pub const DEFAULT_TOP_K: usize = 20;
/// The default `limit` in the C# `ReadForContactAsync(..., int limit = 100, ...)`.
pub const DEFAULT_READ_LIMIT: usize = 100;
