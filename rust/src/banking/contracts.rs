//! contracts.rs
//!
//! (2.8.0) Banking contracts — Rust port of `src/CircleAI.Banking/Contracts.cs`.
//!
//! The C# `ValueTask`-returning, `CancellationToken`-parameterised interfaces
//! collapse to synchronous traits here (the workspace Rust port is sync-only).
//! `decimal` money values become [`f64`] — there is no `System.Decimal` analogue
//! in the dependency set; the in-memory board only ever sums / differences same-
//! scale values, so ordering and equality remain exact for realistic inputs.

use chrono::{DateTime, Utc};

/// (Banking) An account.
///
/// Mirrors `sealed record Account(string AccountId, string OwnerId,
/// string Currency, decimal Balance)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Account {
    pub account_id: String,
    pub owner_id: String,
    pub currency: String,
    pub balance: f64,
}

impl Account {
    /// Constructs an account, mirroring the positional C# record constructor.
    pub fn new(
        account_id: impl Into<String>,
        owner_id: impl Into<String>,
        currency: impl Into<String>,
        balance: f64,
    ) -> Self {
        Self {
            account_id: account_id.into(),
            owner_id: owner_id.into(),
            currency: currency.into(),
            balance,
        }
    }
}

/// (Banking) A ledger entry.
///
/// Mirrors `sealed record LedgerEntry(string TxId, string AccountId,
/// decimal Amount, string Memo, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct LedgerEntry {
    pub tx_id: String,
    pub account_id: String,
    pub amount: f64,
    pub memo: String,
    pub at_utc: DateTime<Utc>,
}

impl LedgerEntry {
    /// Constructs a ledger entry, mirroring the positional C# record constructor.
    pub fn new(
        tx_id: impl Into<String>,
        account_id: impl Into<String>,
        amount: f64,
        memo: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            tx_id: tx_id.into(),
            account_id: account_id.into(),
            amount,
            memo: memo.into(),
            at_utc,
        }
    }
}

/// (Banking) A payment request.
///
/// Mirrors `sealed record PaymentRequest(string FromAccount, string ToAccount,
/// decimal Amount, string Currency, string Memo)`.
#[derive(Debug, Clone, PartialEq)]
pub struct PaymentRequest {
    pub from_account: String,
    pub to_account: String,
    pub amount: f64,
    pub currency: String,
    pub memo: String,
}

impl PaymentRequest {
    /// Constructs a payment request, mirroring the positional C# record constructor.
    pub fn new(
        from_account: impl Into<String>,
        to_account: impl Into<String>,
        amount: f64,
        currency: impl Into<String>,
        memo: impl Into<String>,
    ) -> Self {
        Self {
            from_account: from_account.into(),
            to_account: to_account.into(),
            amount,
            currency: currency.into(),
            memo: memo.into(),
        }
    }
}

/// (Banking) The outcome of a payment.
///
/// Mirrors `sealed record PaymentResult(string TxId, bool Accepted,
/// string? FailureReason)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PaymentResult {
    pub tx_id: String,
    pub accepted: bool,
    pub failure_reason: Option<String>,
}

impl PaymentResult {
    /// Constructs a payment result, mirroring the positional C# record constructor.
    pub fn new(
        tx_id: impl Into<String>,
        accepted: bool,
        failure_reason: Option<String>,
    ) -> Self {
        Self {
            tx_id: tx_id.into(),
            accepted,
            failure_reason,
        }
    }
}

/// (Banking) Read-side account access.
///
/// Mirrors `interface IAccountReader`.
pub trait IAccountReader {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// Looks up an account by id.
    fn get_account(&self, account_id: &str) -> Option<Account>;
    /// All accounts owned by `owner_id`.
    fn list_for_owner(&self, owner_id: &str) -> Vec<Account>;
}

/// (Banking) Append-only ledger writer.
///
/// Mirrors `interface ILedgerWriter`.
pub trait ILedgerWriter {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// Appends `entry`, returning it.
    fn append(&self, entry: LedgerEntry) -> LedgerEntry;
    /// Up to `limit` entries for `account_id`, newest-first.
    fn read(&self, account_id: &str, limit: usize) -> Vec<LedgerEntry>;
}

/// (Banking) Payment processor.
///
/// Mirrors `interface IPaymentProcessor`.
pub trait IPaymentProcessor {
    /// A stable identifier for the backing store.
    fn backend_id(&self) -> &str;
    /// Processes `req`, returning the outcome.
    fn process(&self, req: PaymentRequest) -> PaymentResult;
}

/// The default `read` limit in the C# `ReadAsync(..., int limit = 100, ...)`.
pub const DEFAULT_READ_LIMIT: usize = 100;
