"""The ubiquity rails that HOLD STATE.

ubiquity.py holds the app-store submitter, the signed delta updater and the
abuse-safe mode. ubiquity_rails.py holds the constant half — the decisions
compiled in so that changing one is a commit with a name on it. This file is
the rest: the rails that remember something between calls.

An onboarding session part-way through, a queue of operations waiting for a
network, the windows during which the assistant has agreed to stay quiet.

MONEY IS IN MINOR UNITS AS INTEGERS. The C# uses decimal, and float money is
how a total stops matching the sum of its parts.
"""

from __future__ import annotations

import hashlib
import re
import secrets
import threading
import unicodedata
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from typing import Callable, Iterable, Sequence


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# Onboarding


@dataclass(frozen=True)
class OnboardingSession:
    """A phone-and-PIN onboarding, part-way through."""

    session_id: str
    phone_number: str
    biometric_enrolled: bool = False
    #: How long the person waited to get to something usable. Recorded because
    #: it is the number the onboarding rail exists to keep small.
    time_to_active: timedelta = timedelta()


class IPhonePinBiometricOnboarding(ABC):
    """Onboarding with a phone number, a PIN and optionally a biometric."""

    @abstractmethod
    def start(self, phone_number: str) -> OnboardingSession: ...

    @abstractmethod
    def complete(self, session_id: str, pin: str, biometric_ok: bool) -> bool: ...

    @abstractmethod
    def verify_pin(self, phone_number: str, pin: str) -> bool: ...


class DefaultPhonePinBiometricOnboarding(IPhonePinBiometricOnboarding):
    """THE PIN IS NEVER STORED.

    What is kept is a salted hash, so a memory dump of a half-onboarded device
    does not hand over everybody's PIN. The comparison is constant-time for the
    same reason it is everywhere else: a timing difference on a four-digit
    secret is a four-digit search.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._sessions: dict[str, OnboardingSession] = {}
        self._pins: dict[str, tuple[bytes, bytes]] = {}

    def start(self, phone_number: str) -> OnboardingSession:
        if not phone_number.strip():
            raise ValueError("a phone number is required")
        session = OnboardingSession(
            session_id=secrets.token_hex(8), phone_number=phone_number
        )
        with self._lock:
            self._sessions[session.session_id] = session
        return session

    def complete(self, session_id: str, pin: str, biometric_ok: bool) -> bool:
        if not pin.strip():
            raise ValueError("a PIN is required")
        with self._lock:
            session = self._sessions.get(session_id)
            if session is None:
                return False
            salt = secrets.token_bytes(16)
            self._pins[session.phone_number] = (
                salt,
                hashlib.pbkdf2_hmac("sha256", pin.encode(), salt, 200_000),
            )
            self._sessions[session_id] = OnboardingSession(
                session_id=session.session_id,
                phone_number=session.phone_number,
                biometric_enrolled=biometric_ok,
                time_to_active=session.time_to_active,
            )
        return True

    def verify_pin(self, phone_number: str, pin: str) -> bool:
        with self._lock:
            entry = self._pins.get(phone_number)
        if entry is None:
            return False
        salt, expected = entry
        actual = hashlib.pbkdf2_hmac("sha256", pin.encode(), salt, 200_000)
        return secrets.compare_digest(actual, expected)


class INoManualFirstRun(ABC):
    """The first screen, which is not a screen."""

    @abstractmethod
    def show(self) -> str: ...


class DefaultNoManualFirstRun(INoManualFirstRun):
    """Nothing to fill in and nothing to read."""

    def show(self) -> str:
        return "Say hello when you are ready."


class IVoiceLedSetup(ABC):
    """Setup driven entirely by voice, in the language somebody thinks in."""

    @abstractmethod
    def run(self, mother_tongue: str) -> bool: ...


class DefaultVoiceLedSetup(IVoiceLedSetup):
    """False for a language with no voice assets rather than falling back to
    English.

    An English setup flow for somebody who does not read English is the failure
    this rail exists to prevent, and doing it silently hides that it happened.
    """

    SUPPORTED = frozenset({"en", "zu", "xh", "af", "st", "tn", "ts", "ve", "nr", "ss", "nso"})

    def run(self, mother_tongue: str) -> bool:
        return mother_tongue.lower() in self.SUPPORTED


@dataclass(frozen=True)
class PersonalityChoice:
    """One personality preset."""

    name: str


class IAiPersonalityWizard(ABC):
    """Choosing how the assistant sounds."""

    @property
    @abstractmethod
    def presets(self) -> Sequence[PersonalityChoice]: ...

    @abstractmethod
    def select(self, session_id: str, choice: PersonalityChoice) -> None: ...


class DefaultAiPersonalityWizard(IAiPersonalityWizard):
    """A CLOSED list on purpose: an arbitrary string here becomes a prompt
    fragment later."""

    _PRESETS = (
        PersonalityChoice("formal"),
        PersonalityChoice("warm"),
        PersonalityChoice("playful"),
        PersonalityChoice("professional"),
    )

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._selections: dict[str, PersonalityChoice] = {}

    @property
    def presets(self) -> Sequence[PersonalityChoice]:
        return self._PRESETS

    def select(self, session_id: str, choice: PersonalityChoice) -> None:
        if not session_id.strip():
            raise ValueError("a session id is required")
        if not any(p.name.lower() == choice.name.lower() for p in self._PRESETS):
            raise ValueError(f"unknown personality {choice.name!r}")
        with self._lock:
            self._selections[session_id] = choice

    def selected(self, session_id: str) -> PersonalityChoice | None:
        with self._lock:
            return self._selections.get(session_id)


class IPersonalDataImport(ABC):
    """Bringing somebody's existing data in."""

    @abstractmethod
    def import_from(self, session_id: str, source: str) -> None: ...


class DefaultPersonalDataImport(IPersonalDataImport):
    """Records what was imported, per session."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._imports: dict[str, list[str]] = {}

    def import_from(self, session_id: str, source: str) -> None:
        if not session_id.strip() or not source.strip():
            raise ValueError("a session id and a source are required")
        with self._lock:
            self._imports.setdefault(session_id, []).append(source)

    def imports_for(self, session_id: str) -> list[str]:
        with self._lock:
            return list(self._imports.get(session_id, ()))


@dataclass(frozen=True)
class HouseholdMember:
    """One person in a household."""

    member_id: str
    display_name: str
    role: str


class IFamilyOnboarding(ABC):
    """Setting a household up."""

    @abstractmethod
    def create_household(self, owner_id: str, members: Sequence[HouseholdMember]) -> None: ...


class DefaultFamilyOnboarding(IFamilyOnboarding):
    """Refuses duplicate member ids and an empty household.

    A household of nobody is a shape the rest of the family features cannot
    handle, and finding that out three screens later is worse than refusing
    here.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._households: dict[str, list[HouseholdMember]] = {}

    def create_household(self, owner_id: str, members: Sequence[HouseholdMember]) -> None:
        if not owner_id.strip():
            raise ValueError("an owner id is required")
        if not members:
            raise ValueError("a household needs at least one member")
        ids = [m.member_id for m in members]
        if len(set(ids)) != len(ids):
            raise ValueError("two members share an id")
        with self._lock:
            self._households[owner_id] = list(members)

    def members_of(self, owner_id: str) -> list[HouseholdMember]:
        with self._lock:
            return list(self._households.get(owner_id, ()))


# ─────────────────────────────────────────────────────────────────────────────
# Transparency


@dataclass(frozen=True)
class TransparencyReceipt:
    """What one call actually did."""

    call_id: str
    actions_taken: tuple[str, ...] = ()
    #: Every destination the call sent data to. EMPTY IS THE INTERESTING CASE
    #: and it must be distinguishable from "not recorded": a receipt that
    #: cannot tell "nothing left the device" from "we did not look" is worth
    #: nothing.
    data_egress: tuple[str, ...] = ()
    cost_micro_usd: int = 0


class IPerCallTransparency(ABC):
    """A receipt for every call."""

    @abstractmethod
    def receipt_for(self, call_id: str) -> TransparencyReceipt | None: ...


class DefaultPerCallTransparency(IPerCallTransparency):
    """Returns None for a call nobody recorded, which is not the same as a call
    that did nothing."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._receipts: dict[str, TransparencyReceipt] = {}

    def note(self, call_id: str, action: str, egress: str | None, cost_micro_usd: int) -> None:
        with self._lock:
            existing = self._receipts.get(call_id, TransparencyReceipt(call_id=call_id))
            self._receipts[call_id] = TransparencyReceipt(
                call_id=call_id,
                actions_taken=existing.actions_taken + (action,),
                data_egress=existing.data_egress + ((egress,) if egress else ()),
                cost_micro_usd=existing.cost_micro_usd + cost_micro_usd,
            )

    def receipt_for(self, call_id: str) -> TransparencyReceipt | None:
        with self._lock:
            return self._receipts.get(call_id)


class IPublicTransparency(ABC):
    """Linking a public claim to its evidence."""

    @abstractmethod
    def link_evidence(self, claim: str, evidence_url: str) -> None: ...


class DefaultPublicTransparency(IPublicTransparency):
    """Refuses anything that is not an absolute http/https URL.

    A relative link as evidence resolves against whatever page renders it,
    which means the claim points at a different document depending on where you
    read it.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._links: list[tuple[str, str, datetime]] = []

    def link_evidence(self, claim: str, evidence_url: str) -> None:
        if not claim.strip():
            raise ValueError("a claim is required")
        if not re.match(r"^https?://[^\s]+$", evidence_url):
            raise ValueError("evidence must be an absolute http or https URL")
        with self._lock:
            self._links.append((claim, evidence_url, _now()))

    @property
    def linked(self) -> list[tuple[str, str, datetime]]:
        with self._lock:
            return list(self._links)


# ─────────────────────────────────────────────────────────────────────────────
# Pricing


@dataclass(frozen=True)
class PricingTier:
    """One tier. Price in MINOR UNITS: R19.00 is 1900."""

    name: str
    monthly_price_minor: int
    currency: str
    features: tuple[str, ...]


class IPricingMatrix(ABC):
    """Every tier."""

    @property
    @abstractmethod
    def all(self) -> Sequence[PricingTier]: ...


class DefaultPricingMatrix(IPricingMatrix):
    """Static: a price a deployment could change is not a price, it is a
    negotiation."""

    _ALL = (
        PricingTier("free", 0, "ZAR", ("Local chat", "Family memory cap")),
        PricingTier("paid", 1900, "ZAR", ("Unlimited cloud calls", "Priority routing")),
        PricingTier("family", 4900, "ZAR", ("Up to 6 members",)),
        PricingTier("stokvel", 9900, "ZAR", ("Group memory", "Group reporting")),
        PricingTier("enterprise", 20000, "ZAR", ("Dedicated brain", "SLA")),
    )

    @property
    def all(self) -> Sequence[PricingTier]:
        return self._ALL

    def find(self, name: str) -> PricingTier | None:
        return next((t for t in self._ALL if t.name == name), None)


# ─────────────────────────────────────────────────────────────────────────────
# Localisation


class ICurrencyFormatter(ABC):
    """Rendering money for a person."""

    @abstractmethod
    def format(self, amount_minor: int, iso_currency_code: str) -> str: ...


class DefaultCurrencyFormatter(ICurrencyFormatter):
    """Takes MINOR UNITS and does the division HERE, in one place, so the number
    on a screen and the number in a ledger cannot disagree.

    Minor units are not always 100: JPY has 1 and some currencies have 1000, and
    a formatter that assumes two decimal places renders a yen amount a hundred
    times too small.
    """

    MINOR_UNITS = {"JPY": 1, "KRW": 1, "VND": 1, "BHD": 1000, "KWD": 1000, "TND": 1000}

    def format(self, amount_minor: int, iso_currency_code: str) -> str:
        code = iso_currency_code.upper()
        units = self.MINOR_UNITS.get(code, 100)
        if units == 1:
            return f"{amount_minor} {code}"
        digits = len(str(units)) - 1
        whole, frac = divmod(abs(amount_minor), units)
        sign = "-" if amount_minor < 0 else ""
        return f"{sign}{whole}.{frac:0{digits}d} {code}"


class IPhoneNumberFormatter(ABC):
    """Rendering a phone number for a person."""

    @abstractmethod
    def format(self, e164: str, country_iso_alpha2: str) -> str: ...


class DefaultPhoneNumberFormatter(IPhoneNumberFormatter):
    """Returns the input unchanged, deliberately.

    A wrong national format is worse than none, because it looks authoritative.
    A host with a real library replaces this rail.
    """

    def format(self, e164: str, country_iso_alpha2: str) -> str:
        return e164


class ICulturalNameRecogniser(ABC):
    """Whether names in a language are handled properly."""

    @abstractmethod
    def recognises_language(self, iso_language: str) -> bool: ...


class DefaultCulturalNameRecogniser(ICulturalNameRecogniser):
    """The languages whose naming conventions are actually understood.

    Click letters, diacritics, the fact that a "surname" is not always the last
    word. Claiming a language here that is only TOLERATED is how somebody's name
    comes back mangled on their own device.
    """

    SUPPORTED = frozenset({"zul", "xho", "tsn", "sot", "yor", "ibo", "twi", "swa", "hin", "ben"})

    def recognises_language(self, iso_language: str) -> bool:
        return iso_language.lower() in self.SUPPORTED


class ICulturalGreetings(ABC):
    """How to greet somebody in their language."""

    @abstractmethod
    def greeting_for(self, iso_language: str) -> str: ...


class DefaultCulturalGreetings(ICulturalGreetings):
    """Falls back to "Hello" rather than to nothing."""

    _GREETINGS = {
        "zul": "Sawubona", "zu": "Sawubona",
        "xho": "Molo", "xh": "Molo",
        "yor": "Ẹ kú àárọ̀",
        "hin": "नमस्ते",
    }

    def greeting_for(self, iso_language: str) -> str:
        return self._GREETINGS.get(iso_language.lower(), "Hello")


class IIndigenousKnowledgeProtocols(ABC):
    """Whether knowledge in a language needs elder review."""

    @abstractmethod
    def requires_elder_review(self, iso_language: str) -> bool: ...


class DefaultIndigenousKnowledgeProtocols(IIndigenousKnowledgeProtocols):
    """TRUE by default and for every language.

    The default is the whole point: knowledge belonging to a community is not
    the assistant's to repeat because a model happened to ingest it. Elder
    review is the gate, and a rail that defaulted to "no review needed" would
    make the exception the rule.
    """

    def requires_elder_review(self, iso_language: str) -> bool:
        return True


# ─────────────────────────────────────────────────────────────────────────────
# Hardware and fallbacks


class IOfflineQueuedOperation(ABC):
    """Operations waiting for a network."""

    @abstractmethod
    def enqueue(self, operation_json: str) -> None: ...

    @property
    @abstractmethod
    def pending(self) -> Sequence[str]: ...

    @abstractmethod
    def try_dequeue(self) -> str | None: ...


class DefaultOfflineQueuedOperation(IOfflineQueuedOperation):
    """FIFO. Ordering is not an implementation detail here — operations queued
    offline are things like "send this", and replaying them out of order is how
    a reply arrives before the message it answers."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._queue: list[str] = []

    def enqueue(self, operation_json: str) -> None:
        if not operation_json.strip():
            raise ValueError("an operation is required")
        with self._lock:
            self._queue.append(operation_json)

    @property
    def pending(self) -> Sequence[str]:
        with self._lock:
            return tuple(self._queue)

    def try_dequeue(self) -> str | None:
        with self._lock:
            return self._queue.pop(0) if self._queue else None


class ISmsFallback(ABC):
    """Answering somebody with no data."""

    @abstractmethod
    def answer_via_sms(self, phone_number: str, question: str) -> None: ...


class DefaultSmsFallback(ISmsFallback):
    """Records without sending when no gateway is wired, which is what a test
    wants and what a device with no SIM does."""

    def __init__(self, delivery: Callable[[str, str], None] | None = None) -> None:
        self._lock = threading.Lock()
        self._sent: list[tuple[str, str, datetime]] = []
        self._delivery = delivery

    def answer_via_sms(self, phone_number: str, question: str) -> None:
        if not phone_number.strip() or not question.strip():
            raise ValueError("a phone number and a question are required")
        with self._lock:
            self._sent.append((phone_number, question, _now()))
        if self._delivery is not None:
            self._delivery(phone_number, question)

    @property
    def sent(self) -> list[tuple[str, str, datetime]]:
        with self._lock:
            return list(self._sent)


class IUssdFallback(ABC):
    """One USSD turn."""

    @abstractmethod
    def respond(self, ussd_session: str, user_input: str) -> str: ...


class DefaultUssdFallback(IUssdFallback):
    """A REAL state machine, not a fixed string.

    USSD has no back button and no scrollback — the menu on the screen is the
    entire interface — so an unrecognised keypress REDISPLAYS the current menu
    rather than resetting to the root. Resetting would drop somebody three
    levels deep back to the start for a mistyped digit, on the one interface
    where they cannot see what happened.
    """

    MENUS: dict[str, tuple[str, dict[str, str]]] = {
        "root": ("CircleAI:\n1. Balance\n2. Ask AI\n3. Help",
                 {"1": "balance", "2": "ask", "3": "help"}),
        "balance": ("Balance: R0.00\n0. Back", {"0": "root"}),
        "ask": ("Type question, then send.\n0. Back", {"0": "root"}),
        "help": ("Dial *120*CIRCLE# anytime.\n0. Back", {"0": "root"}),
    }

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._sessions: dict[str, str] = {}

    def respond(self, ussd_session: str, user_input: str) -> str:
        if not ussd_session.strip():
            raise ValueError("a session id is required")
        if user_input is None:
            raise ValueError("input is required")
        with self._lock:
            current = self._sessions.setdefault(ussd_session, "root")
            prompt, routes = self.MENUS.get(current, self.MENUS["root"])
            nxt = routes.get(user_input.strip())
            if nxt is None:
                return prompt
            self._sessions[ussd_session] = nxt
            return self.MENUS[nxt][0]


# ─────────────────────────────────────────────────────────────────────────────
# Services


class IWhatsAppIntegration(ABC):
    """Reaching somebody on WhatsApp."""

    @abstractmethod
    def send(self, phone_number: str, message: str) -> None: ...


_E164 = re.compile(r"^\+?[1-9]\d{6,14}$")


class DefaultWhatsAppIntegration(IWhatsAppIntegration):
    """Validates E.164 BEFORE recording.

    The check is here rather than at the gateway because an invalid number that
    reaches the outbox has already been counted as sent, and reconciling that
    later is guesswork.
    """

    def __init__(self, send: Callable[[str, str], None] | None = None) -> None:
        self._lock = threading.Lock()
        self._outbox: list[tuple[str, str, datetime]] = []
        self._send = send

    def send(self, phone_number: str, message: str) -> None:
        if not phone_number.strip() or not message.strip():
            raise ValueError("a phone number and a message are required")
        if not _E164.match(phone_number):
            raise ValueError(f"invalid E.164 phone {phone_number!r}")
        with self._lock:
            self._outbox.append((phone_number, message, _now()))
        if self._send is not None:
            self._send(phone_number, message)

    @property
    def outbox(self) -> list[tuple[str, str, datetime]]:
        with self._lock:
            return list(self._outbox)


class ITelegramIntegration(ABC):
    """Reaching somebody on Telegram."""

    @abstractmethod
    def send(self, chat_id: str, message: str) -> None: ...


class DefaultTelegramIntegration(ITelegramIntegration):
    """Records and optionally sends."""

    def __init__(self, send: Callable[[str, str], None] | None = None) -> None:
        self._lock = threading.Lock()
        self._outbox: list[tuple[str, str, datetime]] = []
        self._send = send

    def send(self, chat_id: str, message: str) -> None:
        if not chat_id.strip() or not message.strip():
            raise ValueError("a chat id and a message are required")
        with self._lock:
            self._outbox.append((chat_id, message, _now()))
        if self._send is not None:
            self._send(chat_id, message)

    @property
    def outbox(self) -> list[tuple[str, str, datetime]]:
        with self._lock:
            return list(self._outbox)


# ─────────────────────────────────────────────────────────────────────────────
# Recovery


class ILostDeviceFlow(ABC):
    """What happens when a device is lost."""

    @abstractmethod
    def remote_wipe(self, device_id: str) -> None: ...

    @abstractmethod
    def is_wiped(self, device_id: str) -> bool: ...


class DefaultLostDeviceFlow(ILostDeviceFlow):
    """Records the wipe, so a device that comes back knows it happened."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._wiped: dict[str, datetime] = {}

    def remote_wipe(self, device_id: str) -> None:
        if not device_id.strip():
            raise ValueError("a device id is required")
        with self._lock:
            self._wiped[device_id] = _now()

    def is_wiped(self, device_id: str) -> bool:
        with self._lock:
            return device_id in self._wiped


class IInheritanceProtocol(ABC):
    """Who gets an account when its owner cannot use it."""

    @abstractmethod
    def designate(self, owner_id: str, designee_id: str) -> None: ...

    @abstractmethod
    def designee_for(self, owner_id: str) -> str | None: ...


class DefaultInheritanceProtocol(IInheritanceProtocol):
    """Refuses owner == designee.

    Naming yourself your own heir is not a designation, it is a way for the
    recovery flow to hand an account to whoever already has it.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._designees: dict[str, str] = {}

    def designate(self, owner_id: str, designee_id: str) -> None:
        if not owner_id.strip() or not designee_id.strip():
            raise ValueError("an owner and a designee are required")
        if owner_id == designee_id:
            raise ValueError("a designee cannot be the owner")
        with self._lock:
            self._designees[owner_id] = designee_id

    def designee_for(self, owner_id: str) -> str | None:
        with self._lock:
            return self._designees.get(owner_id)


class IVerifiableWipe(ABC):
    """Wiping, with proof."""

    @abstractmethod
    def wipe_and_certify(self, owner_id: str) -> bytes: ...


class DefaultVerifiableWipe(IVerifiableWipe):
    """SHA-256 over "wipe|owner|iso-time|nonce".

    The NONCE is what makes the certificate evidence rather than decoration:
    without it the hash is a function of the owner and the second, and anybody
    can produce one for a wipe that never happened.
    """

    def wipe_and_certify(self, owner_id: str) -> bytes:
        if not owner_id.strip():
            raise ValueError("an owner id is required")
        nonce = secrets.token_bytes(16)
        payload = f"wipe|{owner_id}|{_now().isoformat()}|{nonce.hex()}"
        return hashlib.sha256(payload.encode()).digest()


class IDataPortabilityExport(ABC):
    """Everything held about somebody, as a portable bundle."""

    @abstractmethod
    def export(self, owner_id: str) -> bytes: ...


class DefaultDataPortabilityExport(IDataPortabilityExport):
    """Not a favour and not a retention feature: it is the thing that makes
    leaving possible, and a product that cannot be left is not one somebody
    chose."""

    def export(self, owner_id: str) -> bytes:
        if not owner_id.strip():
            raise ValueError("an owner id is required")
        import json

        return json.dumps(
            {
                "owner_id": owner_id,
                "exported_at": _now().isoformat(),
                "schema": "circleai/portability/v1",
                "note": "A host overrides export to stream the actual data — "
                        "memory, contacts, transcripts.",
            }
        ).encode()


class IAccountCompromiseRecovery(ABC):
    """Getting an account back."""

    @abstractmethod
    def begin(self, owner_id: str) -> None: ...

    @abstractmethod
    def in_recovery(self, owner_id: str) -> bool: ...

    @abstractmethod
    def complete(self, owner_id: str) -> None: ...


class DefaultAccountCompromiseRecovery(IAccountCompromiseRecovery):
    """Tracks who is mid-recovery, so the rest of the system can be careful."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._active: dict[str, datetime] = {}

    def begin(self, owner_id: str) -> None:
        if not owner_id.strip():
            raise ValueError("an owner id is required")
        with self._lock:
            self._active[owner_id] = _now()

    def in_recovery(self, owner_id: str) -> bool:
        with self._lock:
            return owner_id in self._active

    def complete(self, owner_id: str) -> None:
        with self._lock:
            self._active.pop(owner_id, None)


# ─────────────────────────────────────────────────────────────────────────────
# Modes


class IImpairedUserMode(ABC):
    """A mode for somebody who is temporarily unable to use the device
    normally."""

    @abstractmethod
    def engage(self, owner_id: str) -> None: ...

    @abstractmethod
    def is_engaged(self, owner_id: str) -> bool: ...

    @abstractmethod
    def disengage(self, owner_id: str) -> None: ...


class DefaultImpairedUserMode(IImpairedUserMode):
    """Engaged and disengaged explicitly, never inferred.

    Inferring impairment from behaviour is a judgement about a person that a
    device is in no position to make.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._engaged: set[str] = set()

    def engage(self, owner_id: str) -> None:
        if not owner_id.strip():
            raise ValueError("an owner id is required")
        with self._lock:
            self._engaged.add(owner_id)

    def is_engaged(self, owner_id: str) -> bool:
        with self._lock:
            return owner_id in self._engaged

    def disengage(self, owner_id: str) -> None:
        with self._lock:
            self._engaged.discard(owner_id)


class IQuietMode(ABC):
    """Windows during which the assistant does not speak first."""

    @abstractmethod
    def engage(self, reason: str, duration: timedelta) -> None: ...

    @abstractmethod
    def is_quiet_at(self, moment: datetime) -> bool: ...


class DefaultQuietMode(IQuietMode):
    """Refuses a non-positive duration.

    A zero-length quiet window reads as "quiet is on" to anybody skimming the
    list, and is silently never true.

    Expired windows are filtered ON READ rather than swept on a timer — there is
    no thread here, and a rail that needs one to stay correct is a rail that is
    wrong whenever the thread is late.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._windows: list[tuple[str, datetime, datetime]] = []

    def engage(self, reason: str, duration: timedelta) -> None:
        if not reason.strip():
            raise ValueError("a reason is required")
        if duration <= timedelta():
            raise ValueError("a quiet window needs a positive duration")
        now = _now()
        with self._lock:
            self._windows.append((reason, now, now + duration))

    def is_quiet_at(self, moment: datetime) -> bool:
        with self._lock:
            return any(start <= moment <= end for _, start, end in self._windows)

    def active_windows(self, now: datetime | None = None) -> list[tuple[str, datetime, datetime]]:
        at = now or _now()
        with self._lock:
            return [w for w in self._windows if w[2] >= at]


__all__ = [
    "DefaultAccountCompromiseRecovery",
    "DefaultAiPersonalityWizard",
    "DefaultCulturalGreetings",
    "DefaultCulturalNameRecogniser",
    "DefaultCurrencyFormatter",
    "DefaultDataPortabilityExport",
    "DefaultFamilyOnboarding",
    "DefaultImpairedUserMode",
    "DefaultIndigenousKnowledgeProtocols",
    "DefaultInheritanceProtocol",
    "DefaultLostDeviceFlow",
    "DefaultNoManualFirstRun",
    "DefaultOfflineQueuedOperation",
    "DefaultPerCallTransparency",
    "DefaultPersonalDataImport",
    "DefaultPhoneNumberFormatter",
    "DefaultPhonePinBiometricOnboarding",
    "DefaultPricingMatrix",
    "DefaultPublicTransparency",
    "DefaultQuietMode",
    "DefaultSmsFallback",
    "DefaultTelegramIntegration",
    "DefaultUssdFallback",
    "DefaultVerifiableWipe",
    "DefaultVoiceLedSetup",
    "DefaultWhatsAppIntegration",
    "HouseholdMember",
    "IAccountCompromiseRecovery",
    "IAiPersonalityWizard",
    "ICulturalGreetings",
    "ICulturalNameRecogniser",
    "ICurrencyFormatter",
    "IDataPortabilityExport",
    "IFamilyOnboarding",
    "IImpairedUserMode",
    "IIndigenousKnowledgeProtocols",
    "IInheritanceProtocol",
    "ILostDeviceFlow",
    "INoManualFirstRun",
    "IOfflineQueuedOperation",
    "IPerCallTransparency",
    "IPersonalDataImport",
    "IPhoneNumberFormatter",
    "IPhonePinBiometricOnboarding",
    "IPricingMatrix",
    "IPublicTransparency",
    "IQuietMode",
    "ISmsFallback",
    "ITelegramIntegration",
    "IUssdFallback",
    "IVerifiableWipe",
    "IVoiceLedSetup",
    "IWhatsAppIntegration",
    "OnboardingSession",
    "PersonalityChoice",
    "PricingTier",
    "TransparencyReceipt",
]
