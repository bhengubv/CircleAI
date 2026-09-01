"""The SQL seam, identity matching, and the last of the runtime probes.

EVERY QUERY HERE IS PARAMETERISED. Not as a style preference: every value that
reaches these stores came from something somebody said to an assistant, so a
store that formats values into SQL can be rewritten by saying the right sentence
out loud. There is not one f-string containing a value in this file - only
identifiers, which are quoted by the dialect and validated before they are.

BIOMETRICS ARE TEMPLATES, NEVER SAMPLES. What is stored is a vector that cannot
be played back or shown; the audio and the image are gone. And the threshold is
set where a false ACCEPT is the expensive error, because letting the wrong
person in is worse than asking the right one again.
"""

from __future__ import annotations

import math
import re
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import Enum
from typing import Callable, Sequence


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# SQL


class SqlDialect(Enum):
    """Which database is on the other end.

    THE DIFFERENCES ARE SMALL AND ALL OF THEM BREAK A QUERY. A placeholder
    written for one and sent to another is a syntax error at best and, on the
    databases that accept it, a literal string where a parameter was meant.
    """

    SQLITE = "sqlite"
    POSTGRES = "postgres"
    SQLSERVER = "sqlserver"
    MYSQL = "mysql"

    def placeholder(self, index: int) -> str:
        """`?` for SQLite and MySQL, `$1` for Postgres, `@p1` for SQL Server.

        Postgres and SQL Server are ONE-BASED and positional, so an index that
        starts at zero silently shifts every parameter by one - which usually
        produces a type error on the first column and a wrong row on the rest.
        """
        if self is SqlDialect.POSTGRES:
            return f"${index + 1}"
        if self is SqlDialect.SQLSERVER:
            return f"@p{index + 1}"
        return "?"

    def placeholders(self, count: int) -> str:
        return ", ".join(self.placeholder(i) for i in range(count))

    def quote(self, identifier: str) -> str:
        """Quotes a TABLE OR COLUMN name, having first checked it is one.

        A quoted identifier is not safe by itself - MySQL's backtick and the
        standard double quote can both be closed by a value containing them. So
        the name is validated against a strict pattern and refused if it is
        anything other than a plain identifier, and only then quoted.
        """
        if not re.match(r"^[A-Za-z_][A-Za-z0-9_]{0,62}$", identifier or ""):
            raise ValueError(
                f"{identifier!r} is not a plain identifier and will not be "
                f"put into a statement")
        if self is SqlDialect.MYSQL:
            return f"`{identifier}`"
        if self is SqlDialect.SQLSERVER:
            return f"[{identifier}]"
        return f'"{identifier}"'

    @property
    def upsert_clause(self) -> str:
        """Every one of these spells it differently and none of them warn."""
        if self is SqlDialect.SQLSERVER:
            # SQL Server has no upsert clause; the caller must MERGE or check
            # first. Saying so here beats emitting something that parses and
            # then duplicates rows.
            return ""
        if self is SqlDialect.MYSQL:
            return "ON DUPLICATE KEY UPDATE"
        return "ON CONFLICT"

    @property
    def text_type(self) -> str:
        return "NVARCHAR(MAX)" if self is SqlDialect.SQLSERVER else "TEXT"

    @property
    def supports_returning(self) -> bool:
        return self in (SqlDialect.POSTGRES, SqlDialect.SQLITE)


class AdoAtomStore:
    """Memory atoms in a relational database.

    THE TABLE NAME IS VALIDATED AND QUOTED, and every value is a parameter. A
    store where the table name is configurable is a store where the
    configuration is an injection point, so the name goes through the dialect's
    checked quoting on the way in and never appears again.
    """

    def __init__(
        self,
        dialect: SqlDialect = SqlDialect.SQLITE,
        execute: Callable[[str, tuple], list[tuple]] | None = None,
        table: str = "atoms",
    ) -> None:
        self._dialect = dialect
        self._execute = execute
        # Validated ONCE, at construction, so a bad name fails where it was
        # configured rather than on the first query in production.
        self._table = dialect.quote(table)

    @property
    def dialect(self) -> SqlDialect:
        return self._dialect

    def create_table_sql(self) -> str:
        d = self._dialect
        return (
            f"CREATE TABLE IF NOT EXISTS {self._table} ("
            f" {d.quote('id')} {d.text_type} PRIMARY KEY,"
            f" {d.quote('kind')} {d.text_type} NOT NULL,"
            f" {d.quote('text')} {d.text_type} NOT NULL,"
            f" {d.quote('stability')} REAL NOT NULL,"
            f" {d.quote('created_at')} {d.text_type} NOT NULL,"
            f" {d.quote('last_recalled_at')} {d.text_type})"
        )

    def insert_sql(self) -> str:
        d = self._dialect
        columns = ", ".join(d.quote(c) for c in (
            "id", "kind", "text", "stability", "created_at", "last_recalled_at"))
        return (
            f"INSERT INTO {self._table} ({columns}) "
            f"VALUES ({d.placeholders(6)})"
        )

    def select_by_kind_sql(self) -> str:
        d = self._dialect
        return (
            f"SELECT {d.quote('id')}, {d.quote('text')}, {d.quote('stability')} "
            f"FROM {self._table} WHERE {d.quote('kind')} = {d.placeholder(0)} "
            f"ORDER BY {d.quote('stability')} DESC"
        )

    def initialise(self) -> bool:
        if self._execute is None:
            return False
        self._execute(self.create_table_sql(), ())
        return True

    def put(
        self, atom_id: str, kind: str, text: str, stability: float = 90.0
    ) -> bool:
        if self._execute is None or not atom_id:
            return False
        self._execute(self.insert_sql(), (
            atom_id, kind, text, stability, _now().isoformat(), None))
        return True

    def by_kind(self, kind: str) -> list[tuple]:
        if self._execute is None:
            return []
        return self._execute(self.select_by_kind_sql(), (kind,))

    def forget(self, atom_id: str) -> bool:
        if self._execute is None:
            return False
        d = self._dialect
        self._execute(
            f"DELETE FROM {self._table} WHERE {d.quote('id')} = {d.placeholder(0)}",
            (atom_id,))
        return True


@dataclass(frozen=True)
class StoredGoal:
    """Something the person is working towards."""

    goal_id: str
    text: str = ""
    #: None means no deadline, which is different from a deadline that has
    #: passed. A goal with no date should never appear as overdue.
    due_at: datetime | None = None
    progress: float = 0.0
    is_done: bool = False
    created_at: datetime = field(default_factory=_now)

    def is_overdue_at(self, when: datetime) -> bool:
        return not self.is_done and self.due_at is not None and when > self.due_at


class InMemoryGoalStore:
    """Goals, in memory.

    ORDERED BY WHAT IS ACTUALLY PRESSING - overdue first, then by deadline, then
    the undated. Sorting by creation date buries a deadline under whatever was
    typed most recently.
    """

    def __init__(self, now: Callable[[], datetime] | None = None) -> None:
        self._now = now or _now
        self._lock = threading.Lock()
        self._goals: dict[str, StoredGoal] = {}

    def put(self, goal: StoredGoal) -> None:
        with self._lock:
            self._goals[goal.goal_id] = goal

    def get(self, goal_id: str) -> StoredGoal | None:
        with self._lock:
            return self._goals.get(goal_id)

    def complete(self, goal_id: str) -> bool:
        with self._lock:
            goal = self._goals.get(goal_id)
            if goal is None or goal.is_done:
                return False
            self._goals[goal_id] = StoredGoal(
                goal.goal_id, goal.text, goal.due_at, 1.0, True, goal.created_at)
            return True

    def open_goals(self) -> list[StoredGoal]:
        at = self._now()
        with self._lock:
            live = [g for g in self._goals.values() if not g.is_done]
        # `due_at or a far future` rather than filtering: an undated goal still
        # belongs in the list, just at the end of it.
        far = datetime.max.replace(tzinfo=timezone.utc)
        return sorted(
            live, key=lambda g: (not g.is_overdue_at(at), g.due_at or far, g.created_at))

    def overdue(self) -> list[StoredGoal]:
        at = self._now()
        with self._lock:
            return [g for g in self._goals.values() if g.is_overdue_at(at)]


# ─────────────────────────────────────────────────────────────────────────────
# Identity


@dataclass(frozen=True)
class IdentityRecord:
    """Who somebody is to this device.

    NO NAME IS REQUIRED. A device can recognise a person without knowing who
    they are, and requiring a name would mean asking for one before the first
    recognition - which is exactly the moment a person is least willing to give
    it.
    """

    identity_id: str
    display_name: str = ""
    enrolled_at: datetime = field(default_factory=_now)
    #: Templates only. Never a photograph, never a recording.
    template_count: int = 0


class InMemoryIdentityStore:
    """Identities and their templates.

    FORGETTING IS ONE CALL and removes everything. A person who asks to be
    forgotten must not depend on a caller enumerating what to delete.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._records: dict[str, IdentityRecord] = {}
        self._templates: dict[str, list[list[float]]] = {}

    def enrol(self, identity_id: str, template: Sequence[float], name: str = "") -> bool:
        if not identity_id or not template:
            return False
        with self._lock:
            self._templates.setdefault(identity_id, []).append(list(template))
            self._records[identity_id] = IdentityRecord(
                identity_id, name or self._records.get(
                    identity_id, IdentityRecord(identity_id)).display_name,
                self._records[identity_id].enrolled_at
                if identity_id in self._records else _now(),
                len(self._templates[identity_id]))
        return True

    def get(self, identity_id: str) -> IdentityRecord | None:
        with self._lock:
            return self._records.get(identity_id)

    def templates(self) -> dict[str, list[list[float]]]:
        with self._lock:
            return {k: [list(v) for v in vs] for k, vs in self._templates.items()}

    def forget(self, identity_id: str) -> bool:
        with self._lock:
            had = identity_id in self._records
            self._records.pop(identity_id, None)
            self._templates.pop(identity_id, None)
            return had

    def forget_everyone(self) -> int:
        with self._lock:
            count = len(self._records)
            self._records.clear()
            self._templates.clear()
            return count


@dataclass(frozen=True)
class BiometricMatch:
    """A match, or the absence of one."""

    identity_id: str = ""
    similarity: float = 0.0
    matched: bool = False
    #: How far clear of the runner-up. A match that only just beat another
    #: person is not a match, and this is what lets a caller see that.
    margin: float = 0.0

    @property
    def is_ambiguous(self) -> bool:
        return self.matched and self.margin < BiometricMatcher.MIN_MARGIN


class BiometricMatcher:
    """Matches a live template against enrolled ones.

    TWO TESTS, NOT ONE. A similarity above the threshold is not enough: it must
    also beat the second-best by a margin. Two siblings produce embeddings that
    both clear a threshold, and picking the higher of two near-identical scores
    is a coin flip with somebody's identity.

    THE THRESHOLD FAVOURS REFUSING. A false accept unlocks something for the
    wrong person; a false reject asks the right person to try again.
    """

    #: Cosine similarity.
    THRESHOLD = 0.75
    #: How far clear of second place a match must be.
    MIN_MARGIN = 0.06

    @staticmethod
    def cosine(a: Sequence[float], b: Sequence[float]) -> float:
        if not a or not b or len(a) != len(b):
            return 0.0
        dot = sum(x * y for x, y in zip(a, b))
        na = math.sqrt(sum(x * x for x in a))
        nb = math.sqrt(sum(y * y for y in b))
        return 0.0 if na == 0 or nb == 0 else dot / (na * nb)

    @classmethod
    def match(
        cls, live: Sequence[float], enrolled: dict[str, list[list[float]]]
    ) -> BiometricMatch:
        if not live or not enrolled:
            return BiometricMatch()
        # The BEST of a person's templates, not the average of them. Averaging
        # a person photographed in two very different lightings produces a
        # template that matches neither.
        scores = sorted(
            ((identity_id, max(cls.cosine(live, t) for t in templates))
             for identity_id, templates in enrolled.items() if templates),
            key=lambda kv: kv[1], reverse=True)
        if not scores:
            return BiometricMatch()
        best_id, best = scores[0]
        margin = best - (scores[1][1] if len(scores) > 1 else 0.0)
        if best < cls.THRESHOLD:
            return BiometricMatch("", best, False, margin)
        if margin < cls.MIN_MARGIN and len(scores) > 1:
            # Two people scored almost the same. Refusing is the only honest
            # answer; returning the higher one would be guessing.
            return BiometricMatch("", best, False, margin)
        return BiometricMatch(best_id, best, True, margin)


# ─────────────────────────────────────────────────────────────────────────────
# Runtime


@dataclass(frozen=True)
class Capability:
    """One thing a device can or cannot do."""

    name: str
    available: bool = False
    #: Why not, when it is not. "GPU unavailable" is unactionable; "no Vulkan
    #: driver on this device" tells somebody whether to look for one.
    reason: str = ""
    detail: str = ""


class CapabilityProbe:
    """Asks the device what it can do, once, and caches the answer.

    CACHED because probing costs - loading a library to find out whether it
    loads is the expensive way to ask - and because the answer does not change
    while the process lives.

    A PROBE THAT THROWS MEANS NOT AVAILABLE, with the exception as the reason. A
    probe that propagated its exception would make a missing optional feature
    into a crash at startup.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._probes: dict[str, Callable[[], bool]] = {}
        self._cache: dict[str, Capability] = {}

    def register(self, name: str, probe: Callable[[], bool]) -> "CapabilityProbe":
        with self._lock:
            self._probes[name] = probe
            self._cache.pop(name, None)
        return self

    def check(self, name: str) -> Capability:
        with self._lock:
            cached = self._cache.get(name)
            probe = self._probes.get(name)
        if cached is not None:
            return cached
        if probe is None:
            result = Capability(name, False, "nothing on this device provides it")
        else:
            try:
                result = Capability(name, bool(probe()))
                if not result.available:
                    result = Capability(name, False, "this device reported it cannot")
            except Exception as exc:  # noqa: BLE001
                result = Capability(name, False, str(exc))
        with self._lock:
            self._cache[name] = result
        return result

    def all(self) -> list[Capability]:
        with self._lock:
            names = sorted(self._probes)
        return [self.check(n) for n in names]

    def summary(self) -> str:
        results = self.all()
        can = [c.name for c in results if c.available]
        cannot = [f"{c.name} ({c.reason})" for c in results if not c.available]
        parts = []
        if can:
            parts.append("can: " + ", ".join(can))
        if cannot:
            parts.append("cannot: " + ", ".join(cannot))
        return "; ".join(parts) or "nothing has been probed"

    def invalidate(self) -> None:
        """For after something is installed. Without it a device that has just
        gained a capability keeps reporting it does not have one."""
        with self._lock:
            self._cache.clear()


class NativeRuntimeFetcher:
    """Fetches the native runtime for this device's ABI.

    THE DIGEST IS CHECKED BEFORE THE FILE IS PUT WHERE IT WILL BE LOADED. A
    native library is code that will run in this process - a partial download or
    a substituted file is not a corrupt asset, it is arbitrary code.

    A downloaded file lands in a TEMPORARY name and is moved into place only
    after it verifies, so a failure never leaves a half-written library where
    the loader will find it.
    """

    def __init__(
        self,
        download: Callable[[str, str], int] | None = None,
        digest_of: Callable[[str], str] | None = None,
        move: Callable[[str, str], None] | None = None,
        remove: Callable[[str], None] | None = None,
    ) -> None:
        self._download = download
        self._digest_of = digest_of
        self._move = move
        self._remove = remove

    def fetch(
        self, url: str, target_path: str, expected_sha256: str
    ) -> tuple[bool, str]:
        if not expected_sha256:
            # No digest means no fetch. This is a native library; running it
            # unverified is running whatever arrived.
            return False, "a native library will not be installed without a checksum"
        if self._download is None or self._digest_of is None or self._move is None:
            return False, "this device cannot fetch a native runtime"
        temporary = target_path + ".partial"
        try:
            self._download(url, temporary)
        except Exception as exc:  # noqa: BLE001
            return False, f"the download did not finish: {exc}"
        actual = self._digest_of(temporary)
        if actual.strip().lower() != expected_sha256.strip().lower():
            if self._remove is not None:
                self._remove(temporary)
            return False, "the downloaded runtime does not match its checksum"
        self._move(temporary, target_path)
        return True, "installed"


class CapabilityManifestSkillStore:
    """Skills, listed with what each one needs.

    A SKILL IS HIDDEN WHEN THE DEVICE CANNOT RUN IT. Offering a skill that needs
    a camera on a device with none, and failing when it is chosen, teaches
    people the assistant is unreliable rather than that the device is limited.
    """

    def __init__(self, probe: CapabilityProbe | None = None) -> None:
        self._probe = probe or CapabilityProbe()
        self._lock = threading.Lock()
        self._skills: dict[str, tuple[str, tuple[str, ...]]] = {}

    def register(
        self, skill_id: str, description: str, requires: Sequence[str] = ()
    ) -> None:
        if not skill_id.strip():
            raise ValueError("a skill needs an identifier")
        with self._lock:
            self._skills[skill_id] = (description, tuple(requires))

    def available(self) -> list[tuple[str, str]]:
        with self._lock:
            entries = list(self._skills.items())
        return [
            (skill_id, description)
            for skill_id, (description, requires) in sorted(entries)
            if all(self._probe.check(r).available for r in requires)
        ]

    def unavailable(self) -> list[tuple[str, str]]:
        """Kept and reportable, so somebody can be told WHY a skill is not
        there rather than being left to wonder whether it exists."""
        with self._lock:
            entries = list(self._skills.items())
        out: list[tuple[str, str]] = []
        for skill_id, (_, requires) in sorted(entries):
            missing = [r for r in requires if not self._probe.check(r).available]
            if missing:
                out.append((skill_id, f"needs {', '.join(missing)}"))
        return out


class HttpPackDownloader:
    """Fetches a skill pack.

    THE SAME RULE AS THE NATIVE RUNTIME: verified before it is unpacked, and
    unpacked into a contained directory. A pack is an archive from somewhere
    else, and an archive entry named `../../` is the oldest trick there is.
    """

    #: Entries above this are refused outright rather than trimmed. A pack that
    #: needs a hundred megabytes is not a skill.
    MAX_BYTES = 32 * 1024 * 1024

    def __init__(
        self,
        fetch: Callable[[str], bytes] | None = None,
        digest_of: Callable[[bytes], str] | None = None,
    ) -> None:
        self._fetch = fetch
        self._digest_of = digest_of

    @staticmethod
    def is_safe_entry(name: str) -> bool:
        """An archive entry that stays inside the directory it is unpacked to.

        Checked on the SEPARATOR-NORMALISED name, because a zip written on
        Windows carries backslashes and a check that only looks for `/` reads
        `..\\..\\etc` as a single ordinary filename.
        """
        normalised = (name or "").replace("\\", "/")
        if not normalised or normalised.startswith("/"):
            return False
        if re.match(r"^[A-Za-z]:", normalised):
            return False
        return ".." not in normalised.split("/")

    def download(self, url: str, expected_sha256: str) -> tuple[bytes, str]:
        """Returns (bytes, error). Never both."""
        if self._fetch is None:
            return b"", "this device cannot download a pack"
        if not expected_sha256:
            return b"", "a pack will not be installed without a checksum"
        try:
            data = self._fetch(url)
        except Exception as exc:  # noqa: BLE001
            return b"", f"the pack did not download: {exc}"
        if len(data) > self.MAX_BYTES:
            return b"", (
                f"that pack is {len(data) // (1024 * 1024)} MB, which is more "
                f"than a skill should be")
        if self._digest_of is not None:
            if self._digest_of(data).strip().lower() != expected_sha256.strip().lower():
                return b"", "the pack does not match its checksum"
        return data, ""


class DtmfToneGenerator:
    """The dual tones a phone keypad makes.

    DUAL-TONE MULTI-FREQUENCY: each key is a low tone AND a high tone together,
    and that is the whole design - a single tone can occur in speech, two
    specific ones simultaneously essentially cannot, which is why a phone system
    can hear a keypress through a conversation.

    The two must be summed at EQUAL amplitude and each at no more than half
    scale. Sending one louder than the other is the commonest reason a switch
    fails to decode a digit that sounds perfectly correct to a person.
    """

    LOW_HZ = (697, 770, 852, 941)
    HIGH_HZ = (1209, 1336, 1477, 1633)
    KEYS = ("123A", "456B", "789C", "*0#D")

    #: ITU-T Q.24 says at least 40 ms of tone and 40 ms of silence between
    #: digits. Shorter and a switch reads two presses as one.
    TONE_MS = 100
    GAP_MS = 60

    @classmethod
    def frequencies_for(cls, key: str) -> tuple[int, int] | None:
        """None for a key that is not on a keypad, rather than a guess."""
        for row, keys in enumerate(cls.KEYS):
            column = keys.find(key.upper())
            if column >= 0:
                return cls.LOW_HZ[row], cls.HIGH_HZ[column]
        return None

    @classmethod
    def samples_for(
        cls, key: str, sample_rate_hz: int = 8000, milliseconds: int = 0
    ) -> list[float]:
        pair = cls.frequencies_for(key)
        if pair is None:
            return []
        low, high = pair
        count = int(sample_rate_hz * (milliseconds or cls.TONE_MS) / 1000)
        # 0.45 each, summing to at most 0.9. Equal amplitudes, and headroom left
        # so the sum cannot clip.
        return [
            0.45 * math.sin(2 * math.pi * low * i / sample_rate_hz)
            + 0.45 * math.sin(2 * math.pi * high * i / sample_rate_hz)
            for i in range(count)
        ]

    @classmethod
    def samples_for_sequence(
        cls, digits: str, sample_rate_hz: int = 8000
    ) -> list[float]:
        """A gap after EVERY digit including the last.

        The trailing gap matters: without it the last digit runs into whatever
        audio follows, and a switch reads the join as a further keypress.
        """
        gap = [0.0] * int(sample_rate_hz * cls.GAP_MS / 1000)
        out: list[float] = []
        for digit in digits:
            tone = cls.samples_for(digit, sample_rate_hz)
            if not tone:
                # An unknown character is SKIPPED, not silently turned into a
                # pause - a phone number with a space in it must dial the same
                # as one without.
                continue
            out += tone + gap
        return out


@dataclass(frozen=True)
class ToolProgress:
    """How far a long-running tool has got."""

    tool_name: str = ""
    #: None when the total is unknown, which is common and must not be shown as
    #: 0%. An unknown total is a spinner, not a bar.
    fraction: float | None = None
    message: str = ""
    is_final: bool = False


class StreamingToolRunner:
    """Runs a tool while telling the caller what is happening.

    PROGRESS IS RATE-LIMITED. A tool that reports every row sends thousands of
    updates a second down a link measured in tens of messages a second, and the
    progress reporting becomes the reason the call is slow.

    THE FINAL UPDATE IS NEVER DROPPED, whatever the rate limit says - it is the
    one that tells the caller to stop waiting.
    """

    #: At most one update per this interval, plus the final one.
    MIN_INTERVAL = timedelta(milliseconds=250)

    def __init__(
        self,
        emit: Callable[[ToolProgress], None] | None = None,
        monotonic: Callable[[], float] | None = None,
    ) -> None:
        self._emit = emit
        self._monotonic = monotonic or (lambda: 0.0)
        self._last_sent = -1e9
        self._sent = 0
        self._suppressed = 0

    @property
    def sent(self) -> int:
        return self._sent

    @property
    def suppressed(self) -> int:
        return self._suppressed

    def report(self, progress: ToolProgress) -> bool:
        if self._emit is None:
            return False
        now = self._monotonic()
        if not progress.is_final and now - self._last_sent < self.MIN_INTERVAL.total_seconds():
            self._suppressed += 1
            return False
        self._last_sent = now
        self._sent += 1
        self._emit(progress)
        return True

    def run(
        self, tool_name: str, work: Callable[[Callable[[str, float | None], None]], object]
    ) -> tuple[object, str]:
        """Runs `work`, handing it a reporter.

        A tool that raises still gets a FINAL update. Without it a caller waits
        on a progress stream that simply stopped, which is indistinguishable
        from a slow tool.
        """
        def report(message: str, fraction: float | None = None) -> None:
            self.report(ToolProgress(tool_name, fraction, message))

        try:
            result = work(report)
        except Exception as exc:  # noqa: BLE001
            self.report(ToolProgress(tool_name, None, str(exc), True))
            return None, str(exc)
        self.report(ToolProgress(tool_name, 1.0, "done", True))
        return result, ""
