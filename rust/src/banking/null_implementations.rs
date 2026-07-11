//! null_implementations.rs
//!
//! (2.8.0) Fail-closed banking defaults — Rust port of
//! `src/CircleAI.Banking/NullImplementations.cs`.
//!
//! Each null backend reports `backend_id() == "null"`, returns nothing / echoes
//! input, and (for payments) refuses with the fixed reason
//! `"NullPaymentProcessor."` carrying the empty guid
//! `00000000-0000-0000-0000-000000000000` (C# `Guid.Empty.ToString()`).

use super::contracts::{
    Account, IAccountReader, ILedgerWriter, IPaymentProcessor, LedgerEntry, PaymentRequest,
    PaymentResult,
};

/// C# `Guid.Empty.ToString()` — the all-zero guid in the default hyphenated form.
pub const EMPTY_GUID: &str = "00000000-0000-0000-0000-000000000000";

/// (Banking) Fail-closed [`IAccountReader`]: knows no accounts.
///
/// Mirrors `sealed class NullAccountReader` (the C# `Instance` singleton is a
/// unit struct here).
#[derive(Debug, Clone, Copy, Default)]
pub struct NullAccountReader;

impl NullAccountReader {
    /// The shared instance (mirrors the C# `static readonly Instance`).
    pub const INSTANCE: NullAccountReader = NullAccountReader;
}

impl IAccountReader for NullAccountReader {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn get_account(&self, _id: &str) -> Option<Account> {
        None
    }
    fn list_for_owner(&self, _owner: &str) -> Vec<Account> {
        Vec::new()
    }
}

/// (Banking) Fail-closed [`ILedgerWriter`]: echoes appends, reads nothing.
///
/// Mirrors `sealed class NullLedgerWriter`.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullLedgerWriter;

impl NullLedgerWriter {
    /// The shared instance.
    pub const INSTANCE: NullLedgerWriter = NullLedgerWriter;
}

impl ILedgerWriter for NullLedgerWriter {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn append(&self, e: LedgerEntry) -> LedgerEntry {
        e
    }
    fn read(&self, _acc: &str, _limit: usize) -> Vec<LedgerEntry> {
        Vec::new()
    }
}

/// (Banking) Fail-closed [`IPaymentProcessor`]: refuses every payment.
///
/// Mirrors `sealed class NullPaymentProcessor`.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullPaymentProcessor;

impl NullPaymentProcessor {
    /// The shared instance.
    pub const INSTANCE: NullPaymentProcessor = NullPaymentProcessor;
}

impl IPaymentProcessor for NullPaymentProcessor {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn process(&self, _req: PaymentRequest) -> PaymentResult {
        PaymentResult::new(EMPTY_GUID, false, Some("NullPaymentProcessor.".into()))
    }
}
