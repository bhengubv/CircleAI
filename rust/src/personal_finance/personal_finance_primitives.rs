//! personal_finance_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for personal finance — Rust port
//! of `src/CircleAI.Personal.Finance/PersonalFinancePrimitives.cs`: accounts,
//! transactions, budgets, monthly summary.
//!
//! `decimal` money → [`f64`]. Accounts and budgets are `Mutex<HashMap>` (C#
//! `ConcurrentDictionary`); transactions are a `Mutex<Vec>` (C# `List` +
//! `object _lock`). Budgets are keyed case-insensitively (the C# uses
//! `StringComparer.OrdinalIgnoreCase`), so the store lower-cases the key while
//! preserving the original category text on the stored [`BudgetLine`].
//! `Budgets` reproduces `OrderBy(b => b.Category)` (ordinal).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Datelike, Utc};

/// (3.3.0) A personal finance account.
///
/// Mirrors `sealed record Account(string AccountId, string Name,
/// decimal Balance, string Currency)`. (Distinct from `banking::Account`.)
#[derive(Debug, Clone, PartialEq)]
pub struct Account {
    pub account_id: String,
    pub name: String,
    pub balance: f64,
    pub currency: String,
}

impl Account {
    /// Constructs an account, mirroring the positional C# record constructor.
    pub fn new(
        account_id: impl Into<String>,
        name: impl Into<String>,
        balance: f64,
        currency: impl Into<String>,
    ) -> Self {
        Self {
            account_id: account_id.into(),
            name: name.into(),
            balance,
            currency: currency.into(),
        }
    }
}

/// (3.3.0) A recorded transaction.
///
/// Mirrors `sealed record FinanceTransaction(string TxId, string AccountId,
/// decimal Amount, string Category, string? Note, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct FinanceTransaction {
    pub tx_id: String,
    pub account_id: String,
    pub amount: f64,
    pub category: String,
    pub note: Option<String>,
    pub at_utc: DateTime<Utc>,
}

impl FinanceTransaction {
    /// Constructs a transaction, mirroring the positional C# record constructor.
    pub fn new(
        tx_id: impl Into<String>,
        account_id: impl Into<String>,
        amount: f64,
        category: impl Into<String>,
        note: Option<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            tx_id: tx_id.into(),
            account_id: account_id.into(),
            amount,
            category: category.into(),
            note,
            at_utc,
        }
    }
}

/// (3.3.0) A monthly budget line for a category.
///
/// Mirrors `sealed record BudgetLine(string Category, decimal MonthlyLimit)`.
#[derive(Debug, Clone, PartialEq)]
pub struct BudgetLine {
    pub category: String,
    pub monthly_limit: f64,
}

impl BudgetLine {
    /// Constructs a budget line, mirroring the positional C# record constructor.
    pub fn new(category: impl Into<String>, monthly_limit: f64) -> Self {
        Self {
            category: category.into(),
            monthly_limit,
        }
    }
}

/// (3.3.0) A month's income/expense summary.
///
/// Mirrors `sealed record MonthSummary(int Year, int Month, decimal TotalIn,
/// decimal TotalOut, IReadOnlyDictionary<string, decimal> ByCategory)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MonthSummary {
    pub year: i32,
    pub month: u32,
    pub total_in: f64,
    pub total_out: f64,
    pub by_category: HashMap<String, f64>,
}

impl MonthSummary {
    /// Constructs a summary, mirroring the positional C# record constructor.
    pub fn new(
        year: i32,
        month: u32,
        total_in: f64,
        total_out: f64,
        by_category: HashMap<String, f64>,
    ) -> Self {
        Self {
            year,
            month,
            total_in,
            total_out,
            by_category,
        }
    }
}

/// (3.3.0) The Personal Finance board contract.
///
/// Mirrors `interface IPersonalFinanceBoard`. The `Budgets` getter becomes
/// [`budgets`](IPersonalFinanceBoard::budgets).
pub trait IPersonalFinanceBoard {
    /// Inserts (or overwrites) an account.
    fn upsert(&self, a: Account);
    /// Looks up an account by id.
    fn get_account(&self, id: &str) -> Option<Account>;
    /// Records a transaction and applies it to the account balance. Panics on
    /// an unknown account (C# `InvalidOperationException`).
    fn record(&self, t: FinanceTransaction);
    /// Transactions for an account in a given year/month.
    fn list_for_month(&self, account_id: &str, year: i32, month: u32) -> Vec<FinanceTransaction>;
    /// Sets (or overwrites) a budget line (category-insensitive key).
    fn set_budget(&self, b: BudgetLine);
    /// Budget lines, ordered by category.
    fn budgets(&self) -> Vec<BudgetLine>;
    /// Summarises an account's month.
    fn summarise(&self, account_id: &str, year: i32, month: u32) -> MonthSummary;
}

/// (3.3.0) In-memory [`IPersonalFinanceBoard`].
pub struct InMemoryPersonalFinanceBoard {
    accounts: Mutex<HashMap<String, Account>>,
    /// Keyed by the lower-cased category (case-insensitive, like the C#
    /// `StringComparer.OrdinalIgnoreCase`).
    budgets: Mutex<HashMap<String, BudgetLine>>,
    txns: Mutex<Vec<FinanceTransaction>>,
}

impl InMemoryPersonalFinanceBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            accounts: Mutex::new(HashMap::new()),
            budgets: Mutex::new(HashMap::new()),
            txns: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryPersonalFinanceBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IPersonalFinanceBoard for InMemoryPersonalFinanceBoard {
    fn upsert(&self, a: Account) {
        self.accounts.lock().unwrap().insert(a.account_id.clone(), a);
    }

    fn get_account(&self, id: &str) -> Option<Account> {
        self.accounts.lock().unwrap().get(id).cloned()
    }

    fn record(&self, t: FinanceTransaction) {
        let mut accounts = self.accounts.lock().unwrap();
        let acct = accounts
            .get(&t.account_id)
            .cloned()
            .unwrap_or_else(|| panic!("Unknown account {}", t.account_id));
        // Apply the delta, then push the txn (single logical critical section).
        let updated = Account {
            balance: acct.balance + t.amount,
            ..acct
        };
        accounts.insert(t.account_id.clone(), updated);
        drop(accounts);
        self.txns.lock().unwrap().push(t);
    }

    fn list_for_month(&self, account_id: &str, year: i32, month: u32) -> Vec<FinanceTransaction> {
        self.txns
            .lock()
            .unwrap()
            .iter()
            .filter(|t| t.account_id == account_id && t.at_utc.year() == year && t.at_utc.month() == month)
            .cloned()
            .collect()
    }

    fn set_budget(&self, b: BudgetLine) {
        self.budgets
            .lock()
            .unwrap()
            .insert(b.category.to_lowercase(), b);
    }

    fn budgets(&self) -> Vec<BudgetLine> {
        let mut out: Vec<BudgetLine> = self.budgets.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.category.cmp(&b.category));
        out
    }

    fn summarise(&self, account_id: &str, year: i32, month: u32) -> MonthSummary {
        let rows = self.list_for_month(account_id, year, month);
        let mut by_cat: HashMap<String, f64> = HashMap::new();
        for t in &rows {
            *by_cat.entry(t.category.clone()).or_insert(0.0) += t.amount;
        }
        let in_sum: f64 = rows.iter().filter(|t| t.amount > 0.0).map(|t| t.amount).sum();
        let out_sum: f64 = -rows.iter().filter(|t| t.amount < 0.0).map(|t| t.amount).sum::<f64>();
        MonthSummary::new(year, month, in_sum, out_sum, by_cat)
    }
}
