//! Running a small business, the career profile behind a CV, and the documents
//! both produce.
//!
//! MONEY IS AN INTEGER NUMBER OF MINOR UNITS. Rust's type system makes this
//! easier to hold than most - `i64` cents cannot silently become a float - and
//! the discipline is the same one every other port needed: a business whose
//! invoice total stops matching the sum of its lines is a business with an
//! argument to have with a client.
//!
//! THE NUMBER OF MINOR UNITS DIFFERS PER CURRENCY. Most have two, the yen has
//! none, the dinar has three. Dividing everything by a hundred prints JPY 1950
//! as 19.50 and KWD 1.950 as 19.50 - both wrong by a factor somebody notices
//! when they are billed.
//!
//! AND THE DATE ARITHMETIC. "Monthly" from the 31st has to mean the 28th in
//! February and the 31st again in March, not the 3rd of March and then the 3rd
//! of every month after. Adding 30 days walks a monthly reminder backwards.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// Money

/// What a currency's minor unit is.
pub struct Currencies;

impl Currencies {
    /// The local one. Named rather than repeated.
    pub const HOME: &'static str = "ZAR";

    /// Currencies whose minor unit is NOT two decimal places.
    ///
    /// Everything absent has two. Listing the exceptions rather than every
    /// currency means a currency nobody thought about gets the common answer
    /// instead of no answer.
    pub fn minor_units(code: &str) -> u32 {
        match code.to_uppercase().as_str() {
            "JPY" | "KRW" | "VND" | "CLP" | "ISK" | "UGX" | "RWF" | "XAF" | "XOF" | "PYG" => 0,
            "KWD" | "BHD" | "OMR" | "TND" | "JOD" | "IQD" | "LYD" => 3,
            _ => 2,
        }
    }

    pub fn divisor(code: &str) -> i64 {
        10i64.pow(Self::minor_units(code))
    }

    pub fn is_known(code: &str) -> bool {
        code.len() == 3 && code.chars().all(|c| c.is_ascii_alphabetic())
    }
}

/// An amount, as an integer count of minor units plus a currency.
///
/// THE CURRENCY IS PART OF THE VALUE. Adding two `Money` of different currencies
/// returns `None` rather than converting, because a conversion needs a rate and
/// a rate needs a date - and a total that silently used today's rate for last
/// year's invoice is wrong in a way nobody can see.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct Money {
    pub minor: i64,
    pub currency: String,
}

impl Money {
    pub fn of(minor: i64, currency: &str) -> Option<Self> {
        Currencies::is_known(currency).then(|| Self {
            minor,
            currency: currency.to_uppercase(),
        })
    }

    pub fn zero(currency: &str) -> Option<Self> {
        Self::of(0, currency)
    }

    /// From a decimal amount, rounded HALF AWAY FROM ZERO at the currency's own
    /// precision - which is what an accountant expects, and what Rust's `round`
    /// already does.
    pub fn from_decimal(amount: f64, currency: &str) -> Option<Self> {
        let divisor = Currencies::divisor(currency) as f64;
        Self::of((amount * divisor).round() as i64, currency)
    }

    fn same_currency(&self, other: &Self) -> bool {
        self.currency == other.currency
    }

    pub fn plus(&self, other: &Self) -> Option<Self> {
        self.same_currency(other)
            .then(|| Self { minor: self.minor + other.minor, currency: self.currency.clone() })
    }

    pub fn minus(&self, other: &Self) -> Option<Self> {
        self.same_currency(other)
            .then(|| Self { minor: self.minor - other.minor, currency: self.currency.clone() })
    }

    /// Multiplied by a quantity, rounded half away from zero.
    ///
    /// A quantity may be fractional - hours, kilograms - and the result must
    /// land on a whole minor unit, because a line item of 3.335 cents cannot be
    /// invoiced.
    pub fn times(&self, quantity: f64) -> Self {
        Self {
            minor: (self.minor as f64 * quantity).round() as i64,
            currency: self.currency.clone(),
        }
    }

    /// A percentage, in BASIS POINTS.
    ///
    /// 15% is 1500, not 0.15. An integer rate cannot be 14.999999999999998,
    /// which is what 0.15 does to a total large enough to matter.
    pub fn percent(&self, basis_points: i64) -> Self {
        // Integer arithmetic throughout, with the rounding done explicitly:
        // dividing by 10000 in integers truncates towards zero and loses a cent
        // on most invoices.
        let scaled = self.minor * basis_points;
        let rounded = if scaled >= 0 {
            (scaled + 5000) / 10_000
        } else {
            (scaled - 5000) / 10_000
        };
        Self { minor: rounded, currency: self.currency.clone() }
    }

    pub fn is_zero(&self) -> bool {
        self.minor == 0
    }

    pub fn is_negative(&self) -> bool {
        self.minor < 0
    }

    /// Formatted at the currency's own precision.
    pub fn format(&self) -> String {
        let digits = Currencies::minor_units(&self.currency) as usize;
        let divisor = Currencies::divisor(&self.currency);
        let negative = self.minor < 0;
        let magnitude = self.minor.abs();
        let units = magnitude / divisor;
        let fraction = magnitude % divisor;
        let body = if digits == 0 {
            units.to_string()
        } else {
            format!("{units}.{fraction:0>digits$}")
        };
        format!("{}{} {}", if negative { "-" } else { "" }, body, self.currency)
    }

    /// Sums a list, and REFUSES an empty one without a currency.
    ///
    /// An empty sum has no currency, and defaulting it to the home one produces
    /// a zero in the wrong currency that then poisons the next addition.
    pub fn sum(amounts: &[Money], currency: Option<&str>) -> Option<Money> {
        match amounts.first() {
            None => Self::zero(currency?),
            Some(first) => amounts[1..]
                .iter()
                .try_fold(first.clone(), |acc, next| acc.plus(next)),
        }
    }
}

impl std::fmt::Display for Money {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.format())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// People

/// Somebody the business deals with.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Client {
    pub id: String,
    pub name: String,
    pub email: String,
    pub phone_e164: String,
    pub vat_number: String,
    pub notes: String,
    pub created_at_ms: u64,
}

/// The book of clients.
pub trait ClientBookTrait {
    fn add(&mut self, client: Client) -> bool;
    fn get(&self, id: &str) -> Option<&Client>;
    fn search(&self, query: &str) -> Vec<Client>;
    fn all(&self) -> Vec<Client>;
}

/// Knows nobody.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullClientBook;

impl ClientBookTrait for NullClientBook {
    fn add(&mut self, _client: Client) -> bool {
        false
    }
    fn get(&self, _id: &str) -> Option<&Client> {
        None
    }
    fn search(&self, _query: &str) -> Vec<Client> {
        Vec::new()
    }
    fn all(&self) -> Vec<Client> {
        Vec::new()
    }
}

/// The default book.
#[derive(Debug, Default)]
pub struct ClientBook {
    clients: HashMap<String, Client>,
}

impl ClientBook {
    pub fn new() -> Self {
        Self::default()
    }
}

impl ClientBookTrait for ClientBook {
    fn add(&mut self, client: Client) -> bool {
        if client.id.trim().is_empty() || client.name.trim().is_empty() {
            return false;
        }
        self.clients.insert(client.id.clone(), client);
        true
    }

    fn get(&self, id: &str) -> Option<&Client> {
        self.clients.get(id)
    }

    /// Searches the name, email and phone TOGETHER.
    ///
    /// Somebody looking for a client types whichever of the three they remember,
    /// and a search that only covers the name fails exactly when a person is
    /// looking up a number they have been called from.
    fn search(&self, query: &str) -> Vec<Client> {
        let q = query.trim().to_lowercase();
        if q.is_empty() {
            return self.all();
        }
        // Punctuation stripped for the phone comparison, so "082 000 0000" finds
        // a client stored as "+27820000000".
        let digits: String = q.chars().filter(char::is_ascii_digit).collect();
        self.clients
            .values()
            .filter(|c| {
                c.name.to_lowercase().contains(&q)
                    || c.email.to_lowercase().contains(&q)
                    || (digits.len() >= 6
                        && c.phone_e164
                            .chars()
                            .filter(char::is_ascii_digit)
                            .collect::<String>()
                            .contains(&digits))
            })
            .cloned()
            .collect()
    }

    fn all(&self) -> Vec<Client> {
        let mut out: Vec<Client> = self.clients.values().cloned().collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Invoices

/// Where an invoice has got to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum InvoiceStatus {
    /// Not yet issued. The only state in which it may still be edited.
    #[default]
    Draft,
    /// Issued to the client. From here it is a record, not a document.
    Sent,
    PartlyPaid,
    Paid,
    /// Cancelled by a credit note, NOT deleted. An issued invoice that vanishes
    /// leaves a gap in the number sequence, which is exactly what an auditor
    /// asks about.
    Cancelled,
    Overdue,
}

/// One line on an invoice.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InvoiceLine {
    pub description: String,
    pub quantity_thousandths: i64,
    pub unit_price: Money,
    /// In basis points. 15% is 1500.
    pub tax_basis_points: i64,
}

impl InvoiceLine {
    /// Quantity is held in THOUSANDTHS as an integer, so 2.5 hours is 2500.
    ///
    /// A float quantity multiplied into an integer price reintroduces exactly
    /// the rounding this whole module avoids.
    pub fn quantity(&self) -> f64 {
        self.quantity_thousandths as f64 / 1000.0
    }

    pub fn total(&self) -> Money {
        self.unit_price.times(self.quantity())
    }
}

/// A whole invoice.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Invoice {
    pub number: String,
    pub client_id: String,
    pub issued_at_ms: u64,
    pub due_at_ms: u64,
    pub lines: Vec<InvoiceLine>,
    pub status: InvoiceStatus,
    pub paid: Money,
    pub currency: String,
    pub notes: String,
}

impl Invoice {
    pub fn subtotal(&self) -> Option<Money> {
        Money::sum(
            &self.lines.iter().map(InvoiceLine::total).collect::<Vec<_>>(),
            Some(&self.currency),
        )
    }

    /// Tax is computed PER LINE and then summed, not on the total.
    ///
    /// Lines may carry different rates - zero-rated and standard-rated on one
    /// invoice is ordinary - and taxing the total applies one rate to all of
    /// them. Even at a single rate the two differ by a cent through rounding,
    /// and the cent is the one a client queries.
    pub fn tax(&self) -> Option<Money> {
        Money::sum(
            &self
                .lines
                .iter()
                .map(|l| l.total().percent(l.tax_basis_points))
                .collect::<Vec<_>>(),
            Some(&self.currency),
        )
    }

    pub fn total(&self) -> Option<Money> {
        self.subtotal()?.plus(&self.tax()?)
    }

    pub fn outstanding(&self) -> Option<Money> {
        self.total()?.minus(&self.paid)
    }

    /// The status DERIVED from what is actually true, rather than stored.
    ///
    /// A stored status goes stale the moment a payment lands or a date passes,
    /// and then an invoice that is paid still shows as overdue on somebody's
    /// screen.
    pub fn status_at(&self, now_ms: u64) -> InvoiceStatus {
        if matches!(self.status, InvoiceStatus::Draft | InvoiceStatus::Cancelled) {
            return self.status;
        }
        match self.outstanding() {
            Some(out) if out.minor <= 0 => InvoiceStatus::Paid,
            _ if now_ms > self.due_at_ms => InvoiceStatus::Overdue,
            _ if self.paid.is_zero() => InvoiceStatus::Sent,
            _ => InvoiceStatus::PartlyPaid,
        }
    }
}

/// Produces invoice numbers.
pub trait InvoiceNumberGenerator {
    fn next(&mut self) -> String;
}

/// Sequential, gapless, and formatted with the year.
///
/// GAPLESS IS THE REQUIREMENT, not a nicety: a missing number in a sequence is
/// the first thing an auditor asks about, and "we deleted a draft" is not an
/// answer they accept. So a number is only issued when an invoice is ISSUED,
/// never when one is drafted.
#[derive(Debug, Clone)]
pub struct SequentialInvoiceNumberGenerator {
    year: u32,
    counter: u32,
    prefix: String,
}

impl SequentialInvoiceNumberGenerator {
    pub fn new(year: u32, start_at: u32, prefix: &str) -> Self {
        Self { year, counter: start_at, prefix: prefix.to_string() }
    }

    pub fn issued(&self) -> u32 {
        self.counter
    }
}

impl InvoiceNumberGenerator for SequentialInvoiceNumberGenerator {
    fn next(&mut self) -> String {
        self.counter += 1;
        // Padded to four, which lasts a small business a long time and sorts
        // correctly as text - unpadded numbers sort 1, 10, 2 in every
        // spreadsheet somebody exports this to.
        format!("{}-{}-{:04}", self.prefix, self.year, self.counter)
    }
}

/// Renders an invoice to a document.
pub trait InvoicePdfRenderer {
    fn is_available(&self) -> bool;
    fn render(&self, invoice: &Invoice, client: Option<&Client>) -> Vec<u8>;
}

/// Renders nothing.
///
/// Returns empty rather than failing: an invoice that exists as a record but
/// cannot be rendered today is still a valid invoice, and failing the whole
/// operation would lose the record too.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullInvoicePdfRenderer;

impl InvoicePdfRenderer for NullInvoicePdfRenderer {
    fn is_available(&self) -> bool {
        false
    }
    fn render(&self, _invoice: &Invoice, _client: Option<&Client>) -> Vec<u8> {
        Vec::new()
    }
}

/// Manages invoices.
pub trait InvoiceServiceTrait {
    fn draft(&self, client_id: &str, lines: Vec<InvoiceLine>, currency: &str) -> Option<Invoice>;
    fn issue(&mut self, invoice: Invoice, due_in_days: u64, now_ms: u64) -> Result<Invoice, String>;
    fn record_payment(&self, invoice: Invoice, amount: &Money, now_ms: u64) -> Result<Invoice, String>;
    fn cancel(&self, invoice: Invoice, reason: &str) -> Invoice;
}

/// Does nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullInvoiceService;

impl InvoiceServiceTrait for NullInvoiceService {
    fn draft(&self, _client_id: &str, _lines: Vec<InvoiceLine>, _currency: &str) -> Option<Invoice> {
        None
    }
    fn issue(&mut self, invoice: Invoice, _days: u64, _now_ms: u64) -> Result<Invoice, String> {
        Ok(invoice)
    }
    fn record_payment(&self, invoice: Invoice, _a: &Money, _now: u64) -> Result<Invoice, String> {
        Ok(invoice)
    }
    fn cancel(&self, invoice: Invoice, _reason: &str) -> Invoice {
        invoice
    }
}

/// The default service.
pub struct InvoiceService<G: InvoiceNumberGenerator> {
    numbers: G,
}

impl<G: InvoiceNumberGenerator> InvoiceService<G> {
    pub fn new(numbers: G) -> Self {
        Self { numbers }
    }
}

impl<G: InvoiceNumberGenerator> InvoiceServiceTrait for InvoiceService<G> {
    /// A draft has NO NUMBER. Numbering a draft is what puts gaps in the
    /// sequence, because most drafts are never issued.
    fn draft(&self, client_id: &str, lines: Vec<InvoiceLine>, currency: &str) -> Option<Invoice> {
        Some(Invoice {
            number: String::new(),
            client_id: client_id.to_string(),
            issued_at_ms: 0,
            due_at_ms: 0,
            lines,
            status: InvoiceStatus::Draft,
            paid: Money::zero(currency)?,
            currency: currency.to_uppercase(),
            notes: String::new(),
        })
    }

    fn issue(&mut self, invoice: Invoice, due_in_days: u64, now_ms: u64) -> Result<Invoice, String> {
        if invoice.status != InvoiceStatus::Draft {
            return Ok(invoice);
        }
        if invoice.lines.is_empty() {
            // Refused. An empty invoice consumes a number and says nothing, and
            // the number cannot be reused.
            return Err("an invoice with no lines cannot be issued".into());
        }
        Ok(Invoice {
            number: self.numbers.next(),
            issued_at_ms: now_ms,
            due_at_ms: now_ms + due_in_days * 24 * 60 * 60 * 1000,
            status: InvoiceStatus::Sent,
            ..invoice
        })
    }

    /// Payments ACCUMULATE. A second payment adds to the first rather than
    /// replacing it, which is the bug that turns two part-payments into one.
    fn record_payment(
        &self,
        invoice: Invoice,
        amount: &Money,
        now_ms: u64,
    ) -> Result<Invoice, String> {
        if amount.is_negative() {
            return Err("a payment cannot be negative; use a credit note".into());
        }
        let paid = invoice
            .paid
            .plus(amount)
            .ok_or("that payment is in a different currency")?;
        let next = Invoice { paid, ..invoice };
        let status = next.status_at(now_ms);
        Ok(Invoice { status, ..next })
    }

    /// Cancels WITHOUT removing. The number stays used and the record stays.
    fn cancel(&self, invoice: Invoice, reason: &str) -> Invoice {
        let notes = if invoice.notes.is_empty() {
            format!("cancelled: {reason}")
        } else {
            format!("{}\ncancelled: {reason}", invoice.notes)
        };
        Invoice { status: InvoiceStatus::Cancelled, notes, ..invoice }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Reminders

/// What a reminder is about.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ReminderKind {
    Invoice,
    Payment,
    Appointment,
    Renewal,
    /// Something a person typed. No rules attached.
    #[default]
    Personal,
}

/// How often something repeats.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Recurrence {
    #[default]
    Once,
    Daily,
    Weekly,
    /// The awkward one. See `RecurrenceRule::next`.
    Monthly,
    Yearly,
}

/// When something happens next.
///
/// MONTHLY IS THE HARD CASE and it is the reason this is a type rather than a
/// number of milliseconds. "Monthly from the 31st" must be the 28th in February
/// and the 31st again in March - not the 3rd of March and then the 3rd forever.
/// Adding 30 days walks a monthly reminder backwards through the year.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct RecurrenceRule {
    pub recurrence: Recurrence,
    /// The day of the month this was ANCHORED to, so a clamped month does not
    /// become the new anchor. Kept separately for exactly that reason.
    pub anchor_day_of_month: u32,
}

impl RecurrenceRule {
    const MS_PER_DAY: u64 = 24 * 60 * 60 * 1000;

    /// Days in a month, with the leap rule spelled out. A table that stops at
    /// 2100 is a table that is wrong in 2100.
    pub fn days_in_month(year: i64, month: u32) -> u32 {
        match month {
            1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
            4 | 6 | 9 | 11 => 30,
            2 if (year % 4 == 0 && year % 100 != 0) || year % 400 == 0 => 29,
            2 => 28,
            _ => 30,
        }
    }

    /// Days since the epoch to a civil date, and back. Howard Hinnant's
    /// algorithm - exact for any date, and small enough to carry rather than
    /// take a dependency.
    fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
        let y = if m <= 2 { y - 1 } else { y };
        let era = if y >= 0 { y } else { y - 399 } / 400;
        let yoe = y - era * 400;
        let mp = ((m as i64) + 9) % 12;
        let doy = (153 * mp + 2) / 5 + d as i64 - 1;
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        era * 146_097 + doe - 719_468
    }

    fn civil_from_days(z: i64) -> (i64, u32, u32) {
        let z = z + 719_468;
        let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
        let doe = z - era * 146_097;
        let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
        let y = yoe + era * 400;
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        let mp = (5 * doy + 2) / 153;
        let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
        let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
        (if m <= 2 { y + 1 } else { y }, m, d)
    }

    /// Adds months, CLAMPING to the last day of the target month.
    ///
    /// The anchor day is passed in rather than read from the date, so a reminder
    /// clamped to 28 February goes back to 31 March rather than staying on the
    /// 28th of every month afterwards - which is what happens when the clamped
    /// date becomes the new anchor.
    pub fn add_months(at_ms: u64, months: i64, anchor_day: u32) -> u64 {
        let days = (at_ms / Self::MS_PER_DAY) as i64;
        let time_of_day = at_ms % Self::MS_PER_DAY;
        let (year, month, day) = Self::civil_from_days(days);
        let anchor = if anchor_day == 0 { day } else { anchor_day };

        let total = year * 12 + (month as i64 - 1) + months;
        let target_year = total.div_euclid(12);
        let target_month = (total.rem_euclid(12) + 1) as u32;
        let last_day = Self::days_in_month(target_year, target_month);
        let target_day = anchor.min(last_day);

        (Self::days_from_civil(target_year, target_month, target_day) as u64)
            * Self::MS_PER_DAY
            + time_of_day
    }

    /// The next occurrence after `from_ms`, or `None` when it does not repeat.
    pub fn next(&self, from_ms: u64) -> Option<u64> {
        match self.recurrence {
            Recurrence::Once => None,
            Recurrence::Daily => Some(from_ms + Self::MS_PER_DAY),
            Recurrence::Weekly => Some(from_ms + 7 * Self::MS_PER_DAY),
            Recurrence::Monthly => Some(Self::add_months(from_ms, 1, self.anchor_day_of_month)),
            Recurrence::Yearly => Some(Self::add_months(from_ms, 12, self.anchor_day_of_month)),
        }
    }
}

/// Something to be reminded about.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Reminder {
    pub id: String,
    pub kind: ReminderKind,
    pub text: String,
    pub due_at_ms: u64,
    pub rule: RecurrenceRule,
    pub is_done: bool,
    /// What it is about - an invoice number, a client id. Empty for a personal
    /// one, which is most of them.
    pub subject_id: String,
}

/// Schedules reminders.
pub trait ReminderSchedulerTrait {
    fn add(&mut self, reminder: Reminder);
    fn due(&self, now_ms: u64) -> Vec<Reminder>;
    fn complete(&mut self, id: &str, now_ms: u64) -> Option<Reminder>;
}

/// Schedules nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullReminderScheduler;

impl ReminderSchedulerTrait for NullReminderScheduler {
    fn add(&mut self, _reminder: Reminder) {}
    fn due(&self, _now_ms: u64) -> Vec<Reminder> {
        Vec::new()
    }
    fn complete(&mut self, _id: &str, _now_ms: u64) -> Option<Reminder> {
        None
    }
}

/// The default scheduler.
#[derive(Debug, Default)]
pub struct ReminderScheduler {
    reminders: HashMap<String, Reminder>,
}

impl ReminderScheduler {
    pub fn new() -> Self {
        Self::default()
    }
}

impl ReminderSchedulerTrait for ReminderScheduler {
    fn add(&mut self, reminder: Reminder) {
        self.reminders.insert(reminder.id.clone(), reminder);
    }

    /// Sorted by how overdue, so the most pressing is first rather than the
    /// oldest-created.
    fn due(&self, now_ms: u64) -> Vec<Reminder> {
        let mut out: Vec<Reminder> = self
            .reminders
            .values()
            .filter(|r| !r.is_done && r.due_at_ms <= now_ms)
            .cloned()
            .collect();
        out.sort_by_key(|r| r.due_at_ms);
        out
    }

    /// Completing a repeating reminder RESCHEDULES it from its own due date, not
    /// from now.
    ///
    /// Rescheduling from now walks a monthly reminder later every month by
    /// however long somebody took to tick it off, and after a year it has
    /// drifted into the middle of the following month.
    fn complete(&mut self, id: &str, now_ms: u64) -> Option<Reminder> {
        let existing = self.reminders.get(id)?.clone();
        if existing.is_done {
            return None;
        }
        let Some(next_at) = existing.rule.next(existing.due_at_ms) else {
            let done = Reminder { is_done: true, ..existing };
            self.reminders.insert(id.to_string(), done.clone());
            return Some(done);
        };
        // A reminder missed for several cycles CATCHES UP rather than firing
        // once per missed cycle - somebody who ignored it for three months does
        // not want three notifications.
        let mut due = next_at;
        while due <= now_ms {
            match existing.rule.next(due) {
                Some(following) if following > due => due = following,
                _ => break,
            }
        }
        let rescheduled = Reminder { due_at_ms: due, ..existing };
        self.reminders.insert(id.to_string(), rescheduled.clone());
        Some(rescheduled)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Storage

/// Reads and writes clients.
pub trait ClientRepository {
    fn put(&mut self, client: Client);
    fn get(&self, id: &str) -> Option<Client>;
    fn all(&self) -> Vec<Client>;
}

/// Reads and writes invoices.
pub trait InvoiceRepository {
    fn put(&mut self, invoice: Invoice);
    fn get(&self, number: &str) -> Option<Invoice>;
    fn for_client(&self, client_id: &str) -> Vec<Invoice>;
    fn all(&self) -> Vec<Invoice>;
}

/// Reads and writes reminders.
pub trait ReminderRepository {
    fn put(&mut self, reminder: Reminder);
    fn get(&self, id: &str) -> Option<Reminder>;
    fn all(&self) -> Vec<Reminder>;
}

/// Everything the business keeps.
#[derive(Debug, Default)]
pub struct InMemoryBusinessStore {
    clients: HashMap<String, Client>,
    invoices: HashMap<String, Invoice>,
    reminders: HashMap<String, Reminder>,
}

impl InMemoryBusinessStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl ClientRepository for InMemoryBusinessStore {
    fn put(&mut self, client: Client) {
        self.clients.insert(client.id.clone(), client);
    }
    fn get(&self, id: &str) -> Option<Client> {
        self.clients.get(id).cloned()
    }
    fn all(&self) -> Vec<Client> {
        self.clients.values().cloned().collect()
    }
}

impl InvoiceRepository for InMemoryBusinessStore {
    /// Keyed on the NUMBER, which is why a draft has none: a draft cannot be
    /// stored here until it is issued, and that is the point.
    fn put(&mut self, invoice: Invoice) {
        if !invoice.number.is_empty() {
            self.invoices.insert(invoice.number.clone(), invoice);
        }
    }
    fn get(&self, number: &str) -> Option<Invoice> {
        self.invoices.get(number).cloned()
    }
    fn for_client(&self, client_id: &str) -> Vec<Invoice> {
        self.invoices
            .values()
            .filter(|i| i.client_id == client_id)
            .cloned()
            .collect()
    }
    fn all(&self) -> Vec<Invoice> {
        self.invoices.values().cloned().collect()
    }
}

impl ReminderRepository for InMemoryBusinessStore {
    fn put(&mut self, reminder: Reminder) {
        self.reminders.insert(reminder.id.clone(), reminder);
    }
    fn get(&self, id: &str) -> Option<Reminder> {
        self.reminders.get(id).cloned()
    }
    fn all(&self) -> Vec<Reminder> {
        self.reminders.values().cloned().collect()
    }
}

/// Keeps nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullBusinessStore;

impl ClientRepository for NullBusinessStore {
    fn put(&mut self, _client: Client) {}
    fn get(&self, _id: &str) -> Option<Client> {
        None
    }
    fn all(&self) -> Vec<Client> {
        Vec::new()
    }
}

/// A bridge to a CRM somebody already uses.
///
/// ONE WAY OUT BY DEFAULT. Pulling a CRM's whole contact list onto a device is a
/// copy of somebody's business relationships, and it should be a decision rather
/// than what happens when a connection is made.
pub struct CrmBridge {
    push: Option<Box<dyn Fn(&Client) -> bool + Send + Sync>>,
    pull: Option<Box<dyn Fn() -> Vec<Client> + Send + Sync>>,
    pull_allowed: bool,
}

impl CrmBridge {
    pub fn new(
        push: Option<Box<dyn Fn(&Client) -> bool + Send + Sync>>,
        pull: Option<Box<dyn Fn() -> Vec<Client> + Send + Sync>>,
        pull_allowed: bool,
    ) -> Self {
        Self { push, pull, pull_allowed }
    }

    pub fn can_push(&self) -> bool {
        self.push.is_some()
    }

    pub fn can_pull(&self) -> bool {
        self.pull.is_some() && self.pull_allowed
    }

    pub fn send(&self, client: &Client) -> bool {
        self.push.as_ref().map(|f| f(client)).unwrap_or(false)
    }

    pub fn receive(&self) -> Vec<Client> {
        if !self.can_pull() {
            return Vec::new();
        }
        self.pull.as_ref().map(|f| f()).unwrap_or_default()
    }
}

/// Something to look at before there is real data.
///
/// REAL-SHAPED, not lorem: a sample with three-character names and round numbers
/// hides exactly the layout problems it exists to reveal - the long client name,
/// the line that wraps, the total that does not fit its column.
pub struct BusinessOpsSampleData;

impl BusinessOpsSampleData {
    pub fn clients() -> Vec<Client> {
        vec![
            Client {
                id: "c1".into(),
                name: "Mokoena Plumbing and Drainage".into(),
                email: "accounts@mokoena.co.za".into(),
                phone_e164: "+27820000001".into(),
                ..Default::default()
            },
            Client {
                id: "c2".into(),
                name: "Naledi Spaza".into(),
                email: "naledi@example.co.za".into(),
                phone_e164: "+27820000002".into(),
                ..Default::default()
            },
            Client {
                id: "c3".into(),
                name: "Thabo T".into(),
                phone_e164: "+27820000003".into(),
                ..Default::default()
            },
        ]
    }

    pub fn invoice() -> Option<Invoice> {
        Some(Invoice {
            number: "INV-2026-0001".into(),
            client_id: "c1".into(),
            issued_at_ms: 0,
            due_at_ms: 30 * 24 * 60 * 60 * 1000,
            lines: vec![
                InvoiceLine {
                    description: "Call-out and first hour".into(),
                    quantity_thousandths: 1_000,
                    unit_price: Money::from_decimal(450.0, "ZAR")?,
                    tax_basis_points: 1500,
                },
                InvoiceLine {
                    description: "Additional hours".into(),
                    quantity_thousandths: 2_500,
                    unit_price: Money::from_decimal(320.0, "ZAR")?,
                    tax_basis_points: 1500,
                },
                InvoiceLine {
                    description: "Parts (zero-rated)".into(),
                    quantity_thousandths: 1_000,
                    unit_price: Money::from_decimal(187.5, "ZAR")?,
                    tax_basis_points: 0,
                },
            ],
            status: InvoiceStatus::Sent,
            paid: Money::zero("ZAR")?,
            currency: "ZAR".into(),
            notes: String::new(),
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Career

/// Who somebody is, for a CV.
///
/// DELIBERATELY ABSENT: date of birth, ID number, marital status, a photograph.
/// They are asked for on South African CVs by convention and they are exactly
/// what enables discrimination before a person is met.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ProfileIdentity {
    pub full_name: String,
    pub headline: String,
    pub email: String,
    pub phone_e164: String,
    pub location: String,
}

/// One job.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ProfileHistory {
    pub role: String,
    pub organisation: String,
    pub start_year: String,
    /// Empty means CURRENT, which is the one safe assumption on a CV.
    pub end_year: String,
    pub bullets: Vec<String>,
}

/// One qualification.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ProfileEducation {
    pub qualification: String,
    pub institution: String,
    pub year: String,
}

/// One certificate.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ProfileCertification {
    pub name: String,
    pub issuer: String,
    pub year: String,
}

/// One skill, and how well.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ProfileSkill {
    pub name: String,
    /// 1..5, SELF-ASSESSED and labelled as such wherever it is shown. An
    /// unlabelled self-assessment reads as a measurement.
    pub level: u8,
}

/// One language, and how well.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ProfileLanguage {
    pub language: String,
    /// The words people actually use, not a number.
    pub proficiency: String,
}

/// Everything somebody has told this device about their working life.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CareerProfile {
    pub identity: ProfileIdentity,
    pub summary: String,
    pub history: Vec<ProfileHistory>,
    pub education: Vec<ProfileEducation>,
    pub certifications: Vec<ProfileCertification>,
    pub skills: Vec<ProfileSkill>,
    pub languages: Vec<ProfileLanguage>,
}

/// Which field an interview question is filling in.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ProfileField {
    Name,
    Headline,
    Contact,
    Summary,
    History,
    Education,
    Certifications,
    Skills,
    Languages,
}

/// One question in the interview.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InterviewQuestion {
    pub field: ProfileField,
    /// Asked in plain words, because this is spoken aloud as often as read.
    pub prompt: String,
    /// Whether the interview can move on without an answer.
    pub is_required: bool,
}

/// Builds a profile by asking.
///
/// ONE QUESTION AT A TIME, in an order that starts with what somebody can answer
/// without thinking. A form with twelve fields is a form nobody finishes, and
/// the people this is for are often filling it in on a phone between other
/// things.
#[derive(Debug, Default)]
pub struct CareerInterview {
    answered: Vec<ProfileField>,
}

impl CareerInterview {
    fn questions() -> Vec<InterviewQuestion> {
        vec![
            InterviewQuestion { field: ProfileField::Name, prompt: "What is your full name?".into(), is_required: true },
            InterviewQuestion { field: ProfileField::Contact, prompt: "What number or email should they use?".into(), is_required: true },
            InterviewQuestion { field: ProfileField::History, prompt: "What is the last job you did?".into(), is_required: true },
            InterviewQuestion { field: ProfileField::Headline, prompt: "In a few words, what do you do?".into(), is_required: false },
            InterviewQuestion { field: ProfileField::Skills, prompt: "What are you good at?".into(), is_required: false },
            InterviewQuestion { field: ProfileField::Education, prompt: "What did you study, if anything?".into(), is_required: false },
            InterviewQuestion { field: ProfileField::Languages, prompt: "Which languages do you speak?".into(), is_required: false },
            InterviewQuestion { field: ProfileField::Certifications, prompt: "Any certificates or licences?".into(), is_required: false },
            InterviewQuestion { field: ProfileField::Summary, prompt: "Anything else worth saying about you?".into(), is_required: false },
        ]
    }

    pub fn new() -> Self {
        Self::default()
    }

    /// The next question, or `None` when the required ones are done.
    pub fn next(&self) -> Option<InterviewQuestion> {
        Self::questions()
            .into_iter()
            .find(|q| !self.answered.contains(&q.field))
    }

    pub fn answer(&mut self, field: ProfileField) {
        if !self.answered.contains(&field) {
            self.answered.push(field);
        }
    }

    /// Skipping is allowed for anything not required, and is not a failure.
    pub fn skip(&mut self, field: ProfileField) -> bool {
        match Self::questions().into_iter().find(|q| q.field == field) {
            Some(q) if !q.is_required => {
                self.answer(field);
                true
            }
            _ => false,
        }
    }

    pub fn is_usable(&self) -> bool {
        Self::questions()
            .iter()
            .filter(|q| q.is_required)
            .all(|q| self.answered.contains(&q.field))
    }

    pub fn progress(&self) -> f32 {
        self.answered.len() as f32 / Self::questions().len() as f32
    }
}

/// What a job is asking for.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct JobSpec {
    pub title: String,
    pub organisation: String,
    pub requirements: Vec<String>,
    pub raw_text: String,
}

/// One thing the tailoring chose to do.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TailoringChoice {
    pub field: ProfileField,
    pub action: String,
    /// Why, in words somebody can disagree with. A tailoring nobody can see is a
    /// tailoring nobody can correct.
    pub reason: String,
}

/// Reorders a profile for a particular job.
///
/// IT NEVER INVENTS AND NEVER OVERSTATES. Emphasis, order and omission only -
/// every word in the output was already in the profile. A CV with a skill on it
/// that somebody does not have fails in the interview, in front of the person it
/// was meant to impress.
pub struct ProfileTailoring;

impl ProfileTailoring {
    pub fn tailor(
        profile: &CareerProfile,
        job: &JobSpec,
    ) -> (CareerProfile, Vec<TailoringChoice>) {
        let wanted: Vec<String> = job
            .requirements
            .iter()
            .flat_map(|r| {
                r.to_lowercase()
                    .split(|c: char| !c.is_alphanumeric() && c != '+' && c != '#')
                    .filter(|w| w.len() > 2)
                    .map(str::to_string)
                    .collect::<Vec<_>>()
            })
            .collect();

        let mut skills = profile.skills.clone();
        skills.sort_by_key(|s| {
            (
                !wanted.contains(&s.name.to_lowercase()),
                std::cmp::Reverse(s.level),
            )
        });

        let mut choices = Vec::new();
        if let (Some(first), Some(was)) = (skills.first(), profile.skills.first()) {
            if first != was {
                choices.push(TailoringChoice {
                    field: ProfileField::Skills,
                    action: "reorder".into(),
                    reason: format!(
                        "{} is asked for in the advert, so it goes first",
                        first.name
                    ),
                });
            }
        }
        // History stays in REVERSE CHRONOLOGICAL order regardless of relevance.
        // Reordering jobs by relevance produces a CV with unexplained gaps,
        // which reads as concealment.
        (CareerProfile { skills, ..profile.clone() }, choices)
    }
}

/// Turns a profile into CV text.
pub struct ProfileToCv;

impl ProfileToCv {
    /// Plain text, which is the format that always works.
    ///
    /// An application form that strips formatting is the common case, and text
    /// that survives a paste is worth more than a layout that does not.
    pub fn to_text(profile: &CareerProfile) -> String {
        let mut out: Vec<String> = Vec::new();
        if !profile.identity.full_name.is_empty() {
            out.push(profile.identity.full_name.to_uppercase());
            out.push(String::new());
        }
        if !profile.identity.headline.is_empty() {
            out.push(profile.identity.headline.clone());
            out.push(String::new());
        }
        // Only what was GIVEN. An empty field prints nothing rather than a
        // placeholder - a CV with "Phone: -" reads as unfinished.
        let contact: Vec<String> = [
            &profile.identity.email,
            &profile.identity.phone_e164,
            &profile.identity.location,
        ]
        .iter()
        .filter(|s| !s.is_empty())
        .map(|s| s.to_string())
        .collect();
        if !contact.is_empty() {
            out.push(contact.join("  |  "));
        }
        if !profile.summary.is_empty() {
            out.push(String::new());
            out.push("SUMMARY".into());
            out.push(profile.summary.clone());
        }
        if !profile.history.is_empty() {
            out.push(String::new());
            out.push("EXPERIENCE".into());
            for job in &profile.history {
                let period = if job.start_year.is_empty() {
                    job.end_year.clone()
                } else {
                    format!(
                        "{} - {}",
                        job.start_year,
                        if job.end_year.is_empty() { "present" } else { &job.end_year }
                    )
                };
                out.push(format!("{}, {}  ({period})", job.role, job.organisation));
                out.extend(job.bullets.iter().map(|b| format!("  - {b}")));
            }
        }
        if !profile.education.is_empty() {
            out.push(String::new());
            out.push("EDUCATION".into());
            out.extend(profile.education.iter().map(|e| {
                if e.year.is_empty() {
                    format!("{}, {}", e.qualification, e.institution)
                } else {
                    format!("{}, {} ({})", e.qualification, e.institution, e.year)
                }
            }));
        }
        if !profile.certifications.is_empty() {
            out.push(String::new());
            out.push("CERTIFICATIONS".into());
            out.extend(profile.certifications.iter().map(|c| {
                [c.name.as_str(), c.issuer.as_str(), c.year.as_str()]
                    .iter()
                    .filter(|s| !s.is_empty())
                    .cloned()
                    .collect::<Vec<_>>()
                    .join(" - ")
            }));
        }
        if !profile.skills.is_empty() {
            out.push(String::new());
            out.push("SKILLS".into());
            out.push(
                profile
                    .skills
                    .iter()
                    .map(|s| s.name.clone())
                    .collect::<Vec<_>>()
                    .join(", "),
            );
        }
        if !profile.languages.is_empty() {
            out.push(String::new());
            out.push("LANGUAGES".into());
            out.push(
                profile
                    .languages
                    .iter()
                    .map(|l| format!("{} ({})", l.language, l.proficiency))
                    .collect::<Vec<_>>()
                    .join(", "),
            );
        }
        out.join("\n")
    }
}

/// A document somebody has looked at and agreed to send.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ApprovedDocument {
    pub id: String,
    pub job_title: String,
    pub organisation: String,
    pub text: String,
    /// WHEN THEY APPROVED IT, not when it was generated. A document generated
    /// and never read is not approved, and this is the field that distinguishes
    /// them.
    pub approved_at_ms: u64,
}

/// The career profile, on disk.
///
/// PARAMETERISED, ALWAYS. Everything here came from something somebody said
/// about their own life, and a store built by concatenating strings into SQL can
/// be rewritten by anybody who can get a sentence into it.
pub struct SqliteCareerStore {
    execute: Option<Box<dyn Fn(&str, &[String]) -> Vec<Vec<String>> + Send + Sync>>,
}

impl SqliteCareerStore {
    pub const SCHEMA: &'static [&'static str] = &[
        "CREATE TABLE IF NOT EXISTS profile (\
         id TEXT PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL)",
        "CREATE TABLE IF NOT EXISTS approved (\
         id TEXT PRIMARY KEY, job_title TEXT NOT NULL, organisation TEXT NOT NULL, \
         text TEXT NOT NULL, approved_at TEXT NOT NULL)",
    ];

    pub fn new(
        execute: Option<Box<dyn Fn(&str, &[String]) -> Vec<Vec<String>> + Send + Sync>>,
    ) -> Self {
        Self { execute }
    }

    pub fn initialise(&self) -> bool {
        let Some(execute) = &self.execute else { return false };
        for statement in Self::SCHEMA {
            execute(statement, &[]);
        }
        true
    }

    pub fn save_profile(&self, id: &str, json: &str, updated_at: &str) -> bool {
        let Some(execute) = &self.execute else { return false };
        if id.trim().is_empty() {
            return false;
        }
        execute(
            "INSERT OR REPLACE INTO profile (id, json, updated_at) VALUES (?, ?, ?)",
            &[id.into(), json.into(), updated_at.into()],
        );
        true
    }

    pub fn approve(&self, document: &ApprovedDocument, approved_at: &str) -> bool {
        let Some(execute) = &self.execute else { return false };
        execute(
            "INSERT OR REPLACE INTO approved \
             (id, job_title, organisation, text, approved_at) VALUES (?, ?, ?, ?, ?)",
            &[
                document.id.clone(),
                document.job_title.clone(),
                document.organisation.clone(),
                document.text.clone(),
                approved_at.into(),
            ],
        );
        true
    }

    /// Everything a person has sent, so they can see it. Nobody else can.
    pub fn approved_documents(&self) -> Vec<Vec<String>> {
        match &self.execute {
            Some(execute) => execute(
                "SELECT id, job_title, organisation, approved_at FROM approved \
                 ORDER BY approved_at DESC",
                &[],
            ),
            None => Vec::new(),
        }
    }
}
