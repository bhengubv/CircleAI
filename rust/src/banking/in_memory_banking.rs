//! in_memory_banking.rs
//!
//! (3.3.0) Real in-memory banking primitives — Rust port of
//! `src/CircleAI.Banking/InMemoryBanking.cs`: account store, ledger writer,
//! payment processor with balance checks + double-entry bookkeeping (debit
//! source, credit destination). Hosts that need durability swap in a
//! database-backed implementation behind the same contract.
//!
//! The single C# `_txLock` (an `object`) guards the whole bank; here a single
//! `Mutex<BankState>` plays that role, so `Append` under `ProcessPayment` cannot
//! re-lock (the inner logic is factored into a private `append_locked`). Tx ids
//! use `Uuid::new_v4()` rendered as 32 lowercase hex chars (`ToString("n")`).

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::Mutex;

use chrono::Utc;
use uuid::Uuid;

use super::contracts::{
    Account, IAccountReader, ILedgerWriter, IPaymentProcessor, LedgerEntry, PaymentRequest,
    PaymentResult,
};

/// A guid rendered like C# `Guid.NewGuid().ToString("n")` — 32 lowercase hex,
/// no hyphens.
fn new_guid_n() -> String {
    Uuid::new_v4().simple().to_string()
}

/// The mutable core, guarded by a single lock (the C# `_txLock`).
#[derive(Default)]
struct BankState {
    accounts: HashMap<String, Account>,
    ledger: HashMap<String, Vec<LedgerEntry>>,
}

impl BankState {
    /// The body of `Append`, run while the caller already holds the lock.
    fn append_locked(&mut self, entry: LedgerEntry) -> LedgerEntry {
        let acct = self
            .accounts
            .get(&entry.account_id)
            .cloned()
            .unwrap_or_else(|| panic!("Unknown account {}", entry.account_id));
        let updated = Account {
            balance: acct.balance + entry.amount,
            ..acct
        };
        self.accounts.insert(entry.account_id.clone(), updated);
        self.ledger
            .entry(entry.account_id.clone())
            .or_default()
            .push(entry.clone());
        entry
    }
}

/// (3.3.0) Concurrent in-memory bank shared by reader/ledger/payment.
///
/// Mirrors `sealed class InMemoryBank`. Cloneable handle (`Arc`) so the reader,
/// ledger writer and payment processor can share one bank exactly as the C#
/// constructors share one `InMemoryBank` reference.
#[derive(Clone)]
pub struct InMemoryBank {
    state: Arc<Mutex<BankState>>,
}

impl InMemoryBank {
    /// Creates an empty bank.
    pub fn new() -> Self {
        Self {
            state: Arc::new(Mutex::new(BankState::default())),
        }
    }

    /// Seeds (or overwrites) an account.
    pub fn seed_account(&self, account: Account) {
        self.state
            .lock()
            .unwrap()
            .accounts
            .insert(account.account_id.clone(), account);
    }

    /// Looks up an account by id.
    pub fn get(&self, id: &str) -> Option<Account> {
        self.state.lock().unwrap().accounts.get(id).cloned()
    }

    /// All accounts owned by `owner_id`.
    pub fn list_for_owner(&self, owner_id: &str) -> Vec<Account> {
        self.state
            .lock()
            .unwrap()
            .accounts
            .values()
            .filter(|a| a.owner_id == owner_id)
            .cloned()
            .collect()
    }

    /// Appends `entry` (applying it to the account balance). Panics on an
    /// unknown account (mirrors the C# `InvalidOperationException`).
    pub fn append(&self, entry: LedgerEntry) -> LedgerEntry {
        self.state.lock().unwrap().append_locked(entry)
    }

    /// Up to `limit` entries for `account_id`, newest-first.
    pub fn read(&self, account_id: &str, limit: usize) -> Vec<LedgerEntry> {
        let state = self.state.lock().unwrap();
        let Some(list) = state.ledger.get(account_id) else {
            return Vec::new();
        };
        let mut out: Vec<LedgerEntry> = list.clone();
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out.truncate(limit);
        out
    }

    /// Processes a payment with balance / currency / positivity checks and
    /// double-entry bookkeeping. Never panics — failures come back as a
    /// non-accepted [`PaymentResult`] carrying a reason, exactly like the C#.
    pub fn process_payment(&self, req: PaymentRequest) -> PaymentResult {
        if req.amount <= 0.0 {
            return PaymentResult::new(new_guid_n(), false, Some("Amount must be positive".into()));
        }
        let mut state = self.state.lock().unwrap();

        let Some(src) = state.accounts.get(&req.from_account).cloned() else {
            return PaymentResult::new(new_guid_n(), false, Some("Unknown source account".into()));
        };
        let Some(dst) = state.accounts.get(&req.to_account).cloned() else {
            return PaymentResult::new(
                new_guid_n(),
                false,
                Some("Unknown destination account".into()),
            );
        };
        if !src.currency.eq_ignore_ascii_case(&req.currency)
            || !dst.currency.eq_ignore_ascii_case(&req.currency)
        {
            return PaymentResult::new(new_guid_n(), false, Some("Currency mismatch".into()));
        }
        if src.balance < req.amount {
            return PaymentResult::new(new_guid_n(), false, Some("Insufficient funds".into()));
        }

        let tx_id = new_guid_n();
        let now = Utc::now();
        state.append_locked(LedgerEntry::new(
            tx_id.clone(),
            req.from_account.clone(),
            -req.amount,
            format!("To {}: {}", req.to_account, req.memo),
            now,
        ));
        state.append_locked(LedgerEntry::new(
            tx_id.clone(),
            req.to_account.clone(),
            req.amount,
            format!("From {}: {}", req.from_account, req.memo),
            now,
        ));
        PaymentResult::new(tx_id, true, None)
    }
}

impl Default for InMemoryBank {
    fn default() -> Self {
        Self::new()
    }
}

/// (Banking) In-memory [`IAccountReader`] over a shared [`InMemoryBank`].
pub struct InMemoryAccountReader {
    bank: InMemoryBank,
}

impl InMemoryAccountReader {
    /// Wraps a shared bank.
    pub fn new(bank: InMemoryBank) -> Self {
        Self { bank }
    }
}

impl IAccountReader for InMemoryAccountReader {
    fn backend_id(&self) -> &str {
        "in-memory"
    }
    fn get_account(&self, id: &str) -> Option<Account> {
        self.bank.get(id)
    }
    fn list_for_owner(&self, owner: &str) -> Vec<Account> {
        self.bank.list_for_owner(owner)
    }
}

/// (Banking) In-memory [`ILedgerWriter`] over a shared [`InMemoryBank`].
pub struct InMemoryLedgerWriter {
    bank: InMemoryBank,
}

impl InMemoryLedgerWriter {
    /// Wraps a shared bank.
    pub fn new(bank: InMemoryBank) -> Self {
        Self { bank }
    }
}

impl ILedgerWriter for InMemoryLedgerWriter {
    fn backend_id(&self) -> &str {
        "in-memory"
    }
    fn append(&self, e: LedgerEntry) -> LedgerEntry {
        self.bank.append(e)
    }
    fn read(&self, acc: &str, limit: usize) -> Vec<LedgerEntry> {
        self.bank.read(acc, limit)
    }
}

/// (Banking) In-memory [`IPaymentProcessor`] over a shared [`InMemoryBank`].
pub struct InMemoryPaymentProcessor {
    bank: InMemoryBank,
}

impl InMemoryPaymentProcessor {
    /// Wraps a shared bank.
    pub fn new(bank: InMemoryBank) -> Self {
        Self { bank }
    }
}

impl IPaymentProcessor for InMemoryPaymentProcessor {
    fn backend_id(&self) -> &str {
        "in-memory"
    }
    fn process(&self, req: PaymentRequest) -> PaymentResult {
        self.bank.process_payment(req)
    }
}
