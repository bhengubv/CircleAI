//! finance_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the Commerce.Finance vertical
//! — Rust port of `src/CircleAI.Commerce.Finance/FinancePrimitives.cs`:
//! invoices, invoice lines, payments, outstanding/overdue computation.
//!
//! `decimal` money → [`f64`]; `double TaxPct` → [`f64`]. Invoice dates are C#
//! `DateTime` compared date-wise, so they map to [`NaiveDate`]. Invoices live in
//! a `Mutex<HashMap>` (C# `ConcurrentDictionary`), payments in a `Mutex<Vec>`
//! (C# `List` + `object _lock`).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, NaiveDate, Utc};

/// (3.3.0) A single billable line on an invoice.
///
/// Mirrors `sealed record InvoiceLine(string Description, decimal Amount,
/// double TaxPct)`.
#[derive(Debug, Clone, PartialEq)]
pub struct InvoiceLine {
    pub description: String,
    pub amount: f64,
    pub tax_pct: f64,
}

impl InvoiceLine {
    /// Constructs a line, mirroring the positional C# record constructor.
    pub fn new(description: impl Into<String>, amount: f64, tax_pct: f64) -> Self {
        Self {
            description: description.into(),
            amount,
            tax_pct,
        }
    }
}

/// (3.3.0) An invoice.
///
/// Mirrors `sealed record Invoice(string InvoiceId, string CustomerId,
/// DateTime IssueDate, DateTime DueDate, IReadOnlyList<InvoiceLine> Lines,
/// string Currency, string Status)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Invoice {
    pub invoice_id: String,
    pub customer_id: String,
    pub issue_date: NaiveDate,
    pub due_date: NaiveDate,
    pub lines: Vec<InvoiceLine>,
    pub currency: String,
    pub status: String,
}

impl Invoice {
    /// Constructs an invoice, mirroring the positional C# record constructor.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        invoice_id: impl Into<String>,
        customer_id: impl Into<String>,
        issue_date: NaiveDate,
        due_date: NaiveDate,
        lines: Vec<InvoiceLine>,
        currency: impl Into<String>,
        status: impl Into<String>,
    ) -> Self {
        Self {
            invoice_id: invoice_id.into(),
            customer_id: customer_id.into(),
            issue_date,
            due_date,
            lines,
            currency: currency.into(),
            status: status.into(),
        }
    }
}

/// (3.3.0) A payment against an invoice.
///
/// Mirrors `sealed record FinancePayment(string PaymentId, string InvoiceId,
/// decimal Amount, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct FinancePayment {
    pub payment_id: String,
    pub invoice_id: String,
    pub amount: f64,
    pub at_utc: DateTime<Utc>,
}

impl FinancePayment {
    /// Constructs a payment, mirroring the positional C# record constructor.
    pub fn new(
        payment_id: impl Into<String>,
        invoice_id: impl Into<String>,
        amount: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            payment_id: payment_id.into(),
            invoice_id: invoice_id.into(),
            amount,
            at_utc,
        }
    }
}

/// (3.3.0) The Invoice board contract.
///
/// Mirrors `interface IInvoiceBoard`.
pub trait IInvoiceBoard {
    /// Issues (or overwrites) an invoice.
    fn issue(&self, i: Invoice);
    /// Looks up an invoice by id.
    fn get(&self, invoice_id: &str) -> Option<Invoice>;
    /// Records a payment.
    fn record_payment(&self, p: FinancePayment);
    /// Flips unpaid invoices whose due date is before `as_of` to `"Overdue"`.
    fn mark_overdue(&self, as_of: NaiveDate);
    /// Remaining balance (billed incl. tax − payments) on an invoice; `0` when
    /// unknown.
    fn remaining_on(&self, invoice_id: &str) -> f64;
    /// Total remaining across all invoices.
    fn total_outstanding(&self) -> f64;
    /// All invoices currently marked `"Overdue"`.
    fn overdue(&self) -> Vec<Invoice>;
}

/// (3.3.0) In-memory [`IInvoiceBoard`].
pub struct InMemoryInvoiceBoard {
    invoices: Mutex<HashMap<String, Invoice>>,
    payments: Mutex<Vec<FinancePayment>>,
}

impl InMemoryInvoiceBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            invoices: Mutex::new(HashMap::new()),
            payments: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryInvoiceBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IInvoiceBoard for InMemoryInvoiceBoard {
    fn issue(&self, i: Invoice) {
        self.invoices.lock().unwrap().insert(i.invoice_id.clone(), i);
    }

    fn get(&self, invoice_id: &str) -> Option<Invoice> {
        self.invoices.lock().unwrap().get(invoice_id).cloned()
    }

    fn record_payment(&self, p: FinancePayment) {
        self.payments.lock().unwrap().push(p);
    }

    fn mark_overdue(&self, as_of: NaiveDate) {
        let mut invoices = self.invoices.lock().unwrap();
        // Collect the ids to flip first (can't mutate the map while iterating it).
        let to_flip: Vec<String> = invoices
            .values()
            .filter(|i| i.due_date < as_of && !i.status.eq_ignore_ascii_case("Paid"))
            .map(|i| i.invoice_id.clone())
            .collect();
        for id in to_flip {
            if let Some(inv) = invoices.get(&id) {
                let updated = Invoice {
                    status: "Overdue".to_string(),
                    ..inv.clone()
                };
                invoices.insert(id, updated);
            }
        }
    }

    fn remaining_on(&self, invoice_id: &str) -> f64 {
        let inv = match self.invoices.lock().unwrap().get(invoice_id) {
            Some(inv) => inv.clone(),
            None => return 0.0,
        };
        let billed: f64 = inv
            .lines
            .iter()
            .map(|l| l.amount * (1.0 + l.tax_pct / 100.0))
            .sum();
        let paid: f64 = self
            .payments
            .lock()
            .unwrap()
            .iter()
            .filter(|p| p.invoice_id == invoice_id)
            .map(|p| p.amount)
            .sum();
        billed - paid
    }

    fn total_outstanding(&self) -> f64 {
        let ids: Vec<String> = self.invoices.lock().unwrap().keys().cloned().collect();
        ids.iter().map(|id| self.remaining_on(id)).sum()
    }

    fn overdue(&self) -> Vec<Invoice> {
        self.invoices
            .lock()
            .unwrap()
            .values()
            .filter(|i| i.status.eq_ignore_ascii_case("Overdue"))
            .cloned()
            .collect()
    }
}
