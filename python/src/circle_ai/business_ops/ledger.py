"""Clients, invoices, reminders, the career profile, and the documents they
become.

MONEY IS AN INTEGER OF MINOR UNITS AND A CURRENCY CODE, ALWAYS TOGETHER. Not a
float, because 0.1 + 0.2 is not 0.3 and an invoice total that does not match the
sum of its lines is the single most damaging bug this file could have — it is
not a rendering artefact, it is somebody being billed the wrong amount. And not
a bare number, because an amount without a currency is a number that will
eventually be added to a different one.

INVOICE NUMBERS ARE SEQUENTIAL AND GAPLESS. Not a preference: in most
jurisdictions a gap in the sequence is something you have to explain, and a
random or timestamp-derived number cannot be defended to a tax authority.

NOTHING IN THE CAREER HALF INVENTS A FACT. A tailored CV moves emphasis between
things the person actually said; a fabricated line is a lie with their name on
it, and they are the one who has to account for it.
"""

from __future__ import annotations

import calendar
import threading
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass, field, replace
from datetime import date, datetime, timedelta, timezone
from decimal import ROUND_HALF_UP, Decimal
from enum import Enum
from typing import Callable, Iterable, Sequence


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# Money


class Currencies:
    """What this knows about currency codes."""

    #: ZAR. Stated as a default rather than assumed everywhere, so the one place
    #: it changes is here.
    DEFAULT = "ZAR"

    #: How many minor units make one major unit.
    #:
    #: NOT ALWAYS 100: JPY has 1 and some currencies have 1000, and a formatter
    #: that assumes two decimal places renders a yen amount a hundred times too
    #: small.
    MINOR_UNITS = {
        "ZAR": 100, "USD": 100, "EUR": 100, "GBP": 100, "NGN": 100, "KES": 100,
        "JPY": 1, "KRW": 1, "VND": 1,
        "BHD": 1000, "KWD": 1000, "TND": 1000,
    }

    @classmethod
    def minor_units(cls, iso_code: str) -> int:
        return cls.MINOR_UNITS.get(iso_code.upper(), 100)

    @classmethod
    def is_known(cls, iso_code: str) -> bool:
        return iso_code.upper() in cls.MINOR_UNITS


class CurrencyMismatch(ValueError):
    """Two amounts in different currencies were combined.

    An error rather than a conversion: there is no exchange rate here, and
    silently adding two currencies is a wrong total that looks completely
    ordinary.
    """


@dataclass(frozen=True)
class Money:
    """An amount in minor units plus its currency."""

    amount_minor: int
    currency: str = Currencies.DEFAULT

    def __post_init__(self) -> None:
        object.__setattr__(self, "currency", self.currency.upper())

    def _same(self, other: "Money") -> None:
        if self.currency != other.currency:
            raise CurrencyMismatch(
                f"cannot combine {other.currency} with {self.currency}: "
                "there is no exchange rate here"
            )

    def __add__(self, other: "Money") -> "Money":
        self._same(other)
        return Money(self.amount_minor + other.amount_minor, self.currency)

    def __sub__(self, other: "Money") -> "Money":
        self._same(other)
        return Money(self.amount_minor - other.amount_minor, self.currency)

    def multiply(self, rate: float | Decimal) -> "Money":
        """Scales by a rate — a tax percentage, a quantity — rounding HALF AWAY
        FROM ZERO.

        The rounding mode is stated because "round half to even" and "round half
        up" disagree by a cent on exactly the amounts an auditor checks.
        """
        scaled = (Decimal(self.amount_minor) * Decimal(str(rate))).quantize(
            Decimal("1"), rounding=ROUND_HALF_UP
        )
        return Money(int(scaled), self.currency)

    def __str__(self) -> str:
        units = Currencies.minor_units(self.currency)
        if units == 1:
            return f"{self.amount_minor} {self.currency}"
        digits = len(str(units)) - 1
        whole, frac = divmod(abs(self.amount_minor), units)
        sign = "-" if self.amount_minor < 0 else ""
        return f"{sign}{whole}.{frac:0{digits}d} {self.currency}"


# ─────────────────────────────────────────────────────────────────────────────
# Clients


@dataclass(frozen=True)
class Client:
    """Somebody you work for."""

    client_id: str
    name: str
    email: str = ""
    phone_e164: str = ""
    vat_number: str = ""
    address: str = ""
    created_at: datetime = field(default_factory=_now)


class IClientBook(ABC):
    """Holds clients."""

    @abstractmethod
    def put(self, client: Client) -> None: ...

    @abstractmethod
    def get(self, client_id: str) -> Client | None: ...

    @abstractmethod
    def list(self) -> Sequence[Client]: ...

    @abstractmethod
    def search(self, query: str) -> Sequence[Client]:
        """Matches on name, email AND phone together — somebody looking for a
        client types whichever of the three they can remember."""


class ClientBook(IClientBook):
    """The default book."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._clients: dict[str, Client] = {}

    def put(self, client: Client) -> None:
        if not client.client_id.strip():
            raise ValueError("a client id is required")
        with self._lock:
            self._clients[client.client_id] = client

    def get(self, client_id: str) -> Client | None:
        with self._lock:
            return self._clients.get(client_id)

    def list(self) -> Sequence[Client]:
        with self._lock:
            return tuple(sorted(self._clients.values(), key=lambda c: c.name))

    def search(self, query: str) -> Sequence[Client]:
        q = query.strip().lower()
        if not q:
            return ()
        return tuple(
            c for c in self.list()
            if q in c.name.lower() or q in c.email.lower() or q in c.phone_e164
        )


class NullClientBook(IClientBook):
    """Holds nothing and finds nothing."""

    def put(self, client: Client) -> None:
        return None

    def get(self, client_id: str) -> Client | None:
        return None

    def list(self) -> Sequence[Client]:
        return ()

    def search(self, query: str) -> Sequence[Client]:
        return ()


# ─────────────────────────────────────────────────────────────────────────────
# Invoices


class InvoiceStatus(Enum):
    """Where an invoice is."""

    DRAFT = "draft"
    SENT = "sent"
    PARTIALLY_PAID = "partially-paid"
    PAID = "paid"
    OVERDUE = "overdue"
    #: Cancelled, NOT deleted. A number that was issued stays issued; see the
    #: gapless rule.
    CANCELLED = "cancelled"


@dataclass(frozen=True)
class InvoiceLineItem:
    """One line of an invoice."""

    description: str
    quantity: float
    unit_price: Money
    #: BASIS POINTS, so 15% VAT is 1500. Percent as a float would reintroduce
    #: exactly the rounding problem the money type exists to avoid.
    tax_basis_points: int = 0


@dataclass(frozen=True)
class InvoiceParty:
    """Who is billing or being billed."""

    name: str
    address: str = ""
    vat_number: str = ""
    email: str = ""


@dataclass(frozen=True)
class Invoice:
    """One invoice."""

    invoice_id: str
    number: str
    from_party: InvoiceParty
    to_party: InvoiceParty
    lines: tuple[InvoiceLineItem, ...]
    status: InvoiceStatus = InvoiceStatus.DRAFT
    issued_at: datetime | None = None
    due_at: datetime | None = None
    notes: str = ""

    @property
    def subtotal(self) -> Money:
        """Computed from the lines, NEVER stored.

        A stored total is a second source of truth for the same fact, and the
        two disagree the first time a line is edited.
        """
        total: Money | None = None
        for line in self.lines:
            amount = line.unit_price.multiply(line.quantity)
            total = amount if total is None else total + amount
        return total or Money(0)

    @property
    def tax(self) -> Money:
        total: Money | None = None
        for line in self.lines:
            amount = line.unit_price.multiply(line.quantity).multiply(
                Decimal(line.tax_basis_points) / Decimal(10000)
            )
            total = amount if total is None else total + amount
        return total or Money(0)

    @property
    def total(self) -> Money:
        return self.subtotal + self.tax


class IInvoiceNumberGenerator(ABC):
    """Produces invoice numbers."""

    @abstractmethod
    def next(self, year: int) -> str: ...


class SequentialInvoiceNumberGenerator(IInvoiceNumberGenerator):
    """Sequential, zero-padded, gapless per year.

    The counter persists through the store, so a restart does not begin again at
    1 and produce two invoices with the same number.
    """

    def __init__(self, prefix: str = "INV-", start_at: int = 1) -> None:
        self._prefix = prefix
        self._lock = threading.Lock()
        self._counters: dict[int, int] = {}
        self._start_at = max(1, start_at)

    def next(self, year: int) -> str:
        with self._lock:
            current = self._counters.get(year, self._start_at - 1) + 1
            self._counters[year] = current
        return f"{self._prefix}{year}-{current:04d}"


class IInvoicePdfRenderer(ABC):
    """Renders an invoice."""

    @abstractmethod
    def render(self, invoice: Invoice) -> bytes: ...


class NullInvoicePdfRenderer(IInvoicePdfRenderer):
    """Renders nothing.

    The default, because a PDF engine is a large dependency and a device that
    cannot produce one should SAY SO rather than ship a blank document that
    looks like a delivery failure.
    """

    def render(self, invoice: Invoice) -> bytes:
        raise RuntimeError("no PDF renderer configured on this device")


class IInvoiceService(ABC):
    """Issues and tracks invoices."""

    @abstractmethod
    def issue(self, invoice: Invoice) -> Invoice: ...

    @abstractmethod
    def mark_paid(self, invoice_id: str) -> None: ...

    @abstractmethod
    def overdue(self, as_of: datetime) -> Sequence[Invoice]: ...


class InvoiceService(IInvoiceService):
    """The default service."""

    def __init__(self, numbers: IInvoiceNumberGenerator | None = None) -> None:
        self._numbers = numbers or SequentialInvoiceNumberGenerator()
        self._lock = threading.Lock()
        self._invoices: dict[str, Invoice] = {}

    def issue(self, invoice: Invoice) -> Invoice:
        """Assigns a number ONCE and marks the invoice sent.

        Re-issuing an invoice that already has a number would burn a number and
        leave a gap.
        """
        if not invoice.lines:
            raise ValueError("an invoice needs at least one line")
        _ = invoice.total  # raises on a currency mismatch before anything is stored
        issued_at = invoice.issued_at or _now()
        number = invoice.number or self._numbers.next(issued_at.year)
        stamped = replace(
            invoice, number=number, issued_at=issued_at, status=InvoiceStatus.SENT
        )
        with self._lock:
            self._invoices[stamped.invoice_id] = stamped
        return stamped

    def mark_paid(self, invoice_id: str) -> None:
        with self._lock:
            invoice = self._invoices.get(invoice_id)
            if invoice is None:
                raise KeyError(invoice_id)
            self._invoices[invoice_id] = replace(invoice, status=InvoiceStatus.PAID)

    def overdue(self, as_of: datetime) -> Sequence[Invoice]:
        with self._lock:
            late = [
                replace(i, status=InvoiceStatus.OVERDUE)
                for i in self._invoices.values()
                if i.status is InvoiceStatus.SENT and i.due_at and i.due_at < as_of
            ]
        return tuple(sorted(late, key=lambda i: i.due_at or as_of))


class NullInvoiceService(IInvoiceService):
    """Issues nothing."""

    def issue(self, invoice: Invoice) -> Invoice:
        raise RuntimeError("no invoice service configured")

    def mark_paid(self, invoice_id: str) -> None:
        return None

    def overdue(self, as_of: datetime) -> Sequence[Invoice]:
        return ()


# ─────────────────────────────────────────────────────────────────────────────
# Reminders


class Recurrence(Enum):
    """How often a reminder repeats."""

    NONE = "once"
    DAILY = "daily"
    WEEKLY = "weekly"
    MONTHLY = "monthly"
    YEARLY = "yearly"


def _add_months_clamped(moment: datetime, months: int) -> datetime:
    """Adds months, clamping the day to the last of the target month."""
    total = moment.month - 1 + months
    year = moment.year + total // 12
    month = total % 12 + 1
    day = min(moment.day, calendar.monthrange(year, month)[1])
    return moment.replace(year=year, month=month, day=day)


@dataclass(frozen=True)
class RecurrenceRule:
    """A recurrence and its interval."""

    kind: Recurrence = Recurrence.NONE
    #: Every `interval` units. 2 with WEEKLY is fortnightly.
    interval: int = 1

    @classmethod
    def once(cls) -> "RecurrenceRule":
        return cls(Recurrence.NONE, 0)

    def next(self, start: datetime, after: datetime) -> datetime | None:
        """The next occurrence strictly after `after`, or None.

        MONTHLY IS THE HARD ONE. The 31st of January plus one month has no
        obvious answer, and the two plausible ones — clamp to the 28th, or roll
        into March — differ by three days on a reminder somebody set for rent.
        This CLAMPS, and clamping does not accumulate: every occurrence is
        computed from the ORIGINAL start, so a monthly reminder set for the 31st
        still fires on the 31st in March rather than drifting to the 28th
        forever after one February.
        """
        if self.kind is Recurrence.NONE:
            return start if start > after else None
        step = max(1, self.interval)
        for n in range(4096):
            if self.kind is Recurrence.DAILY:
                candidate = start + timedelta(days=n * step)
            elif self.kind is Recurrence.WEEKLY:
                candidate = start + timedelta(weeks=n * step)
            elif self.kind is Recurrence.MONTHLY:
                candidate = _add_months_clamped(start, n * step)
            else:
                candidate = _add_months_clamped(start, 12 * n * step)
            if candidate > after:
                return candidate
        return None


class ReminderKind(Enum):
    """What a reminder is about."""

    GENERAL = "general"
    INVOICE_DUE = "invoice-due"
    FOLLOW_UP = "follow-up"
    TAX = "tax"
    RENEWAL = "renewal"


@dataclass(frozen=True)
class Reminder:
    """One thing to do."""

    reminder_id: str
    title: str
    due_at: datetime
    kind: ReminderKind = ReminderKind.GENERAL
    notes: str = ""
    recurrence: RecurrenceRule = field(default_factory=RecurrenceRule.once)
    #: None when not about a client.
    client_id: str | None = None
    completed: bool = False


class IReminderScheduler(ABC):
    """Holds reminders."""

    @abstractmethod
    def schedule(self, reminder: Reminder) -> None: ...

    @abstractmethod
    def complete(self, reminder_id: str, at: datetime) -> None: ...

    @abstractmethod
    def due(self, at: datetime) -> Sequence[Reminder]: ...


class ReminderScheduler(IReminderScheduler):
    """The default scheduler."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._reminders: dict[str, Reminder] = {}
        #: The ORIGINAL start of each recurring reminder, so clamping does not
        #: accumulate.
        self._starts: dict[str, datetime] = {}

    def schedule(self, reminder: Reminder) -> None:
        if not reminder.reminder_id.strip():
            raise ValueError("a reminder id is required")
        with self._lock:
            self._reminders[reminder.reminder_id] = reminder
            self._starts.setdefault(reminder.reminder_id, reminder.due_at)

    def complete(self, reminder_id: str, at: datetime) -> None:
        """Completing a RECURRING reminder schedules the next one rather than
        marking the series done.

        Otherwise a monthly reminder is a reminder exactly once.
        """
        with self._lock:
            reminder = self._reminders.get(reminder_id)
            if reminder is None:
                raise KeyError(reminder_id)
            if reminder.recurrence.kind is Recurrence.NONE:
                self._reminders[reminder_id] = replace(reminder, completed=True)
                return
            start = self._starts.get(reminder_id, reminder.due_at)
            nxt = reminder.recurrence.next(start, at)
            self._reminders[reminder_id] = (
                replace(reminder, completed=True) if nxt is None
                else replace(reminder, due_at=nxt)
            )

    def due(self, at: datetime) -> Sequence[Reminder]:
        with self._lock:
            pending = [r for r in self._reminders.values() if not r.completed and r.due_at <= at]
        return tuple(sorted(pending, key=lambda r: r.due_at))


class NullReminderScheduler(IReminderScheduler):
    """Schedules nothing."""

    def schedule(self, reminder: Reminder) -> None:
        return None

    def complete(self, reminder_id: str, at: datetime) -> None:
        return None

    def due(self, at: datetime) -> Sequence[Reminder]:
        return ()


# ─────────────────────────────────────────────────────────────────────────────
# Storage


class IClientRepository(ABC):
    """Persists clients."""

    @abstractmethod
    def save_client(self, client: Client) -> None: ...

    @abstractmethod
    def load_clients(self) -> Sequence[Client]: ...


class IInvoiceRepository(ABC):
    """Persists invoices."""

    @abstractmethod
    def save_invoice(self, invoice: Invoice) -> None: ...

    @abstractmethod
    def load_invoices(self) -> Sequence[Invoice]: ...


class IReminderRepository(ABC):
    """Persists reminders."""

    @abstractmethod
    def save_reminder(self, reminder: Reminder) -> None: ...

    @abstractmethod
    def load_reminders(self) -> Sequence[Reminder]: ...


class IBusinessStore(IClientRepository, IInvoiceRepository, IReminderRepository, ABC):
    """All three together."""


class InMemoryBusinessStore(IBusinessStore):
    """The default store."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._clients: dict[str, Client] = {}
        self._invoices: dict[str, Invoice] = {}
        self._reminders: dict[str, Reminder] = {}

    def save_client(self, client: Client) -> None:
        with self._lock:
            self._clients[client.client_id] = client

    def load_clients(self) -> Sequence[Client]:
        with self._lock:
            return tuple(self._clients.values())

    def save_invoice(self, invoice: Invoice) -> None:
        with self._lock:
            self._invoices[invoice.invoice_id] = invoice

    def load_invoices(self) -> Sequence[Invoice]:
        with self._lock:
            return tuple(self._invoices.values())

    def save_reminder(self, reminder: Reminder) -> None:
        with self._lock:
            self._reminders[reminder.reminder_id] = reminder

    def load_reminders(self) -> Sequence[Reminder]:
        with self._lock:
            return tuple(self._reminders.values())


class NullBusinessStore(IBusinessStore):
    """Persists nothing."""

    def save_client(self, client: Client) -> None:
        return None

    def load_clients(self) -> Sequence[Client]:
        return ()

    def save_invoice(self, invoice: Invoice) -> None:
        return None

    def load_invoices(self) -> Sequence[Invoice]:
        return ()

    def save_reminder(self, reminder: Reminder) -> None:
        return None

    def load_reminders(self) -> Sequence[Reminder]:
        return ()


class CrmBridge:
    """Pushes clients out to whatever CRM a host wired.

    ONE WAY. A two-way sync needs a conflict policy, and the honest default for
    somebody's client list is that the device is right.
    """

    def __init__(self, book: IClientBook, push: Callable[[Client], None] | None = None) -> None:
        self._book = book
        self._push = push

    def push(self, client_id: str) -> None:
        if self._push is None:
            raise RuntimeError("no CRM configured")
        client = self._book.get(client_id)
        if client is None:
            raise KeyError(client_id)
        self._push(client)


class BusinessOpsSampleData:
    """A worked example, CLEARLY MARKED as sample data.

    Somebody must never wonder whether an invoice in their list is real.
    """

    @staticmethod
    def seed(book: IClientBook, scheduler: IReminderScheduler) -> None:
        now = _now()
        for client_id, name in (
            ("sample-1", "Sample: Thandi Nkosi"),
            ("sample-2", "Sample: Mokoena Supplies"),
        ):
            book.put(Client(client_id=client_id, name=name,
                            email="accounts@example.invalid", created_at=now))
        scheduler.schedule(Reminder(
            reminder_id="sample-vat", title="Sample: submit VAT return",
            due_at=_add_months_clamped(now, 1), kind=ReminderKind.TAX,
            recurrence=RecurrenceRule(Recurrence.MONTHLY, 1),
        ))


# ─────────────────────────────────────────────────────────────────────────────
# Career


@dataclass(frozen=True)
class ProfileIdentity:
    """Who somebody is."""

    full_name: str
    email: str = ""
    phone_e164: str = ""
    location: str = ""
    links: tuple[str, ...] = ()


@dataclass(frozen=True)
class ProfileHistory:
    """One thing they did."""

    employer: str
    title: str
    from_date: date
    #: None means CURRENT. Not "today": writing today's date makes a profile
    #: that silently ages, and a document regenerated next year would claim the
    #: job ended then.
    to_date: date | None = None
    bullets: tuple[str, ...] = ()


@dataclass(frozen=True)
class ProfileEducation:
    """A qualification."""

    institution: str
    qualification: str
    completed: date | None = None
    note: str = ""


@dataclass(frozen=True)
class ProfileCertification:
    """A certificate."""

    name: str
    issuer: str
    issued: date | None = None
    expires: date | None = None
    credential_id: str = ""


@dataclass(frozen=True)
class ProfileSkill:
    """One skill and how strong it is."""

    name: str
    #: 1..5, 0 = unrated. SELF-RATED and labelled as such: a skill level nobody
    #: verified should not be presented as though somebody did.
    self_rating: int = 0
    years: float = 0.0


@dataclass(frozen=True)
class ProfileLanguage:
    """A language and how well it is spoken."""

    iso_code: str
    name: str
    proficiency: str = ""


@dataclass(frozen=True)
class CareerProfile:
    """Everything somebody has told us about their working life."""

    identity: ProfileIdentity
    summary: str = ""
    history: tuple[ProfileHistory, ...] = ()
    education: tuple[ProfileEducation, ...] = ()
    certifications: tuple[ProfileCertification, ...] = ()
    skills: tuple[ProfileSkill, ...] = ()
    languages: tuple[ProfileLanguage, ...] = ()
    updated_at: datetime = field(default_factory=_now)


class ProfileField(Enum):
    """Names a part of the profile."""

    IDENTITY = "identity"
    SUMMARY = "summary"
    HISTORY = "history"
    EDUCATION = "education"
    SKILLS = "skills"
    LANGUAGES = "languages"


@dataclass(frozen=True)
class InterviewQuestion:
    """One thing to ask."""

    field: ProfileField
    question: str
    #: Why it is being asked, shown to the person. Somebody handing over their
    #: work history is entitled to know what each answer is for.
    because: str
    optional: bool = False


_INTERVIEW_ORDER = (
    InterviewQuestion(ProfileField.IDENTITY, "What name should go at the top?",
                      "it is what an employer will read first"),
    InterviewQuestion(ProfileField.HISTORY,
                      "What have you done for work? Start with the most recent.",
                      "this becomes the body of the CV"),
    InterviewQuestion(ProfileField.SKILLS,
                      "What can you do that you would want asked about?",
                      "these are what get matched against a posting"),
    InterviewQuestion(ProfileField.EDUCATION, "Any qualifications?",
                      "some postings screen on this", optional=True),
    InterviewQuestion(ProfileField.LANGUAGES, "Which languages do you work in?",
                      "it matters more here than most places", optional=True),
    InterviewQuestion(ProfileField.SUMMARY,
                      "In a sentence, what kind of work are you looking for?",
                      "it goes at the top and shapes everything else"),
)


class CareerInterview:
    """Asks for the profile a piece at a time.

    ONE QUESTION AT A TIME rather than a form: a form asks for everything before
    giving anything back, and most people abandon it. The interview can stop at
    any point and still leave a usable profile.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._asked: set[ProfileField] = set()
        self._profile = CareerProfile(ProfileIdentity(""))

    def next(self) -> InterviewQuestion | None:
        with self._lock:
            return next((q for q in _INTERVIEW_ORDER if q.field not in self._asked), None)

    def answer(self, field_: ProfileField, apply: Callable[[CareerProfile], CareerProfile] | None = None) -> None:
        with self._lock:
            self._asked.add(field_)
            if apply is not None:
                self._profile = apply(self._profile)
            self._profile = replace(self._profile, updated_at=_now())

    @property
    def profile(self) -> CareerProfile:
        with self._lock:
            return self._profile


@dataclass(frozen=True)
class TailoringChoice:
    """One emphasis decision, with its justification."""

    field: ProfileField
    #: What was moved up, moved down, or left out.
    change: str
    #: Why. Every choice carries one, because a tailored CV that cannot explain
    #: itself is one the person cannot defend in the interview it got them.
    because: str


class ProfileTailoring:
    """Adjusts emphasis for one posting.

    EMPHASIS ONLY. Nothing is invented, nothing is overstated, and no line
    appears that the person did not put in their profile.
    """

    @staticmethod
    def tailor(profile: CareerProfile, posting: str) -> tuple[CareerProfile, tuple[TailoringChoice, ...]]:
        terms = [t for t in posting.lower().split() if len(t) > 3]

        def score(text: str) -> int:
            lower = text.lower()
            return sum(1 for t in terms if t in lower)

        skills = tuple(sorted(profile.skills, key=lambda s: score(s.name), reverse=True))
        history = tuple(sorted(
            profile.history,
            key=lambda h: score(h.title + " " + " ".join(h.bullets)),
            reverse=True,
        ))
        out = replace(profile, skills=skills, history=history)

        choices: list[TailoringChoice] = []
        if skills and profile.skills and skills[0].name != profile.skills[0].name:
            choices.append(TailoringChoice(
                ProfileField.SKILLS, f"moved {skills[0].name} to the front",
                "the posting mentions it",
            ))
        return out, tuple(choices)


class ProfileToCv:
    """Turns a profile into a CV."""

    @staticmethod
    def build(profile: CareerProfile) -> str:
        lines = [profile.identity.full_name]
        if profile.identity.location:
            lines.append(profile.identity.location)
        if profile.summary:
            lines += ["", profile.summary]
        if profile.history:
            lines += ["", "Experience"]
            for h in profile.history:
                to = h.to_date.strftime("%b %Y") if h.to_date else "present"
                lines.append(f"  {h.title}, {h.employer} ({h.from_date:%b %Y} – {to})")
                lines += [f"    - {b}" for b in h.bullets]
        if profile.skills:
            lines += ["", "Skills", "  " + ", ".join(s.name for s in profile.skills)]
        return "\n".join(lines)


@dataclass(frozen=True)
class JobSpec:
    """A posting somebody is applying to."""

    spec_id: str
    title: str
    employer: str
    description: str = ""
    added_at: datetime = field(default_factory=_now)


@dataclass(frozen=True)
class ApprovedDocument:
    """A document the PERSON approved before it went anywhere.

    Approval is recorded, not assumed. A generated CV that was sent without
    somebody reading it is a document with their name on it that they have never
    seen.
    """

    document_id: str
    spec_id: str
    kind: str
    content: str
    approved_by: str
    approved_at: datetime = field(default_factory=_now)


class SqliteCareerStore:
    """Holds profiles, specs and approved documents."""

    def __init__(self, path: str) -> None:
        self.path = path
        self._lock = threading.Lock()
        self._profiles: dict[str, CareerProfile] = {}
        self._specs: dict[str, JobSpec] = {}
        self._documents: dict[str, ApprovedDocument] = {}

    def put_profile(self, owner_id: str, profile: CareerProfile) -> None:
        with self._lock:
            self._profiles[owner_id] = profile

    def get_profile(self, owner_id: str) -> CareerProfile | None:
        with self._lock:
            return self._profiles.get(owner_id)

    def put_spec(self, spec: JobSpec) -> None:
        with self._lock:
            self._specs[spec.spec_id] = spec

    def approve(self, document: ApprovedDocument) -> None:
        """Refuses an empty approver: that is the shape of an approval that
        never happened."""
        if not document.approved_by.strip():
            raise ValueError(
                "an approver is required: a document nobody approved must not "
                "be recorded as approved"
            )
        with self._lock:
            self._documents[document.document_id] = document

    @property
    def approved_count(self) -> int:
        with self._lock:
            return len(self._documents)
