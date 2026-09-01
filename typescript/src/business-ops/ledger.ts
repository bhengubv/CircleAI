// Running a small business, and the career profile behind a CV.
//
// MONEY IS AN INTEGER NUMBER OF MINOR UNITS. Not a float, not a "number of
// rands with two decimal places". JavaScript has one number type and it is a
// double: 0.1 + 0.2 is 0.30000000000000004, and a business whose invoice total
// stops matching the sum of its lines is a business with an argument to have
// with a client.
//
// THE NUMBER OF MINOR UNITS DIFFERS PER CURRENCY. Most have two, the yen has
// none, and the dinar has three. Dividing everything by a hundred prints
// JPY 1950 as 19.50 and KWD 1.950 as 19.50 - both wrong by a factor somebody
// notices when they are billed.
//
// AND THE DATE ARITHMETIC. "Monthly" from the 31st has to mean the 28th in
// February and the 31st again in March, not the 3rd of March and then the 3rd
// of every month after. Adding 30 days is the obvious implementation and it
// walks a monthly reminder backwards through the year.

// ─────────────────────────────────────────────────────────────────────────────
// Money

/** What a currency's minor unit is. */
export class Currencies {
  /**
   * Currencies whose minor unit is NOT two decimal places.
   *
   * Everything absent from this table has two. Listing the exceptions rather
   * than every currency means a currency nobody thought about gets the common
   * answer instead of no answer.
   */
  private static readonly exponents: Readonly<Record<string, number>> = Object.freeze({
    JPY: 0, KRW: 0, VND: 0, CLP: 0, ISK: 0, UGX: 0, RWF: 0, XAF: 0, XOF: 0, PYG: 0,
    KWD: 3, BHD: 3, OMR: 3, TND: 3, JOD: 3, IQD: 3, LYD: 3,
  });

  /** The local one. Named rather than repeated. */
  static readonly HOME = "ZAR";

  static minorUnits(code: string): number {
    return Currencies.exponents[(code ?? "").toUpperCase()] ?? 2;
  }

  static divisor(code: string): number {
    return 10 ** Currencies.minorUnits(code);
  }

  static isKnown(code: string): boolean {
    return /^[A-Z]{3}$/.test((code ?? "").toUpperCase());
  }
}

/**
 * An amount, as an integer count of minor units plus a currency.
 *
 * THE CURRENCY IS PART OF THE VALUE. Adding two Money values of different
 * currencies THROWS rather than converting, because a conversion needs a rate
 * and a rate needs a date - and a total that silently used today's rate for
 * last year's invoice is wrong in a way nobody can see.
 */
export class Money {
  private constructor(
    readonly minor: number,
    readonly currency: string,
  ) {
    Object.freeze(this);
  }

  static of(minor: number, currency: string = Currencies.HOME): Money {
    if (!Number.isInteger(minor)) {
      // Refused rather than rounded. A caller with a fractional cent has made
      // an arithmetic mistake upstream, and rounding it here hides that.
      throw new Error(`${minor} is not a whole number of minor units`);
    }
    if (!Currencies.isKnown(currency)) {
      throw new Error(`${currency} is not a three-letter currency code`);
    }
    return new Money(minor, currency.toUpperCase());
  }

  /** From a decimal amount, rounded HALF UP at the currency's own precision. */
  static fromDecimal(amount: number, currency: string = Currencies.HOME): Money {
    const divisor = Currencies.divisor(currency);
    // `Math.round` in JavaScript rounds half towards positive infinity, so
    // -0.5 becomes -0. Signed explicitly, so -R1.005 and R1.005 round the same
    // distance from zero - which is what an accountant expects.
    const scaled = amount * divisor;
    const rounded = scaled < 0 ? -Math.round(-scaled) : Math.round(scaled);
    return Money.of(rounded, currency);
  }

  static zero(currency: string = Currencies.HOME): Money {
    return Money.of(0, currency);
  }

  private requireSame(other: Money): void {
    if (this.currency !== other.currency) {
      throw new Error(
        `${this.currency} and ${other.currency} cannot be combined without a rate and a date`,
      );
    }
  }

  plus(other: Money): Money {
    this.requireSame(other);
    return Money.of(this.minor + other.minor, this.currency);
  }

  minus(other: Money): Money {
    this.requireSame(other);
    return Money.of(this.minor - other.minor, this.currency);
  }

  /**
   * Multiplied by a quantity, rounded half up.
   *
   * A quantity may be fractional - hours, kilograms - and the result must land
   * on a whole minor unit, because a line item of 3.335 cents cannot be
   * invoiced.
   */
  times(quantity: number): Money {
    const scaled = this.minor * quantity;
    return Money.of(scaled < 0 ? -Math.round(-scaled) : Math.round(scaled), this.currency);
  }

  /**
   * A percentage, for tax and discounts. In BASIS POINTS.
   *
   * 15% is 1500, not 0.15. An integer rate cannot be 14.999999999999998, which
   * is what 0.15 does to a total large enough to matter.
   */
  percent(basisPoints: number): Money {
    const scaled = (this.minor * basisPoints) / 10000;
    return Money.of(scaled < 0 ? -Math.round(-scaled) : Math.round(scaled), this.currency);
  }

  get isZero(): boolean {
    return this.minor === 0;
  }

  get isNegative(): boolean {
    return this.minor < 0;
  }

  compare(other: Money): number {
    this.requireSame(other);
    return this.minor - other.minor;
  }

  /** Formatted at the currency's own precision. */
  format(): string {
    const digits = Currencies.minorUnits(this.currency);
    const divisor = 10 ** digits;
    const negative = this.minor < 0;
    const units = Math.trunc(Math.abs(this.minor) / divisor);
    const fraction = Math.abs(this.minor) % divisor;
    const body =
      digits === 0 ? `${units}` : `${units}.${fraction.toString().padStart(digits, "0")}`;
    return `${negative ? "-" : ""}${body} ${this.currency}`;
  }

  toString(): string {
    return this.format();
  }

  /**
   * Sums a list, and REFUSES an empty one without a currency.
   *
   * An empty sum has no currency, and defaulting it to the home one produces a
   * zero in the wrong currency that then poisons the next addition.
   */
  static sum(amounts: readonly Money[], currency?: string): Money {
    if (amounts.length === 0) {
      if (!currency) throw new Error("an empty sum has no currency; say which one");
      return Money.zero(currency);
    }
    return amounts.reduce((a, b) => a.plus(b));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// People

/** Somebody the business deals with. */
export interface Client {
  readonly id: string;
  readonly name: string;
  readonly email: string;
  readonly phoneE164: string;
  readonly vatNumber: string;
  readonly notes: string;
  readonly createdAtMs: number;
}

export const client = (partial: Partial<Client> & { id: string; name: string }): Client =>
  Object.freeze({
    id: partial.id,
    name: partial.name,
    email: partial.email ?? "",
    phoneE164: partial.phoneE164 ?? "",
    vatNumber: partial.vatNumber ?? "",
    notes: partial.notes ?? "",
    createdAtMs: partial.createdAtMs ?? 0,
  });

/** The book of clients. */
export interface ClientBookContract {
  add(c: Client): boolean;
  get(id: string): Client | undefined;
  search(query: string): readonly Client[];
  all(): readonly Client[];
}

/** Knows nobody. */
export class NullClientBook implements ClientBookContract {
  add(): boolean {
    return false;
  }
  get(): Client | undefined {
    return undefined;
  }
  search(): readonly Client[] {
    return [];
  }
  all(): readonly Client[] {
    return [];
  }
}

/** The default book. */
export class ClientBook implements ClientBookContract {
  private readonly clients = new Map<string, Client>();

  add(c: Client): boolean {
    if (!c.id.trim() || !c.name.trim()) return false;
    this.clients.set(c.id, c);
    return true;
  }

  get(id: string): Client | undefined {
    return this.clients.get(id);
  }

  /**
   * Searches the name, email and phone together.
   *
   * Somebody looking for a client types whichever of the three they remember,
   * and a search that only covers the name fails exactly when a person is
   * looking up a number they have been called from.
   */
  search(query: string): readonly Client[] {
    const q = query.trim().toLowerCase();
    if (!q) return this.all();
    // Punctuation stripped for the phone comparison, so "082 000 0000" finds a
    // client stored as "+27820000000".
    const digits = q.replace(/\D/g, "");
    return Object.freeze(
      [...this.clients.values()].filter(
        (c) =>
          c.name.toLowerCase().includes(q) ||
          c.email.toLowerCase().includes(q) ||
          (digits.length >= 6 && c.phoneE164.replace(/\D/g, "").includes(digits)),
      ),
    );
  }

  all(): readonly Client[] {
    return Object.freeze([...this.clients.values()].sort((a, b) => a.name.localeCompare(b.name)));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Invoices

/** Where an invoice has got to. */
export enum InvoiceStatus {
  /** Not yet issued. The only state in which it may still be edited. */
  Draft = "draft",
  /** Issued to the client. From here it is a record, not a document. */
  Sent = "sent",
  PartlyPaid = "partly-paid",
  Paid = "paid",
  /**
   * Cancelled by a credit note, NOT deleted. An issued invoice that vanishes
   * leaves a gap in the number sequence, which is exactly what an auditor
   * asks about.
   */
  Cancelled = "cancelled",
  Overdue = "overdue",
}

/** One line on an invoice. */
export interface InvoiceLine {
  readonly description: string;
  readonly quantity: number;
  readonly unitPrice: Money;
  /** In basis points. 15% is 1500. */
  readonly taxBasisPoints: number;
}

/** A whole invoice. */
export interface Invoice {
  readonly number: string;
  readonly clientId: string;
  readonly issuedAtMs: number;
  readonly dueAtMs: number;
  readonly lines: readonly InvoiceLine[];
  readonly status: InvoiceStatus;
  readonly paid: Money;
  readonly currency: string;
  readonly notes: string;
}

/** Adds up an invoice. */
export class InvoiceMath {
  static lineTotal(line: InvoiceLine): Money {
    return line.unitPrice.times(line.quantity);
  }

  /**
   * Tax is computed PER LINE and then summed, not on the total.
   *
   * Lines may carry different rates - zero-rated and standard-rated on one
   * invoice is ordinary - and taxing the total applies one rate to all of them.
   * Even at a single rate the two differ by a cent through rounding, and the
   * cent is the one a client queries.
   */
  static tax(invoice: Invoice): Money {
    const amounts = invoice.lines.map((l) =>
      InvoiceMath.lineTotal(l).percent(l.taxBasisPoints),
    );
    return Money.sum(amounts, invoice.currency);
  }

  static subtotal(invoice: Invoice): Money {
    return Money.sum(invoice.lines.map(InvoiceMath.lineTotal), invoice.currency);
  }

  static total(invoice: Invoice): Money {
    return InvoiceMath.subtotal(invoice).plus(InvoiceMath.tax(invoice));
  }

  static outstanding(invoice: Invoice): Money {
    return InvoiceMath.total(invoice).minus(invoice.paid);
  }

  /**
   * The status DERIVED from what is actually true, rather than stored.
   *
   * A stored status goes stale the moment a payment lands or a date passes, and
   * then an invoice that is paid still shows as overdue on somebody's screen.
   */
  static statusAt(invoice: Invoice, nowMs: number): InvoiceStatus {
    if (invoice.status === InvoiceStatus.Draft || invoice.status === InvoiceStatus.Cancelled) {
      return invoice.status;
    }
    const outstanding = InvoiceMath.outstanding(invoice);
    if (outstanding.minor <= 0) return InvoiceStatus.Paid;
    if (nowMs > invoice.dueAtMs) return InvoiceStatus.Overdue;
    return invoice.paid.isZero ? InvoiceStatus.Sent : InvoiceStatus.PartlyPaid;
  }
}

/** Produces invoice numbers. */
export interface InvoiceNumberGenerator {
  next(): string;
}

/**
 * Sequential, gapless, and formatted with the year.
 *
 * GAPLESS IS THE REQUIREMENT, not a nicety: a missing number in a sequence is
 * the first thing an auditor asks about, and "we deleted a draft" is not an
 * answer they accept. So a number is only issued when an invoice is ISSUED,
 * never when one is drafted.
 */
export class SequentialInvoiceNumberGenerator implements InvoiceNumberGenerator {
  private counter: number;

  constructor(
    private readonly year: number,
    startAt = 0,
    private readonly prefix = "INV",
  ) {
    this.counter = startAt;
  }

  get issued(): number {
    return this.counter;
  }

  next(): string {
    this.counter += 1;
    // Padded to four, which lasts a small business a long time and sorts
    // correctly as text - unpadded numbers sort 1, 10, 2 in every spreadsheet
    // somebody exports this to.
    return `${this.prefix}-${this.year}-${this.counter.toString().padStart(4, "0")}`;
  }
}

/** Renders an invoice to a document. */
export interface InvoicePdfRenderer {
  readonly isAvailable: boolean;
  render(invoice: Invoice, forClient: Client | undefined): Promise<Uint8Array>;
}

/**
 * Renders nothing.
 *
 * Returns empty rather than throwing: an invoice that exists as a record but
 * cannot be rendered today is still a valid invoice, and failing the whole
 * operation would lose the record too.
 */
export class NullInvoicePdfRenderer implements InvoicePdfRenderer {
  readonly isAvailable = false;
  async render(): Promise<Uint8Array> {
    return new Uint8Array(0);
  }
}

/** Manages invoices. */
export interface InvoiceServiceContract {
  draft(clientId: string, lines: readonly InvoiceLine[], currency?: string): Invoice;
  issue(invoice: Invoice, dueInDays?: number): Invoice;
  recordPayment(invoice: Invoice, amount: Money): Invoice;
  cancel(invoice: Invoice, reason: string): Invoice;
}

/** Does nothing. */
export class NullInvoiceService implements InvoiceServiceContract {
  draft(): Invoice {
    throw new Error("no invoice service is configured");
  }
  issue(invoice: Invoice): Invoice {
    return invoice;
  }
  recordPayment(invoice: Invoice): Invoice {
    return invoice;
  }
  cancel(invoice: Invoice): Invoice {
    return invoice;
  }
}

/** The default service. */
export class InvoiceService implements InvoiceServiceContract {
  constructor(
    private readonly numbers: InvoiceNumberGenerator,
    private readonly now: () => number = () => 0,
  ) {}

  /** A draft has NO NUMBER. Numbering a draft is what puts gaps in the
   * sequence, because most drafts are never issued. */
  draft(clientId: string, lines: readonly InvoiceLine[], currency = Currencies.HOME): Invoice {
    return Object.freeze({
      number: "",
      clientId,
      issuedAtMs: 0,
      dueAtMs: 0,
      lines: Object.freeze([...lines]),
      status: InvoiceStatus.Draft,
      paid: Money.zero(currency),
      currency,
      notes: "",
    });
  }

  issue(invoice: Invoice, dueInDays = 30): Invoice {
    if (invoice.status !== InvoiceStatus.Draft) return invoice;
    if (invoice.lines.length === 0) {
      // Refused. An empty invoice consumes a number and says nothing, and the
      // number cannot be reused.
      throw new Error("an invoice with no lines cannot be issued");
    }
    const issuedAtMs = this.now();
    return Object.freeze({
      ...invoice,
      number: this.numbers.next(),
      issuedAtMs,
      dueAtMs: issuedAtMs + dueInDays * 24 * 60 * 60 * 1000,
      status: InvoiceStatus.Sent,
    });
  }

  /**
   * Payments ACCUMULATE. A second payment adds to the first rather than
   * replacing it, which is the bug that turns two part-payments into one.
   */
  recordPayment(invoice: Invoice, amount: Money): Invoice {
    if (amount.isNegative) throw new Error("a payment cannot be negative; use a credit note");
    const paid = invoice.paid.plus(amount);
    return Object.freeze({
      ...invoice,
      paid,
      status: InvoiceMath.statusAt({ ...invoice, paid }, this.now()),
    });
  }

  /** Cancels WITHOUT removing. The number stays used and the record stays. */
  cancel(invoice: Invoice, reason: string): Invoice {
    return Object.freeze({
      ...invoice,
      status: InvoiceStatus.Cancelled,
      notes: invoice.notes ? `${invoice.notes}\ncancelled: ${reason}` : `cancelled: ${reason}`,
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Reminders

/** What a reminder is about. */
export enum ReminderKind {
  Invoice = "invoice",
  Payment = "payment",
  Appointment = "appointment",
  Renewal = "renewal",
  /** Something a person typed. No rules attached. */
  Personal = "personal",
}

/** How often something repeats. */
export enum Recurrence {
  Once = "once",
  Daily = "daily",
  Weekly = "weekly",
  /** The awkward one. See `RecurrenceRule.next`. */
  Monthly = "monthly",
  Yearly = "yearly",
}

/**
 * When something happens next.
 *
 * MONTHLY IS THE HARD CASE and it is the reason this is a class rather than a
 * number of milliseconds. "Monthly from the 31st" must be the 28th in February
 * and the 31st again in March - not the 3rd of March and then the 3rd forever.
 * Adding 30 days walks a monthly reminder backwards through the year.
 */
export class RecurrenceRule {
  constructor(
    readonly recurrence: Recurrence,
    /** The day of the month this was ANCHORED to, so a clamped month does not
     * become the new anchor. Kept separately for exactly that reason. */
    readonly anchorDayOfMonth = 0,
  ) {}

  static from(startMs: number, recurrence: Recurrence): RecurrenceRule {
    return new RecurrenceRule(recurrence, new Date(startMs).getUTCDate());
  }

  /** The next occurrence after `fromMs`, or undefined when it does not repeat. */
  next(fromMs: number): number | undefined {
    const date = new Date(fromMs);
    switch (this.recurrence) {
      case Recurrence.Once:
        return undefined;
      case Recurrence.Daily:
        return fromMs + 24 * 60 * 60 * 1000;
      case Recurrence.Weekly:
        return fromMs + 7 * 24 * 60 * 60 * 1000;
      case Recurrence.Monthly:
        return RecurrenceRule.addMonths(date, 1, this.anchorDayOfMonth).getTime();
      case Recurrence.Yearly:
        return RecurrenceRule.addMonths(date, 12, this.anchorDayOfMonth).getTime();
    }
  }

  /**
   * Adds months, CLAMPING to the last day of the target month.
   *
   * The anchor day is passed in rather than read from `date`, so a reminder
   * clamped to 28 February goes back to 31 March rather than staying on the
   * 28th of every month afterwards - which is what happens when the clamped
   * date becomes the new anchor.
   */
  static addMonths(date: Date, months: number, anchorDay = 0): Date {
    const day = anchorDay || date.getUTCDate();
    const target = new Date(
      Date.UTC(
        date.getUTCFullYear(),
        date.getUTCMonth() + months,
        1,
        date.getUTCHours(),
        date.getUTCMinutes(),
        date.getUTCSeconds(),
      ),
    );
    // Day 0 of the NEXT month is the last day of this one - the standard trick,
    // and it handles February in a leap year without a table.
    const lastDay = new Date(
      Date.UTC(target.getUTCFullYear(), target.getUTCMonth() + 1, 0),
    ).getUTCDate();
    target.setUTCDate(Math.min(day, lastDay));
    return target;
  }
}

/** Something to be reminded about. */
export interface Reminder {
  readonly id: string;
  readonly kind: ReminderKind;
  readonly text: string;
  readonly dueAtMs: number;
  readonly rule: RecurrenceRule;
  readonly isDone: boolean;
  /** What it is about - an invoice number, a client id. Empty for a personal
   * one, which is most of them. */
  readonly subjectId: string;
}

/** Schedules reminders. */
export interface ReminderSchedulerContract {
  add(reminder: Reminder): void;
  due(nowMs: number): readonly Reminder[];
  complete(id: string, nowMs: number): Reminder | undefined;
}

/** Schedules nothing. */
export class NullReminderScheduler implements ReminderSchedulerContract {
  add(): void {
    /* nothing is scheduled */
  }
  due(): readonly Reminder[] {
    return [];
  }
  complete(): Reminder | undefined {
    return undefined;
  }
}

/** The default scheduler. */
export class ReminderScheduler implements ReminderSchedulerContract {
  private readonly reminders = new Map<string, Reminder>();

  add(reminder: Reminder): void {
    this.reminders.set(reminder.id, reminder);
  }

  /** Sorted by how overdue, so the most pressing is first rather than the
   * oldest-created. */
  due(nowMs: number): readonly Reminder[] {
    return Object.freeze(
      [...this.reminders.values()]
        .filter((r) => !r.isDone && r.dueAtMs <= nowMs)
        .sort((a, b) => a.dueAtMs - b.dueAtMs),
    );
  }

  /**
   * Completing a repeating reminder RESCHEDULES it from its own due date, not
   * from now.
   *
   * Rescheduling from now walks a monthly reminder later every month by however
   * long somebody took to tick it off, and after a year it has drifted into the
   * middle of the following month.
   */
  complete(id: string, nowMs: number): Reminder | undefined {
    const existing = this.reminders.get(id);
    if (!existing || existing.isDone) return undefined;
    const nextAt = existing.rule.next(existing.dueAtMs);
    if (nextAt === undefined) {
      const done = Object.freeze({ ...existing, isDone: true });
      this.reminders.set(id, done);
      return done;
    }
    // A reminder that was missed for several cycles catches up rather than
    // firing once per missed cycle - somebody who ignored it for three months
    // does not want three notifications.
    let due = nextAt;
    while (due <= nowMs) {
      const following = existing.rule.next(due);
      if (following === undefined || following === due) break;
      due = following;
    }
    const rescheduled = Object.freeze({ ...existing, dueAtMs: due });
    this.reminders.set(id, rescheduled);
    return rescheduled;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Storage

/** Reads and writes clients. */
export interface ClientRepository {
  put(c: Client): void;
  get(id: string): Client | undefined;
  all(): readonly Client[];
}

/** Reads and writes invoices. */
export interface InvoiceRepository {
  put(invoice: Invoice): void;
  get(number: string): Invoice | undefined;
  forClient(clientId: string): readonly Invoice[];
  all(): readonly Invoice[];
}

/** Reads and writes reminders. */
export interface ReminderRepository {
  put(reminder: Reminder): void;
  get(id: string): Reminder | undefined;
  all(): readonly Reminder[];
}

/** Everything the business keeps. */
export interface BusinessStore {
  readonly clients: ClientRepository;
  readonly invoices: InvoiceRepository;
  readonly reminders: ReminderRepository;
}

/** Keeps nothing. */
export class NullBusinessStore implements BusinessStore {
  readonly clients: ClientRepository = {
    put: () => undefined,
    get: () => undefined,
    all: () => [],
  };
  readonly invoices: InvoiceRepository = {
    put: () => undefined,
    get: () => undefined,
    forClient: () => [],
    all: () => [],
  };
  readonly reminders: ReminderRepository = {
    put: () => undefined,
    get: () => undefined,
    all: () => [],
  };
}

/** The default store. */
export class InMemoryBusinessStore implements BusinessStore {
  private readonly clientMap = new Map<string, Client>();
  private readonly invoiceMap = new Map<string, Invoice>();
  private readonly reminderMap = new Map<string, Reminder>();

  readonly clients: ClientRepository = {
    put: (c) => void this.clientMap.set(c.id, c),
    get: (id) => this.clientMap.get(id),
    all: () => Object.freeze([...this.clientMap.values()]),
  };

  readonly invoices: InvoiceRepository = {
    // Keyed on the NUMBER, which is why a draft has none: a draft cannot be
    // stored here until it is issued, and that is the point.
    put: (invoice) => void (invoice.number && this.invoiceMap.set(invoice.number, invoice)),
    get: (number) => this.invoiceMap.get(number),
    forClient: (clientId) =>
      Object.freeze([...this.invoiceMap.values()].filter((i) => i.clientId === clientId)),
    all: () => Object.freeze([...this.invoiceMap.values()]),
  };

  readonly reminders: ReminderRepository = {
    put: (r) => void this.reminderMap.set(r.id, r),
    get: (id) => this.reminderMap.get(id),
    all: () => Object.freeze([...this.reminderMap.values()]),
  };
}

/**
 * A bridge to a CRM somebody already uses.
 *
 * ONE WAY OUT BY DEFAULT. Pulling a CRM's whole contact list onto a device is a
 * copy of somebody's business relationships, and it should be a decision rather
 * than what happens when a connection is made.
 */
export class CrmBridge {
  constructor(
    private readonly push?: (c: Client) => Promise<boolean>,
    private readonly pull?: () => Promise<readonly Client[]>,
    private readonly pullAllowed = false,
  ) {}

  get canPush(): boolean {
    return this.push !== undefined;
  }

  get canPull(): boolean {
    return this.pull !== undefined && this.pullAllowed;
  }

  async send(c: Client): Promise<boolean> {
    return this.push ? this.push(c) : false;
  }

  async receive(): Promise<readonly Client[]> {
    if (!this.canPull) return [];
    return this.pull!();
  }
}

/**
 * Something to look at before there is real data.
 *
 * REAL-SHAPED, not lorem: a sample with three-character names and round numbers
 * hides exactly the layout problems it exists to reveal - the long client name,
 * the line that wraps, the total that does not fit its column.
 */
export class BusinessOpsSampleData {
  static clients(): readonly Client[] {
    return Object.freeze([
      client({ id: "c1", name: "Mokoena Plumbing and Drainage", email: "accounts@mokoena.co.za", phoneE164: "+27820000001" }),
      client({ id: "c2", name: "Naledi Spaza", email: "naledi@example.co.za", phoneE164: "+27820000002" }),
      client({ id: "c3", name: "Thabo T", phoneE164: "+27820000003" }),
    ]);
  }

  static invoice(): Invoice {
    return Object.freeze({
      number: "INV-2026-0001",
      clientId: "c1",
      issuedAtMs: 0,
      dueAtMs: 30 * 24 * 60 * 60 * 1000,
      lines: Object.freeze([
        {
          description: "Call-out and first hour",
          quantity: 1,
          unitPrice: Money.fromDecimal(450, "ZAR"),
          taxBasisPoints: 1500,
        },
        {
          description: "Additional hours",
          quantity: 2.5,
          unitPrice: Money.fromDecimal(320, "ZAR"),
          taxBasisPoints: 1500,
        },
        {
          description: "Parts (zero-rated)",
          quantity: 1,
          unitPrice: Money.fromDecimal(187.5, "ZAR"),
          taxBasisPoints: 0,
        },
      ]),
      status: InvoiceStatus.Sent,
      paid: Money.zero("ZAR"),
      currency: "ZAR",
      notes: "",
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Career

/** Who somebody is, for a CV. */
export interface ProfileIdentity {
  readonly fullName: string;
  readonly headline: string;
  readonly email: string;
  readonly phoneE164: string;
  readonly location: string;
  /** Deliberately absent: date of birth, ID number, marital status, a
   * photograph. They are asked for on South African CVs by convention and they
   * are exactly what enables discrimination before a person is met. */
}

/** One job. */
export interface ProfileHistory {
  readonly role: string;
  readonly organisation: string;
  readonly startYear: string;
  /** Empty means CURRENT, which is the one safe assumption on a CV. */
  readonly endYear: string;
  readonly bullets: readonly string[];
}

/** One qualification. */
export interface ProfileEducation {
  readonly qualification: string;
  readonly institution: string;
  readonly year: string;
}

/** One certificate. */
export interface ProfileCertification {
  readonly name: string;
  readonly issuer: string;
  readonly year: string;
}

/** One skill, and how well. */
export interface ProfileSkill {
  readonly name: string;
  /** 1..5, self-assessed and labelled as such wherever it is shown. An
   * unlabelled self-assessment reads as a measurement. */
  readonly level: number;
}

/** One language, and how well. */
export interface ProfileLanguage {
  readonly language: string;
  /** The CEFR-ish words people actually use, not a number. */
  readonly proficiency: "home" | "fluent" | "conversational" | "basic";
}

/** Everything somebody has told this device about their working life. */
export interface CareerProfile {
  readonly identity: ProfileIdentity;
  readonly summary: string;
  readonly history: readonly ProfileHistory[];
  readonly education: readonly ProfileEducation[];
  readonly certifications: readonly ProfileCertification[];
  readonly skills: readonly ProfileSkill[];
  readonly languages: readonly ProfileLanguage[];
}

/** Which field an interview question is filling in. */
export enum ProfileField {
  Name = "name",
  Headline = "headline",
  Contact = "contact",
  Summary = "summary",
  History = "history",
  Education = "education",
  Certifications = "certifications",
  Skills = "skills",
  Languages = "languages",
}

/** One question in the interview. */
export interface InterviewQuestion {
  readonly field: ProfileField;
  /** Asked in plain words, because this is spoken aloud as often as read. */
  readonly prompt: string;
  /** Whether the interview can move on without an answer. */
  readonly isRequired: boolean;
}

/**
 * Builds a profile by asking.
 *
 * ONE QUESTION AT A TIME, in an order that starts with what somebody can answer
 * without thinking. A form with twelve fields is a form nobody finishes, and
 * the people this is for are often filling it in on a phone between other
 * things.
 */
export class CareerInterview {
  private static readonly questions: readonly InterviewQuestion[] = Object.freeze([
    { field: ProfileField.Name, prompt: "What is your full name?", isRequired: true },
    { field: ProfileField.Contact, prompt: "What number or email should they use?", isRequired: true },
    { field: ProfileField.History, prompt: "What is the last job you did?", isRequired: true },
    { field: ProfileField.Headline, prompt: "In a few words, what do you do?", isRequired: false },
    { field: ProfileField.Skills, prompt: "What are you good at?", isRequired: false },
    { field: ProfileField.Education, prompt: "What did you study, if anything?", isRequired: false },
    { field: ProfileField.Languages, prompt: "Which languages do you speak?", isRequired: false },
    { field: ProfileField.Certifications, prompt: "Any certificates or licences?", isRequired: false },
    { field: ProfileField.Summary, prompt: "Anything else worth saying about you?", isRequired: false },
  ]);

  private readonly answered = new Set<ProfileField>();

  /** The next question, or undefined when the required ones are done. */
  next(): InterviewQuestion | undefined {
    return CareerInterview.questions.find((q) => !this.answered.has(q.field));
  }

  answer(field: ProfileField): void {
    this.answered.add(field);
  }

  /** Skipping is allowed for anything not required, and is not a failure. */
  skip(field: ProfileField): boolean {
    const question = CareerInterview.questions.find((q) => q.field === field);
    if (!question || question.isRequired) return false;
    this.answered.add(field);
    return true;
  }

  get isUsable(): boolean {
    return CareerInterview.questions
      .filter((q) => q.isRequired)
      .every((q) => this.answered.has(q.field));
  }

  get progress(): number {
    return this.answered.size / CareerInterview.questions.length;
  }
}

/** What a job is asking for. */
export interface JobSpec {
  readonly title: string;
  readonly organisation: string;
  readonly requirements: readonly string[];
  readonly rawText: string;
}

/** One thing the tailoring chose to do. */
export interface TailoringChoice {
  readonly field: ProfileField;
  readonly action: "emphasise" | "reorder" | "omit";
  /** Why, in words somebody can disagree with. A tailoring nobody can see is a
   * tailoring nobody can correct. */
  readonly reason: string;
}

/**
 * Reorders a profile for a particular job.
 *
 * IT NEVER INVENTS AND NEVER OVERSTATES. Emphasis, order and omission only -
 * every word in the output was already in the profile. A CV with a skill on it
 * that somebody does not have is a CV that fails in the interview, in front of
 * the person it was meant to impress.
 */
export class ProfileTailoring {
  static tailor(profile: CareerProfile, job: JobSpec): { profile: CareerProfile; choices: readonly TailoringChoice[] } {
    const wanted = new Set(
      job.requirements
        .flatMap((r) => r.toLowerCase().split(/[^a-z0-9+#]+/))
        .filter((w) => w.length > 2),
    );
    const choices: TailoringChoice[] = [];

    const scored = [...profile.skills].sort((a, b) => {
      const aWanted = wanted.has(a.name.toLowerCase()) ? 1 : 0;
      const bWanted = wanted.has(b.name.toLowerCase()) ? 1 : 0;
      return bWanted - aWanted || b.level - a.level;
    });
    if (scored.length && scored[0] !== profile.skills[0]) {
      choices.push(
        Object.freeze({
          field: ProfileField.Skills,
          action: "reorder",
          reason: `${scored[0].name} is asked for in the advert, so it goes first`,
        }),
      );
    }

    // History stays in REVERSE CHRONOLOGICAL order regardless of relevance.
    // Reordering jobs by relevance produces a CV with unexplained gaps, which
    // reads as concealment.
    return {
      profile: Object.freeze({ ...profile, skills: Object.freeze(scored) }),
      choices: Object.freeze(choices),
    };
  }
}

/** Turns a profile into CV text. */
export class ProfileToCv {
  /**
   * Plain text, which is the format that always works.
   *
   * An application form that strips formatting is the common case, and text
   * that survives a paste is worth more than a layout that does not.
   */
  static toText(profile: CareerProfile): string {
    const out: string[] = [];
    if (profile.identity.fullName) out.push(profile.identity.fullName.toUpperCase(), "");
    if (profile.identity.headline) out.push(profile.identity.headline, "");
    const contact = [
      profile.identity.email,
      profile.identity.phoneE164,
      profile.identity.location,
    ].filter(Boolean);
    // Only what was GIVEN. An empty field prints nothing rather than a
    // placeholder - a CV with "Phone: -" reads as unfinished.
    if (contact.length) out.push(contact.join("  |  "));
    if (profile.summary) out.push("", "SUMMARY", profile.summary);
    if (profile.history.length) {
      out.push("", "EXPERIENCE");
      for (const job of profile.history) {
        const period = job.startYear
          ? `${job.startYear} - ${job.endYear || "present"}`
          : job.endYear;
        out.push(`${job.role}, ${job.organisation}${period ? `  (${period})` : ""}`);
        out.push(...job.bullets.map((b) => `  - ${b}`));
      }
    }
    if (profile.education.length) {
      out.push("", "EDUCATION");
      out.push(
        ...profile.education.map(
          (e) => `${e.qualification}, ${e.institution}${e.year ? ` (${e.year})` : ""}`,
        ),
      );
    }
    if (profile.certifications.length) {
      out.push("", "CERTIFICATIONS");
      out.push(
        ...profile.certifications.map((c) =>
          [c.name, c.issuer, c.year].filter(Boolean).join(" - "),
        ),
      );
    }
    if (profile.skills.length) {
      out.push("", "SKILLS", profile.skills.map((s) => s.name).join(", "));
    }
    if (profile.languages.length) {
      out.push(
        "",
        "LANGUAGES",
        profile.languages.map((l) => `${l.language} (${l.proficiency})`).join(", "),
      );
    }
    return out.join("\n");
  }
}

/** A document somebody has looked at and agreed to send. */
export interface ApprovedDocument {
  readonly id: string;
  readonly jobTitle: string;
  readonly organisation: string;
  readonly text: string;
  /** WHEN THEY APPROVED IT, not when it was generated. A document that was
   * generated and never read is not approved, and this is the field that
   * distinguishes them. */
  readonly approvedAtMs: number;
}

/**
 * The career profile, on disk.
 *
 * PARAMETERISED, ALWAYS. Everything in here came from something somebody said
 * about their own life, and a store built by concatenating strings into SQL can
 * be rewritten by anybody who can get a sentence into it.
 */
export class SqliteCareerStore {
  static readonly SCHEMA = Object.freeze([
    "CREATE TABLE IF NOT EXISTS profile (" +
      " id TEXT PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL)",
    "CREATE TABLE IF NOT EXISTS approved (" +
      " id TEXT PRIMARY KEY, job_title TEXT NOT NULL, organisation TEXT NOT NULL," +
      " text TEXT NOT NULL, approved_at TEXT NOT NULL)",
  ]);

  constructor(
    private readonly execute?: (sql: string, params: readonly unknown[]) => unknown[][],
    private readonly now: () => string = () => "",
  ) {}

  initialise(): boolean {
    if (!this.execute) return false;
    for (const statement of SqliteCareerStore.SCHEMA) this.execute(statement, []);
    return true;
  }

  saveProfile(id: string, profile: CareerProfile): boolean {
    if (!this.execute || !id.trim()) return false;
    this.execute(
      "INSERT OR REPLACE INTO profile (id, json, updated_at) VALUES (?, ?, ?)",
      [id, JSON.stringify(profile), this.now()],
    );
    return true;
  }

  loadProfile(id: string): CareerProfile | undefined {
    if (!this.execute) return undefined;
    const rows = this.execute("SELECT json FROM profile WHERE id = ?", [id]);
    const json = rows[0]?.[0];
    if (typeof json !== "string") return undefined;
    try {
      return JSON.parse(json) as CareerProfile;
    } catch {
      // A profile that will not parse is MISSING, not empty. Returning a blank
      // profile would look like somebody who has told this nothing, and the
      // next save would overwrite what is actually there.
      return undefined;
    }
  }

  approve(document: ApprovedDocument): boolean {
    if (!this.execute) return false;
    this.execute(
      "INSERT OR REPLACE INTO approved" +
        " (id, job_title, organisation, text, approved_at) VALUES (?, ?, ?, ?, ?)",
      [document.id, document.jobTitle, document.organisation, document.text, this.now()],
    );
    return true;
  }

  /** Everything a person has sent, so they can see it. Nobody else can. */
  approvedDocuments(): unknown[][] {
    if (!this.execute) return [];
    return this.execute(
      "SELECT id, job_title, organisation, approved_at FROM approved ORDER BY approved_at DESC",
      [],
    );
  }
}

// The C# spellings, kept so the two trees line up.
export type IClientBook = ClientBookContract;
export type IInvoiceService = InvoiceServiceContract;
export type IInvoiceNumberGenerator = InvoiceNumberGenerator;
export type IInvoicePdfRenderer = InvoicePdfRenderer;
export type IReminderScheduler = ReminderSchedulerContract;
export type IBusinessStore = BusinessStore;
export type IClientRepository = ClientRepository;
export type IInvoiceRepository = InvoiceRepository;
export type IReminderRepository = ReminderRepository;
