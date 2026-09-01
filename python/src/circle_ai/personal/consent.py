"""Personal data, plugins, benchmarks, and the test scaffolding.

THE CONSENT GUARD IS THE SERIOUS PART OF THIS FILE. Everything else here is
plumbing; this is the thing standing between an assistant and somebody's email.

Four properties, and each exists because the version without it is the one that
gets built by default:

  * SCOPED. A grant to read the calendar is not a grant to read contacts. A
    single "personal data" permission is how an assistant that was allowed to
    check a meeting time ends up reading a mailbox.

  * EXPIRING. A grant with no end is a grant forever, and nobody revisits it.
    Every token here has an expiry and there is no way to build one without.

  * REVOCABLE, and revocation beats everything - an unexpired, in-scope,
    correctly-signed token that has been revoked is refused.

  * FAIL CLOSED. Every path that cannot answer answers NO. An adapter with no
    token, a token for the wrong scope, an expired one, a clock that cannot be
    read: all denied, none of them logged as an error, because a refusal is the
    system working.
"""

from __future__ import annotations

import difflib
import re
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import Enum
from typing import Callable, Sequence


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# Consent


class ConsentScope(Enum):
    """One thing a person may agree to.

    SEPARATE VALUES, deliberately fine-grained, and reading is separate from
    writing everywhere. An assistant that can read a calendar to answer "when am
    I free" does not thereby get to send invitations.
    """

    CALENDAR_READ = "calendar:read"
    CALENDAR_WRITE = "calendar:write"
    CONTACTS_READ = "contacts:read"
    CONTACTS_WRITE = "contacts:write"
    EMAIL_READ = "email:read"
    #: Sending is its own scope and is never bundled. Sending mail as somebody
    #: is the single most consequential thing in this file.
    EMAIL_SEND = "email:send"
    LOCATION_READ = "location:read"
    PHOTOS_READ = "photos:read"

    @property
    def is_write(self) -> bool:
        return self.value.endswith((":write", ":send"))


@dataclass(frozen=True)
class UserConsentToken:
    """Proof that somebody agreed to something, for a while.

    NO OPEN-ENDED GRANT IS CONSTRUCTIBLE. `expires_at` has no default and a
    token whose expiry is not after its issue time is refused at construction -
    which makes "forever" something a caller has to write out in whole years
    rather than something they get by leaving a field alone.
    """

    scopes: frozenset[ConsentScope]
    expires_at: datetime
    granted_at: datetime = field(default_factory=_utc_now)
    #: Who agreed. Blank is refused: a grant nobody can be shown to have made is
    #: not a grant.
    granted_by: str = "the person using this device"
    #: What it is for, in their words. Shown when they review what they have
    #: allowed, which is the only thing that makes a review meaningful.
    purpose: str = ""

    def __post_init__(self) -> None:
        if not self.scopes:
            raise ValueError("a consent token must name at least one scope")
        if not str(self.granted_by).strip():
            raise ValueError("a consent token must record who granted it")
        if self.expires_at <= self.granted_at:
            raise ValueError("a consent token must expire after it was granted")

    def is_valid_at(self, when: datetime) -> bool:
        return self.granted_at <= when < self.expires_at

    def covers(self, scope: ConsentScope) -> bool:
        return scope in self.scopes

    def remaining(self, when: datetime) -> timedelta:
        return max(timedelta(), self.expires_at - when)

    def describe(self) -> str:
        names = ", ".join(sorted(s.value for s in self.scopes))
        purpose = f" to {self.purpose}" if self.purpose else ""
        return f"{names}{purpose}, until {self.expires_at.isoformat()}"

    @staticmethod
    def for_scopes(
        scopes: Sequence[ConsentScope], minutes: int = 15,
        purpose: str = "", now: datetime | None = None,
    ) -> "UserConsentToken":
        """FIFTEEN MINUTES by default.

        Short because the common case is one task - "what is on today" - and a
        grant that outlives the task is a grant that is still open next week.
        """
        at = now or _utc_now()
        return UserConsentToken(
            frozenset(scopes), at + timedelta(minutes=max(1, minutes)), at,
            purpose=purpose)


@dataclass(frozen=True)
class ConsentDecision:
    """Whether an operation may proceed, and why not."""

    allowed: bool
    reason: str
    scope: ConsentScope | None = None


class ConsentGuard:
    """Holds tokens and answers whether a scope is permitted right now.

    FAILS CLOSED EVERYWHERE. No token, wrong scope, expired, revoked, or a clock
    that will not answer - all no. The one thing it must never do is allow
    something because it could not work out whether to refuse.
    """

    def __init__(self, now: Callable[[], datetime] | None = None) -> None:
        self._now = now or _utc_now
        self._lock = threading.Lock()
        self._tokens: list[UserConsentToken] = []
        self._revoked: set[ConsentScope] = set()

    def grant(self, token: UserConsentToken) -> None:
        with self._lock:
            self._tokens.append(token)
            # Granting CLEARS a previous revocation for those scopes. A person
            # who revokes and then agrees again means the second thing.
            self._revoked -= token.scopes

    def revoke(self, scope: ConsentScope) -> None:
        """Revocation is by SCOPE, not by token.

        Revoking a token would leave any other token carrying the same scope
        working, and a person who says "stop reading my email" means all of it.
        """
        with self._lock:
            self._revoked.add(scope)

    def revoke_all(self) -> None:
        with self._lock:
            self._revoked.update(ConsentScope)
            self._tokens.clear()

    def check(self, scope: ConsentScope) -> ConsentDecision:
        try:
            now = self._now()
        except Exception:
            # A clock that will not answer means no. Assuming a time here would
            # let a broken clock become an open door.
            return ConsentDecision(False, "this device cannot tell the time, so "
                                          "it will not act on your data", scope)
        with self._lock:
            if scope in self._revoked:
                # Checked FIRST. Revocation beats a token that is otherwise
                # perfectly valid.
                return ConsentDecision(
                    False, f"you turned off {scope.value}", scope)
            live = [t for t in self._tokens if t.covers(scope) and t.is_valid_at(now)]
        if not live:
            expired = any(t.covers(scope) for t in self._tokens)
            return ConsentDecision(
                False,
                f"the permission for {scope.value} has run out - ask again"
                if expired else
                f"this needs your permission for {scope.value}",
                scope)
        return ConsentDecision(True, f"allowed until "
                                     f"{max(t.expires_at for t in live).isoformat()}",
                               scope)

    def require(self, scope: ConsentScope) -> None:
        decision = self.check(scope)
        if not decision.allowed:
            raise PermissionError(decision.reason)

    def active_scopes(self) -> tuple[ConsentScope, ...]:
        """What is allowed right now - for the screen where somebody reviews it.

        A permission list nobody can see is a permission list nobody withdraws.
        """
        now = self._now()
        with self._lock:
            live = {
                s for t in self._tokens if t.is_valid_at(now)
                for s in t.scopes if s not in self._revoked
            }
        return tuple(sorted(live, key=lambda s: s.value))


# ─────────────────────────────────────────────────────────────────────────────
# Personal adapters


@dataclass(frozen=True)
class CalendarEvent:
    """One event."""

    title: str = ""
    starts_at: datetime | None = None
    ends_at: datetime | None = None
    location: str = ""
    attendees: tuple[str, ...] = ()


@dataclass(frozen=True)
class Contact:
    """One person."""

    display_name: str = ""
    emails: tuple[str, ...] = ()
    phones: tuple[str, ...] = ()


@dataclass(frozen=True)
class EmailMessage:
    """One message."""

    subject: str = ""
    sender: str = ""
    recipients: tuple[str, ...] = ()
    body: str = ""
    received_at: datetime | None = None


class ICalendarAdapter(ABC):
    """Reaches the device's calendar."""

    @abstractmethod
    def events_between(self, start: datetime, end: datetime) -> Sequence[CalendarEvent]: ...

    @property
    @abstractmethod
    def is_available(self) -> bool: ...


class IContactsAdapter(ABC):
    """Reaches the device's contacts."""

    @abstractmethod
    def search(self, query: str) -> Sequence[Contact]: ...

    @property
    @abstractmethod
    def is_available(self) -> bool: ...


class IEmailAdapter(ABC):
    """Reaches the device's mail."""

    @abstractmethod
    def recent(self, count: int = 20) -> Sequence[EmailMessage]: ...

    @abstractmethod
    def send(self, message: EmailMessage) -> bool: ...

    @property
    @abstractmethod
    def is_available(self) -> bool: ...


class NullCalendarAdapter(ICalendarAdapter):
    """Reads nothing.

    The DEFAULT, so a build with no calendar binding reads no calendar - rather
    than a build that happens to have one reading it without anybody wiring
    consent.
    """

    def events_between(self, start: datetime, end: datetime) -> Sequence[CalendarEvent]:
        return ()

    @property
    def is_available(self) -> bool:
        return False


class NullContactsAdapter(IContactsAdapter):
    """Finds nobody."""

    def search(self, query: str) -> Sequence[Contact]:
        return ()

    @property
    def is_available(self) -> bool:
        return False


class NullEmailAdapter(IEmailAdapter):
    """Reads nothing and sends nothing.

    `send` returns False rather than raising, and returning True would be the
    worst possible default: the assistant would tell somebody their message went
    when it did not.
    """

    def recent(self, count: int = 20) -> Sequence[EmailMessage]:
        return ()

    def send(self, message: EmailMessage) -> bool:
        return False

    @property
    def is_available(self) -> bool:
        return False


@dataclass(frozen=True)
class PersonalDomainContext:
    """What the companion may reach on this device."""

    has_calendar: bool = False
    has_contacts: bool = False
    has_email: bool = False

    def describe(self, guard: ConsentGuard | None = None) -> str:
        """Says what is CONNECTED and, separately, what is ALLOWED.

        The two are different and conflating them is how a person is told the
        assistant can read their mail when it merely could if they said so.
        """
        connected = [
            name for name, present in (
                ("calendar", self.has_calendar), ("contacts", self.has_contacts),
                ("email", self.has_email))
            if present
        ]
        if not connected:
            return "nothing personal is connected to this device"
        text = "connected: " + ", ".join(connected)
        if guard is not None:
            active = guard.active_scopes()
            text += (
                "; allowed right now: " + ", ".join(s.value for s in active)
                if active else "; nothing is allowed right now")
        return text


class PersonalCompanionAdapter:
    """The companion's way in - and every call passes the guard first.

    THE GUARD IS CHECKED BEFORE THE ADAPTER IS TOUCHED, not after. Reading the
    data and then deciding whether it was allowed has already read the data, and
    on a platform that logs access, already recorded it.
    """

    def __init__(
        self,
        guard: ConsentGuard | None = None,
        calendar: ICalendarAdapter | None = None,
        contacts: IContactsAdapter | None = None,
        email: IEmailAdapter | None = None,
    ) -> None:
        self._guard = guard or ConsentGuard()
        self._calendar = calendar or NullCalendarAdapter()
        self._contacts = contacts or NullContactsAdapter()
        self._email = email or NullEmailAdapter()

    @property
    def context(self) -> PersonalDomainContext:
        return PersonalDomainContext(
            self._calendar.is_available, self._contacts.is_available,
            self._email.is_available)

    def events_between(
        self, start: datetime, end: datetime
    ) -> tuple[Sequence[CalendarEvent], str]:
        decision = self._guard.check(ConsentScope.CALENDAR_READ)
        if not decision.allowed:
            return (), decision.reason
        return self._calendar.events_between(start, end), ""

    def find_contact(self, query: str) -> tuple[Sequence[Contact], str]:
        decision = self._guard.check(ConsentScope.CONTACTS_READ)
        if not decision.allowed:
            return (), decision.reason
        return self._contacts.search(query), ""

    def recent_email(self, count: int = 20) -> tuple[Sequence[EmailMessage], str]:
        decision = self._guard.check(ConsentScope.EMAIL_READ)
        if not decision.allowed:
            return (), decision.reason
        return self._email.recent(count), ""

    def send_email(self, message: EmailMessage) -> tuple[bool, str]:
        """Requires EMAIL_SEND, which reading never grants.

        The two scopes are checked separately and a token that carries only
        EMAIL_READ cannot send - which is the whole reason they are separate
        values.
        """
        decision = self._guard.check(ConsentScope.EMAIL_SEND)
        if not decision.allowed:
            return False, decision.reason
        if not self._email.is_available:
            return False, "no mail account is connected to this device"
        return self._email.send(message), ""


# ─────────────────────────────────────────────────────────────────────────────
# Plugins


@dataclass(frozen=True)
class Permissions:
    """What a plugin is allowed to do.

    EVERYTHING OFF. A plugin is code from somebody else running inside the
    assistant, and a permission it was not given is a permission it does not
    have - not one it has until somebody notices.
    """

    read_files: bool = False
    write_files: bool = False
    network: bool = False
    #: Reaching the model. Off by default, because a plugin with model access
    #: can spend the device's battery and, through the model, its context.
    inference: bool = False
    #: Anything under `ConsentScope`. Held as scopes rather than a flag so a
    #: plugin cannot be given "personal data" wholesale.
    consent_scopes: frozenset[ConsentScope] = frozenset()
    #: Directories it may touch, if `read_files` or `write_files` is on. An
    #: empty tuple with file access on means its own workspace only.
    paths: tuple[str, ...] = ()

    @staticmethod
    def none() -> "Permissions":
        return Permissions()

    def allows(self, capability: str) -> bool:
        return bool(getattr(self, capability, False))

    def describe(self) -> str:
        """What a person is shown before installing. Written as capabilities in
        plain words, because "network: true" is not a decision anybody can
        make."""
        wants: list[str] = []
        if self.network:
            wants.append("use the internet")
        if self.read_files:
            wants.append("read files" + (f" in {', '.join(self.paths)}" if self.paths else " in its own folder"))
        if self.write_files:
            wants.append("change files" + (f" in {', '.join(self.paths)}" if self.paths else " in its own folder"))
        if self.inference:
            wants.append("use the assistant's model")
        wants += [f"reach your {s.value.split(':')[0]}" for s in sorted(self.consent_scopes, key=lambda s: s.value)]
        return "this wants to " + "; ".join(wants) if wants else "this asks for nothing"


class IPluginsRootResolver(ABC):
    """Says where plugins live."""

    @abstractmethod
    def plugins_root(self) -> str: ...


class IWorkspacePathProvider(ABC):
    """Says where one plugin's own files live."""

    @abstractmethod
    def workspace_for(self, plugin_id: str) -> str: ...


@dataclass(frozen=True)
class PluginLoadResult:
    """What loading one plugin did."""

    plugin_id: str = ""
    loaded: bool = False
    version: str = ""
    granted: Permissions = field(default_factory=Permissions)
    #: What it ASKED for. Kept beside what it got, so a review screen can show
    #: the difference - a plugin asking for far more than it was given is worth
    #: seeing.
    requested: Permissions = field(default_factory=Permissions)
    error: str = ""

    @property
    def was_narrowed(self) -> bool:
        return self.loaded and self.granted != self.requested


class PluginLoader:
    """Loads a plugin with no more than it was granted.

    THE INTERSECTION, ALWAYS. A plugin gets what it asked for AND what the
    person allowed - never the union, and never what it asked for on the grounds
    that it asked. That single rule is the difference between a permission
    system and a manifest.
    """

    def __init__(
        self,
        roots: IPluginsRootResolver | None = None,
        workspaces: IWorkspacePathProvider | None = None,
        read_manifest: Callable[[str], dict[str, object]] | None = None,
    ) -> None:
        self._roots = roots
        self._workspaces = workspaces
        self._read_manifest = read_manifest

    @staticmethod
    def intersect(requested: Permissions, allowed: Permissions) -> Permissions:
        return Permissions(
            read_files=requested.read_files and allowed.read_files,
            write_files=requested.write_files and allowed.write_files,
            network=requested.network and allowed.network,
            inference=requested.inference and allowed.inference,
            consent_scopes=requested.consent_scopes & allowed.consent_scopes,
            # Paths intersect too. A plugin granted one directory and asking for
            # two gets one.
            paths=tuple(p for p in requested.paths if p in allowed.paths),
        )

    def load(self, plugin_id: str, allowed: Permissions) -> PluginLoadResult:
        if not plugin_id.strip():
            return PluginLoadResult(error="a plugin needs an identifier")
        if self._read_manifest is None:
            return PluginLoadResult(plugin_id, error="no way to read a manifest")
        try:
            manifest = self._read_manifest(plugin_id)
        except Exception as exc:  # noqa: BLE001
            return PluginLoadResult(plugin_id, error=str(exc))

        raw = manifest.get("permissions") or {}
        scopes = set()
        for name in raw.get("consent_scopes", ()) if isinstance(raw, dict) else ():
            try:
                scopes.add(ConsentScope(str(name)))
            except ValueError:
                # An unknown scope is DROPPED, not an error. A plugin built
                # against a newer build asking for something this one has never
                # heard of gets less, not a failure - and it certainly does not
                # get it.
                continue
        requested = Permissions(
            read_files=bool(raw.get("read_files")),
            write_files=bool(raw.get("write_files")),
            network=bool(raw.get("network")),
            inference=bool(raw.get("inference")),
            consent_scopes=frozenset(scopes),
            paths=tuple(str(p) for p in raw.get("paths", ())),
        )
        return PluginLoadResult(
            plugin_id, True, str(manifest.get("version", "")),
            self.intersect(requested, allowed), requested)


class PluginLifecycleService:
    """Starts and stops plugins.

    STOPPING IS THE HARD PART. A plugin that will not stop is a plugin still
    holding a permission, so this drops the grant FIRST and then asks it to
    stop - if it ignores the request it is at least no longer allowed to do
    anything.
    """

    def __init__(self, loader: PluginLoader | None = None) -> None:
        self._loader = loader or PluginLoader()
        self._lock = threading.Lock()
        self._running: dict[str, PluginLoadResult] = {}

    def start(self, plugin_id: str, allowed: Permissions) -> PluginLoadResult:
        result = self._loader.load(plugin_id, allowed)
        if result.loaded:
            with self._lock:
                self._running[plugin_id] = result
        return result

    def stop(self, plugin_id: str) -> bool:
        with self._lock:
            return self._running.pop(plugin_id, None) is not None

    def stop_all(self) -> int:
        with self._lock:
            count = len(self._running)
            self._running.clear()
            return count

    def running(self) -> tuple[str, ...]:
        with self._lock:
            return tuple(sorted(self._running))

    def permissions_of(self, plugin_id: str) -> Permissions:
        """A plugin that is not running has NO permissions, not its last ones."""
        with self._lock:
            result = self._running.get(plugin_id)
        return result.granted if result else Permissions.none()


class PluginsServiceCollectionExtensions:
    """Wires the plugin service."""

    @staticmethod
    def add_plugins(
        roots: IPluginsRootResolver | None = None,
        workspaces: IWorkspacePathProvider | None = None,
        read_manifest: Callable[[str], dict[str, object]] | None = None,
    ) -> PluginLifecycleService:
        return PluginLifecycleService(
            PluginLoader(roots, workspaces, read_manifest))


# ─────────────────────────────────────────────────────────────────────────────
# Benchmarks


@dataclass(frozen=True)
class BenchTask:
    """One thing to ask, and what a right answer looks like."""

    task_id: str
    prompt: str
    expected: str = ""
    #: Several acceptable answers. Most real questions have more than one right
    #: reply, and a benchmark that admits only the first measures phrasing.
    also_accept: tuple[str, ...] = ()
    tags: tuple[str, ...] = ()
    tolerance: float = 0.0


@dataclass(frozen=True)
class BenchScoring:
    """How a task is marked."""

    scorer: str = "exact"
    #: Relative tolerance for numeric answers. RELATIVE rather than absolute
    #: because 1% of a distance and 1% of a price are different numbers and the
    #: same judgement.
    relative_tolerance: float = 0.01
    absolute_tolerance: float = 0.0
    case_sensitive: bool = False


@dataclass(frozen=True)
class BenchResult:
    """How one task went."""

    task_id: str
    passed: bool = False
    score: float = 0.0
    actual: str = ""
    expected: str = ""
    duration_ms: int = 0
    #: Populated when the run failed rather than the answer being wrong. The two
    #: must not be averaged together - a crashed run is not a zero score, it is
    #: an absent one.
    error: str = ""

    @property
    def counted(self) -> bool:
        return not self.error


class IBenchScorer(ABC):
    """Marks one answer."""

    @property
    @abstractmethod
    def name(self) -> str: ...

    @abstractmethod
    def score(self, actual: str, task: BenchTask, scoring: BenchScoring) -> float: ...


def _normalise(text: str, case_sensitive: bool) -> str:
    """Trims and collapses whitespace, and folds case unless told not to.

    A model that answers with a trailing newline is not wrong, and a scorer that
    counts it as wrong measures formatting.
    """
    collapsed = " ".join(text.split())
    return collapsed if case_sensitive else collapsed.lower()


class ExactMatchScorer(IBenchScorer):
    """One or zero."""

    @property
    def name(self) -> str:
        return "exact"

    def score(self, actual: str, task: BenchTask, scoring: BenchScoring) -> float:
        got = _normalise(actual, scoring.case_sensitive)
        wanted = [task.expected, *task.also_accept]
        return 1.0 if any(got == _normalise(w, scoring.case_sensitive) for w in wanted) else 0.0


class SubstringScorer(IBenchScorer):
    """Whether the answer is IN there.

    For questions where the model reasonably says more than the answer. It is
    also the easiest scorer to fool, which is why it is chosen per task rather
    than used as a default.
    """

    @property
    def name(self) -> str:
        return "substring"

    def score(self, actual: str, task: BenchTask, scoring: BenchScoring) -> float:
        got = _normalise(actual, scoring.case_sensitive)
        wanted = [task.expected, *task.also_accept]
        return 1.0 if any(
            _normalise(w, scoring.case_sensitive) in got for w in wanted if w
        ) else 0.0


class RegexScorer(IBenchScorer):
    """Matches a pattern."""

    @property
    def name(self) -> str:
        return "regex"

    def score(self, actual: str, task: BenchTask, scoring: BenchScoring) -> float:
        flags = 0 if scoring.case_sensitive else re.IGNORECASE
        for pattern in (task.expected, *task.also_accept):
            if not pattern:
                continue
            try:
                if re.search(pattern, actual, flags):
                    return 1.0
            except re.error:
                # A bad pattern is a broken TASK, not a failed answer. Scoring
                # it zero would blame the model for the benchmark's mistake.
                continue
        return 0.0


class NumericToleranceScorer(IBenchScorer):
    """Marks a number as right if it is close enough.

    RELATIVE AND ABSOLUTE TOGETHER, and either passing is enough. Relative alone
    cannot handle an expected value of zero, where every answer is infinitely
    far away in relative terms; absolute alone needs a different threshold for
    every question.
    """

    #: Handles a leading currency symbol, thousands separators and a trailing
    #: unit, because a model asked for a price answers "R1 234,50" and that is a
    #: correct answer.
    _NUMBER = re.compile(r"[-+]?\d[\d\s,._]*(?:\.\d+)?|[-+]?\.\d+")

    @property
    def name(self) -> str:
        return "numeric"

    @classmethod
    def extract(cls, text: str) -> float | None:
        """The FIRST number, or None.

        None rather than zero: a reply with no number in it has not answered,
        and scoring it as zero makes it indistinguishable from an answer of
        zero.
        """
        match = cls._NUMBER.search(text or "")
        if not match:
            return None
        raw = match.group().replace(" ", "").replace("_", "")
        # A comma is a thousands separator here and a decimal point in half the
        # world. Decided by POSITION: a comma with exactly two digits after it
        # and no full stop present is a decimal comma.
        if "," in raw and "." not in raw and re.search(r",\d{1,2}$", raw):
            raw = raw.replace(",", ".")
        else:
            raw = raw.replace(",", "")
        try:
            return float(raw)
        except ValueError:
            return None

    def score(self, actual: str, task: BenchTask, scoring: BenchScoring) -> float:
        got = self.extract(actual)
        wanted = self.extract(task.expected)
        if got is None or wanted is None:
            return 0.0
        difference = abs(got - wanted)
        absolute = task.tolerance or scoring.absolute_tolerance
        if absolute > 0 and difference <= absolute:
            return 1.0
        if scoring.relative_tolerance > 0 and wanted != 0:
            return 1.0 if difference / abs(wanted) <= scoring.relative_tolerance else 0.0
        return 1.0 if difference == 0 else 0.0


class BuiltInScorers:
    """The scorers that ship."""

    @staticmethod
    def all() -> dict[str, IBenchScorer]:
        return {s.name: s for s in (
            ExactMatchScorer(), SubstringScorer(), RegexScorer(),
            NumericToleranceScorer())}

    @staticmethod
    def get(name: str) -> IBenchScorer:
        """Falls back to EXACT, the strictest.

        A misspelt scorer name must not silently become the most permissive one,
        which would quietly inflate every score in the suite.
        """
        return BuiltInScorers.all().get(name.lower(), ExactMatchScorer())


class BenchSuiteRegistry:
    """The suites this device knows about."""

    def __init__(self) -> None:
        self._suites: dict[str, tuple[BenchTask, ...]] = {}

    def register(self, name: str, tasks: Sequence[BenchTask]) -> None:
        if not name.strip():
            raise ValueError("a suite needs a name")
        ids = [t.task_id for t in tasks]
        if len(ids) != len(set(ids)):
            # Duplicate ids silently halve a suite when results are keyed by id,
            # and the total still looks plausible.
            raise ValueError(f"suite {name!r} has duplicate task ids")
        self._suites[name] = tuple(tasks)

    def get(self, name: str) -> tuple[BenchTask, ...]:
        return self._suites.get(name, ())

    def names(self) -> tuple[str, ...]:
        return tuple(sorted(self._suites))

    def tagged(self, tag: str) -> tuple[BenchTask, ...]:
        return tuple(
            t for tasks in self._suites.values() for t in tasks if tag in t.tags)


@dataclass(frozen=True)
class AbComparison:
    """A against B over one suite."""

    a_label: str = ""
    b_label: str = ""
    a_score: float = 0.0
    b_score: float = 0.0
    counted: int = 0
    a_errors: int = 0
    b_errors: int = 0

    @property
    def winner(self) -> str:
        """Ties are reported as ties.

        Rounding a tie to a winner is how a change with no effect gets shipped
        as an improvement.
        """
        if self.counted == 0:
            return "neither - nothing was scored"
        if abs(self.a_score - self.b_score) < 1e-9:
            return "tie"
        return self.a_label if self.a_score > self.b_score else self.b_label


class AbBenchRunner:
    """Runs two answerers over one suite and compares.

    BOTH SEE EXACTLY THE SAME TASKS IN THE SAME ORDER, and a task that errors on
    either side is EXCLUDED FROM BOTH. Averaging a crash on one side against a
    score on the other compares a model with a bug report.
    """

    def __init__(self, registry: BenchSuiteRegistry | None = None) -> None:
        self._registry = registry or BenchSuiteRegistry()

    def run_one(
        self, task: BenchTask, answer: Callable[[str], str],
        scoring: BenchScoring | None = None,
    ) -> BenchResult:
        rules = scoring or BenchScoring()
        try:
            actual = answer(task.prompt)
        except Exception as exc:  # noqa: BLE001
            return BenchResult(task.task_id, error=str(exc), expected=task.expected)
        score = BuiltInScorers.get(rules.scorer).score(actual, task, rules)
        return BenchResult(
            task.task_id, score >= 1.0, score, actual, task.expected)

    def compare(
        self, tasks: Sequence[BenchTask],
        a: Callable[[str], str], b: Callable[[str], str],
        scoring: BenchScoring | None = None,
        a_label: str = "A", b_label: str = "B",
    ) -> AbComparison:
        a_results = [self.run_one(t, a, scoring) for t in tasks]
        b_results = [self.run_one(t, b, scoring) for t in tasks]
        pairs = [
            (x, y) for x, y in zip(a_results, b_results)
            if x.counted and y.counted
        ]
        counted = len(pairs)
        return AbComparison(
            a_label, b_label,
            sum(x.score for x, _ in pairs) / counted if counted else 0.0,
            sum(y.score for _, y in pairs) / counted if counted else 0.0,
            counted,
            sum(1 for r in a_results if not r.counted),
            sum(1 for r in b_results if not r.counted),
        )


# ─────────────────────────────────────────────────────────────────────────────
# Test scaffolding


class FrozenClock:
    """A clock that only moves when told.

    Every test that touches expiry, decay or a refractory period needs one.
    Sleeping instead makes a suite slow AND flaky, which is the worst pair.
    """

    def __init__(self, at: datetime | None = None) -> None:
        self._at = at or datetime(2026, 1, 1, tzinfo=timezone.utc)

    def now(self) -> datetime:
        return self._at

    def advance(self, delta: timedelta) -> datetime:
        self._at += delta
        return self._at

    def set(self, at: datetime) -> None:
        """Allows moving BACKWARDS on purpose, to test what a device does when
        its clock is corrected - which happens, and is exactly when time-based
        logic goes wrong."""
        self._at = at


class DeterministicIds:
    """Sequential identifiers, so a golden file can contain one.

    A random id in a snapshot makes every comparison fail, and the usual fix -
    stripping ids before comparing - also stops the test noticing when the wrong
    id is used.
    """

    def __init__(self, prefix: str = "id") -> None:
        self._prefix = prefix
        self._next = 0
        self._lock = threading.Lock()

    def next(self) -> str:
        with self._lock:
            self._next += 1
            return f"{self._prefix}-{self._next:04d}"

    def reset(self) -> None:
        with self._lock:
            self._next = 0


@dataclass(frozen=True)
class SnapshotDiff:
    """What changed between a golden file and what happened."""

    matches: bool = True
    #: A unified diff, ready to print. Empty when they match.
    diff: str = ""
    added: int = 0
    removed: int = 0

    @property
    def summary(self) -> str:
        if self.matches:
            return "unchanged"
        return f"{self.added} lines added, {self.removed} removed"


class ISnapshotComparer(ABC):
    """Compares actual output against a golden."""

    @abstractmethod
    def compare(self, expected: str, actual: str) -> SnapshotDiff: ...


class LineDiffSnapshotComparer(ISnapshotComparer):
    """Line-by-line, with the line endings normalised first.

    A golden file written on Windows and compared on a Mac differs on EVERY
    line, which buries the one real change in a diff of the whole file.
    """

    def compare(self, expected: str, actual: str) -> SnapshotDiff:
        left = expected.replace("\r\n", "\n").split("\n")
        right = actual.replace("\r\n", "\n").split("\n")
        if left == right:
            return SnapshotDiff()
        lines = list(difflib.unified_diff(
            left, right, "golden", "actual", lineterm="", n=2))
        return SnapshotDiff(
            False, "\n".join(lines),
            sum(1 for l in lines if l.startswith("+") and not l.startswith("+++")),
            sum(1 for l in lines if l.startswith("-") and not l.startswith("---")),
        )


class NullSnapshotComparer(ISnapshotComparer):
    """Says everything matches.

    Named so that using it is a visible decision - a suite wired to this passes
    unconditionally, which is worth being obvious about.
    """

    def compare(self, expected: str, actual: str) -> SnapshotDiff:
        return SnapshotDiff()


class IGoldenStore(ABC):
    """Holds expected outputs."""

    @abstractmethod
    def get(self, key: str) -> str | None: ...

    @abstractmethod
    def put(self, key: str, value: str) -> None: ...

    @property
    @abstractmethod
    def is_writable(self) -> bool: ...


class InMemoryGoldenStore(IGoldenStore):
    """Goldens for a test run.

    `update_mode` is OFF by default and has to be turned on deliberately: a
    store that rewrites the golden whenever it differs is a test that can never
    fail, and it will happily record a regression as the new expectation.
    """

    def __init__(self, seed: dict[str, str] | None = None, update_mode: bool = False) -> None:
        self._values = dict(seed or {})
        self._update_mode = update_mode
        self._written: list[str] = []

    @property
    def is_writable(self) -> bool:
        return self._update_mode

    @property
    def written_keys(self) -> tuple[str, ...]:
        return tuple(self._written)

    def get(self, key: str) -> str | None:
        return self._values.get(key)

    def put(self, key: str, value: str) -> None:
        if not self._update_mode:
            raise PermissionError(
                "this golden store is read-only; run with update mode to "
                "record a new expectation")
        self._values[key] = value
        self._written.append(key)

    def verify(
        self, key: str, actual: str, comparer: ISnapshotComparer | None = None
    ) -> SnapshotDiff:
        """A MISSING golden is not a pass.

        In update mode it is recorded; otherwise it is a difference, because a
        test whose expectation does not exist has not checked anything.
        """
        expected = self.get(key)
        if expected is None:
            if self._update_mode:
                self.put(key, actual)
                return SnapshotDiff()
            return SnapshotDiff(
                False, f"no golden recorded for {key!r}",
                added=len(actual.split("\n")))
        return (comparer or LineDiffSnapshotComparer()).compare(expected, actual)


class NullGoldenStore(IGoldenStore):
    """Holds nothing and accepts nothing."""

    def get(self, key: str) -> str | None:
        return None

    def put(self, key: str, value: str) -> None:
        return None

    @property
    def is_writable(self) -> bool:
        return False
