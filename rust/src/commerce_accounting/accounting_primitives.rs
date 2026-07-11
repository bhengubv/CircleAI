//! accounting_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the Commerce.Accounting
//! vertical — Rust port of
//! `src/CircleAI.Commerce.Accounting/AccountingPrimitives.cs`: journal entries,
//! tax rates, period sums, account balances, net profit.
//!
//! `decimal` amounts → [`f64`]; `double Percentage` → [`f64`]. Entries live in a
//! `Mutex<Vec<..>>` (the C# `List<T>` + `object _lock`); tax rates in a
//! `Mutex<HashMap<..>>` (the C# `ConcurrentDictionary`). `AtUtc` is a C#
//! `DateTime` compared by calendar `.Year`/`.Month`, so [`DateTime<Utc>`] is the
//! faithful analogue.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Datelike, Utc};

/// (3.3.0) A double-entry journal line.
///
/// Mirrors `sealed record AccountingEntry(string EntryId, DateTime AtUtc,
/// string AccountCode, decimal DebitAmount, decimal CreditAmount, string Memo)`.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountingEntry {
    pub entry_id: String,
    pub at_utc: DateTime<Utc>,
    pub account_code: String,
    pub debit_amount: f64,
    pub credit_amount: f64,
    pub memo: String,
}

impl AccountingEntry {
    /// Constructs an entry, mirroring the positional C# record constructor.
    pub fn new(
        entry_id: impl Into<String>,
        at_utc: DateTime<Utc>,
        account_code: impl Into<String>,
        debit_amount: f64,
        credit_amount: f64,
        memo: impl Into<String>,
    ) -> Self {
        Self {
            entry_id: entry_id.into(),
            at_utc,
            account_code: account_code.into(),
            debit_amount,
            credit_amount,
            memo: memo.into(),
        }
    }
}

/// (3.3.0) A named tax rate.
///
/// Mirrors `sealed record TaxRate(string Code, double Percentage)`.
#[derive(Debug, Clone, PartialEq)]
pub struct TaxRate {
    pub code: String,
    pub percentage: f64,
}

impl TaxRate {
    /// Constructs a tax rate, mirroring the positional C# record constructor.
    pub fn new(code: impl Into<String>, percentage: f64) -> Self {
        Self {
            code: code.into(),
            percentage,
        }
    }
}

/// (3.3.0) A year/month accounting period.
///
/// Mirrors `sealed record Period(int Year, int Month)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Period {
    pub year: i32,
    pub month: u32,
}

impl Period {
    /// Constructs a period.
    pub fn new(year: i32, month: u32) -> Self {
        Self { year, month }
    }
}

/// (3.3.0) The Accounting board contract.
///
/// Mirrors `interface IAccountingBoard`.
pub trait IAccountingBoard {
    /// Posts a journal entry. Panics if either amount is negative (C#
    /// `ArgumentException`).
    fn post(&self, e: AccountingEntry);
    /// Defines (or overwrites) a tax rate.
    fn define_tax(&self, r: TaxRate);
    /// Looks up a tax rate by code.
    fn get_tax(&self, code: &str) -> Option<TaxRate>;
    /// Running balance for an account (`Σ debit − credit`).
    fn account_balance(&self, account_code: &str) -> f64;
    /// Balance for an account within a period.
    fn sum(&self, account_code: &str, p: Period) -> f64;
    /// Entries for an account within a period, oldest-first.
    fn for_account(&self, account_code: &str, p: Period) -> Vec<AccountingEntry>;
    /// Net profit for a period (`revenue − expense`).
    fn net_profit(&self, p: Period, revenue_account: &str, expense_account: &str) -> f64;
}

/// (3.3.0) In-memory [`IAccountingBoard`].
pub struct InMemoryAccountingBoard {
    entries: Mutex<Vec<AccountingEntry>>,
    tax: Mutex<HashMap<String, TaxRate>>,
}

impl InMemoryAccountingBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            entries: Mutex::new(Vec::new()),
            tax: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryAccountingBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IAccountingBoard for InMemoryAccountingBoard {
    fn post(&self, e: AccountingEntry) {
        if e.debit_amount < 0.0 || e.credit_amount < 0.0 {
            panic!("amounts must be non-negative");
        }
        self.entries.lock().unwrap().push(e);
    }

    fn define_tax(&self, r: TaxRate) {
        self.tax.lock().unwrap().insert(r.code.clone(), r);
    }

    fn get_tax(&self, code: &str) -> Option<TaxRate> {
        self.tax.lock().unwrap().get(code).cloned()
    }

    fn account_balance(&self, account_code: &str) -> f64 {
        self.entries
            .lock()
            .unwrap()
            .iter()
            .filter(|e| e.account_code == account_code)
            .map(|e| e.debit_amount - e.credit_amount)
            .sum()
    }

    fn sum(&self, account_code: &str, p: Period) -> f64 {
        self.entries
            .lock()
            .unwrap()
            .iter()
            .filter(|e| {
                e.account_code == account_code
                    && e.at_utc.year() == p.year
                    && e.at_utc.month() == p.month
            })
            .map(|e| e.debit_amount - e.credit_amount)
            .sum()
    }

    fn for_account(&self, account_code: &str, p: Period) -> Vec<AccountingEntry> {
        let mut out: Vec<AccountingEntry> = self
            .entries
            .lock()
            .unwrap()
            .iter()
            .filter(|e| {
                e.account_code == account_code
                    && e.at_utc.year() == p.year
                    && e.at_utc.month() == p.month
            })
            .cloned()
            .collect();
        out.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        out
    }

    fn net_profit(&self, p: Period, revenue_account: &str, expense_account: &str) -> f64 {
        self.sum(revenue_account, p) - self.sum(expense_account, p)
    }
}
