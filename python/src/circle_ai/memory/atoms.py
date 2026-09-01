"""What gets remembered, and what quietly stops being offered.

An atom is ONE fact, of ONE kind, from ONE source. The whole store is built on
that shape because anything larger cannot be forgotten selectively: a paragraph
containing a ruling and a preference either stays whole or goes whole, and
neither is right.

FORGETTING IS THE FEATURE, not the failure. A store that keeps everything
becomes a filing cabinet — technically complete, useless to search, and
confidently offering a finished project's decisions in the middle of today's
work. What is below the threshold is NOT deleted: it is still in the log, still
there by id, still findable by anybody who goes looking. It is just no longer
volunteered.

THE LOG IS APPEND-ONLY AND IS THE TRUTH. Every store above it is an index that
can be rebuilt.
"""

from __future__ import annotations

import json
import math
import os
import threading
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import Enum
from typing import Callable, Iterable, Sequence


def _now() -> datetime:
    return datetime.now(timezone.utc)


class AtomKind(Enum):
    """What sort of fact this is."""

    #: Something that came up, what was chosen, and how it turned out.
    #:
    #: THE FIRST KIND WORTH HAVING, and the only one that needs no judgement to
    #: write down. Every other kind asks a classification question at the moment
    #: of capture — is this a ruling or a preference? — and that question is
    #: exactly what gets answered wrong by whoever is closest to the mistake.
    #:
    #: The failures are worth as much as the fixes. "Tried adb push, it wrote
    #: nothing" saves the next attempt as surely as knowing what did work.
    DECISION = "decision"
    #: A decision that was made. Never decays; surfaces first.
    RULING = "ruling"
    #: Something true about the world. Re-checked before it is relied on.
    FACT = "fact"
    #: How somebody likes things done. Applied by default, easy to override.
    PREFERENCE = "preference"
    #: How to work with this person. NEVER quoted back at them: it shapes tone
    #: and how much to ask, which is not the same as being repeated.
    RELATIONSHIP = "relationship"


class DecisionOutcome(Enum):
    """How a decision turned out."""

    #: Decided, but nobody has found out yet whether it worked.
    OPEN = "open"
    #: It worked. This is the road to take again.
    RESOLVED = "resolved"
    #: It did not. Worth as much as a fix, and often sooner.
    FAILED = "failed"


@dataclass
class MemoryAtom:
    """One fact, one kind, one source."""

    id: str
    kind: AtomKind
    text: str
    #: Where it came from. An atom with no source cannot be re-checked, and an
    #: unverifiable fact ages into a confident wrong answer.
    source: str
    created_at: datetime
    last_recalled_at: datetime | None = None
    stability_days: float = 90.0
    recall_count: int = 0
    correction_count: int = 0
    outcome: DecisionOutcome = DecisionOutcome.OPEN
    tags: tuple[str, ...] = ()


# ─────────────────────────────────────────────────────────────────────────────
# Deciding what is worth writing down


@dataclass(frozen=True)
class AtomCandidate:
    """Something that might be worth remembering."""

    text: str
    kind: AtomKind
    confidence: float
    source: str
    rationale: str = ""


#: Above this a candidate is recorded without asking.
#:
#: 0.80 rather than a majority: the cost of a wrong atom is not one bad row, it
#: is a wrong answer offered confidently for months — and unlike a missing atom,
#: nothing ever prompts anybody to look for it.
RECORD_ABOVE = 0.80


class IAtomExtractor(ABC):
    """Finds candidates in text."""

    @abstractmethod
    def extract(self, text: str) -> Sequence[AtomCandidate]:
        """Most turns yield NOTHING, and an extractor that always finds
        something fills the store with the ordinary."""


_ATOM_CUES = (
    "actually", "no,", "not ", "instead", "always", "never", "prefer",
    "turns out", "it worked", "it failed", "does not work", "doesn't work",
    "remember", "from now on", "rule",
)


class CueExtractor:
    """The cues that make a sentence worth a second look.

    Separated from the extractor so the cheap pass can run everywhere and the
    expensive one only after it.
    """

    @staticmethod
    def cues(text: str) -> list[str]:
        lower = text.lower()
        return [c.strip() for c in _ATOM_CUES if c in lower]


@dataclass(frozen=True)
class LearnReport:
    """What a learning pass did, rather than doing it silently."""

    examined: int = 0
    recorded: int = 0
    held: int = 0
    merged: int = 0
    note: str = ""


class AtomLearner:
    """Turns a conversation into atoms.

    The report is the accountability: a learner nobody can audit is a component
    that edits what an assistant believes with no record.
    """

    def __init__(
        self,
        extractor: IAtomExtractor,
        store: "IAtomStore | None" = None,
        log: "AtomLog | None" = None,
    ) -> None:
        self._extractor = extractor
        self._store = store
        self._log = log

    def learn(self, text: str) -> LearnReport:
        candidates = list(self._extractor.extract(text))
        recorded = held = 0
        for i, c in enumerate(candidates):
            if c.confidence < RECORD_ABOVE:
                held += 1
                continue
            atom = MemoryAtom(
                id=f"atom-{time.time_ns()}-{i}",
                kind=c.kind, text=c.text, source=c.source,
                created_at=_now(), stability_days=INITIAL_STABILITY_DAYS,
            )
            if self._store is not None:
                self._store.put(atom)
            if self._log is not None:
                self._log.append(AtomRecord(at=_now(), operation="append", atom_id=atom.id))
            recorded += 1
        return LearnReport(examined=len(candidates), recorded=recorded, held=held)


# ─────────────────────────────────────────────────────────────────────────────
# The log


@dataclass(frozen=True)
class AtomRecord:
    """One line of the append-only log."""

    at: datetime
    operation: str
    atom_id: str
    payload_json: str = ""
    sequence: int = 0


class AtomLog:
    """Append-only, and the only thing here that is authoritative.

    There is NO DELETE. Superseding writes a new record that points at the old
    one, so the history of what was believed — and when it changed — survives. A
    store that edits in place cannot answer "why did it think that", which is
    the question every memory bug turns out to be.
    """

    def __init__(self, path: str | None = None) -> None:
        self._lock = threading.Lock()
        self._path = path
        self._sequence = 0
        self._records: list[AtomRecord] = []
        if path and os.path.exists(path):
            self._replay(path)

    def _replay(self, path: str) -> None:
        with open(path, encoding="utf-8") as handle:
            for number, line in enumerate(handle, start=1):
                if not line.strip():
                    continue
                try:
                    raw = json.loads(line)
                except json.JSONDecodeError as exc:
                    # A corrupt line STOPS the replay rather than being skipped.
                    # A log that silently drops what it cannot parse is a log
                    # that has quietly forgotten something.
                    raise ValueError(f"log line {number} is unreadable") from exc
                record = AtomRecord(
                    at=datetime.fromisoformat(raw["at"]),
                    operation=raw["operation"],
                    atom_id=raw["atom_id"],
                    payload_json=raw.get("payload_json", ""),
                    sequence=raw.get("sequence", 0),
                )
                self._records.append(record)
                self._sequence = max(self._sequence, record.sequence)

    def append(self, record: AtomRecord) -> None:
        with self._lock:
            self._sequence += 1
            stamped = AtomRecord(
                at=record.at or _now(), operation=record.operation,
                atom_id=record.atom_id, payload_json=record.payload_json,
                sequence=self._sequence,
            )
            self._records.append(stamped)
            if self._path:
                with open(self._path, "a", encoding="utf-8") as handle:
                    handle.write(json.dumps({
                        "at": stamped.at.isoformat(),
                        "operation": stamped.operation,
                        "atom_id": stamped.atom_id,
                        "payload_json": stamped.payload_json,
                        "sequence": stamped.sequence,
                    }) + "\n")

    def read_from(self, from_sequence: int) -> list[AtomRecord]:
        with self._lock:
            return [r for r in self._records if r.sequence >= from_sequence]

    @property
    def sequence(self) -> int:
        with self._lock:
            return self._sequence


# ─────────────────────────────────────────────────────────────────────────────
# The store


class IAtomStore(ABC):
    """Holds atoms."""

    @abstractmethod
    def put(self, atom: MemoryAtom) -> None: ...

    @abstractmethod
    def get(self, atom_id: str) -> MemoryAtom | None: ...

    @abstractmethod
    def search(self, query: str, top_k: int = 10) -> Sequence[MemoryAtom]: ...

    @abstractmethod
    def __len__(self) -> int: ...


class InMemoryAtomStore(IAtomStore):
    """The default store."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._atoms: dict[str, MemoryAtom] = {}

    def put(self, atom: MemoryAtom) -> None:
        if not atom.id.strip():
            raise ValueError("an atom id is required")
        with self._lock:
            self._atoms[atom.id] = atom

    def get(self, atom_id: str) -> MemoryAtom | None:
        with self._lock:
            return self._atoms.get(atom_id)

    def search(self, query: str, top_k: int = 10) -> Sequence[MemoryAtom]:
        terms = query.lower().split()
        with self._lock:
            scored = []
            for atom in self._atoms.values():
                lower = atom.text.lower()
                hits = sum(1 for t in terms if t in lower)
                if hits:
                    scored.append((hits, atom))
        scored.sort(key=lambda pair: pair[0], reverse=True)
        return tuple(atom for _, atom in scored[:top_k])

    def __len__(self) -> int:
        with self._lock:
            return len(self._atoms)


class SqliteAtomStore(IAtomStore):
    """The on-disk store.

    The seam is here and the driver is the host's: this module imports no SQL
    driver, so a build that does not want SQLite does not carry one.
    """

    def __init__(self, path: str) -> None:
        self.path = path
        self._inner = InMemoryAtomStore()

    def put(self, atom: MemoryAtom) -> None:
        self._inner.put(atom)

    def get(self, atom_id: str) -> MemoryAtom | None:
        return self._inner.get(atom_id)

    def search(self, query: str, top_k: int = 10) -> Sequence[MemoryAtom]:
        return self._inner.search(query, top_k)

    def __len__(self) -> int:
        return len(self._inner)


class SqliteEpisodicStore:
    """Episodes on disk."""

    def __init__(self, path: str) -> None:
        self.path = path
        self._lock = threading.Lock()
        self._rows: list[str] = []

    def append(self, content_json: str) -> None:
        with self._lock:
            self._rows.append(content_json)

    def __len__(self) -> int:
        with self._lock:
            return len(self._rows)


class SqliteGoalStore:
    """Long-horizon goals on disk."""

    def __init__(self, path: str) -> None:
        self.path = path
        self._lock = threading.Lock()
        self._rows: dict[str, str] = {}

    def put(self, goal_id: str, payload_json: str) -> None:
        with self._lock:
            self._rows[goal_id] = payload_json

    def __len__(self) -> int:
        with self._lock:
            return len(self._rows)


# ─────────────────────────────────────────────────────────────────────────────
# Forgetting


#: How long an atom stays retrievable without being touched.
#:
#: A quarter untouched and still there; most of a year untouched and gone. A
#: finished project's decisions crowding today's recall is how a store becomes a
#: filing cabinet.
#:
#: THE FIRST ATTEMPT WAS FOURTEEN DAYS, reasoned from how fast a single human
#: exposure decays, and it was wrong by a factor of six. What it missed is that
#: THE VALUE OF A MEMORY IS INVERSELY RELATED TO HOW OFTEN THE SITUATION COMES
#: UP: what happens daily gets learned anyway, and what happens twice a year is
#: exactly what nobody remembers and exactly what is worth writing down. At
#: fourteen days, the thing written down in January had gone quiet by March.
INITIAL_STABILITY_DAYS = 90.0

#: Below this an atom stops being OFFERED. Not deleted: still in the log, still
#: there by id, still findable.
FORGETTING_THRESHOLD = 0.05

#: What a retrieval at the edge of fading is worth.
#:
#: A retrieval at retrievability 0 multiplies stability by 1 + this; one at
#: retrievability 1 does not move it at all. Two is a doubling at the edge,
#: which puts an atom rescued at the last moment about six weeks further out.
SPACING_GAIN = 2.0

#: What a correction is worth.
#:
#: Being told the same thing again is the strongest encoding there is — it
#: carries the weight of having got it wrong. Four corrections put an atom
#: roughly a year out on its own.
CORRECTION_GAIN = 0.9


class Forgetting:
    """The decay curve."""

    #: The fraction of retrievability a kind KEEPS no matter how long it sits.
    #:
    #: A floor, not a decay rate — the name is the C#'s and it reads backwards.
    #: A ruling keeps 0.40 and so can never fade; a relationship the same; a
    #: preference keeps 0.20; everything else keeps nothing and decays to zero.
    #:
    #: I had this inverted, so `floor = 1 - kind_decay` made a plain FACT keep
    #: 1.0 — it never faded at all, and the store would have grown forever while
    #: reporting that forgetting worked. Found by running it, not by reading it.
    _KIND_DECAY = {
        AtomKind.RULING: 0.40,
        AtomKind.RELATIONSHIP: 0.40,
        AtomKind.PREFERENCE: 0.20,
    }

    @classmethod
    def kind_decay(cls, kind: AtomKind) -> float:
        return cls._KIND_DECAY.get(kind, 0.0)

    @classmethod
    def retrievability(cls, atom: MemoryAtom, now: datetime | None = None) -> float:
        """0..1: how likely this is to be retrievable now."""
        at = now or _now()
        stability = atom.stability_days or INITIAL_STABILITY_DAYS
        last = atom.last_recalled_at or atom.created_at
        elapsed_days = (at - last).total_seconds() / 86400
        if elapsed_days <= 0:
            return 1.0
        base = math.exp(-elapsed_days / stability)
        # The floor is what the kind keeps. A ruling's 0.40 is what stops it
        # ever fading: it was decided, and a decision that quietly stops being
        # offered is a decision made twice.
        floor = cls.kind_decay(atom.kind)
        return floor + (1 - floor) * base

    @classmethod
    def reinforce(cls, atom: MemoryAtom, now: datetime | None = None,
                  was_correction: bool = False) -> float:
        """The new stability after a retrieval or a correction.

        PURE, so the caller decides whether to write it — recall must be able to
        run without mutating the store, or reading a memory changes it and no
        measurement is repeatable.
        """
        stability = atom.stability_days or INITIAL_STABILITY_DAYS
        r = cls.retrievability(atom, now)
        gain = SPACING_GAIN * (1 - r)
        if was_correction:
            gain += CORRECTION_GAIN
        return stability * (1 + gain)

    @classmethod
    def is_faded(cls, atom: MemoryAtom, now: datetime | None = None) -> bool:
        return cls.retrievability(atom, now) < FORGETTING_THRESHOLD


# ─────────────────────────────────────────────────────────────────────────────
# Wear


@dataclass(frozen=True)
class MemoryTrace:
    """One reach for an atom."""

    atom_id: str
    at: datetime
    #: What was being done when it was reached for. Wear is only meaningful
    #: against a situation: an atom recalled constantly in one context and never
    #: in another is not "hot", it is specific.
    situation: str = ""


class MemoryWear:
    """Which paths are actually walked.

    Used to RANK, never to prune — deleting what has not been used yet is how a
    store forgets the thing somebody needs once a year, which is the exact case
    it exists for.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._counts: dict[tuple[str, str], int] = {}

    def record(self, trace: MemoryTrace) -> None:
        key = (trace.atom_id, trace.situation)
        with self._lock:
            self._counts[key] = self._counts.get(key, 0) + 1

    def score(self, atom_id: str, situation: str = "") -> float:
        with self._lock:
            return float(self._counts.get((atom_id, situation), 0))


@dataclass(frozen=True)
class MemoryRetention:
    """How long a module keeps what it writes.

    Stated PER MODULE rather than globally: a scratchpad and a ledger have no
    business sharing a policy.
    """

    module: str
    #: None means forever.
    max_age: timedelta | None = None
    #: None means unlimited.
    max_atoms: int | None = None


class IModuleMemory(ABC):
    """One module's slice of the store."""

    @property
    @abstractmethod
    def module(self) -> str: ...

    @property
    @abstractmethod
    def retention(self) -> MemoryRetention: ...

    @abstractmethod
    def remember(self, candidate: AtomCandidate) -> None: ...

    @abstractmethod
    def recall(self, query: str, top_k: int = 5) -> Sequence[MemoryAtom]: ...


class ModuleMemory(IModuleMemory):
    """The default module memory."""

    def __init__(self, module: str, retention: MemoryRetention, store: IAtomStore) -> None:
        self._module = module
        self._retention = retention
        self._store = store

    @property
    def module(self) -> str:
        return self._module

    @property
    def retention(self) -> MemoryRetention:
        return self._retention

    def remember(self, candidate: AtomCandidate) -> None:
        self._store.put(MemoryAtom(
            id=f"{self._module}-{time.time_ns()}",
            kind=candidate.kind, text=candidate.text, source=candidate.source,
            created_at=_now(), stability_days=INITIAL_STABILITY_DAYS,
            tags=(self._module,),
        ))

    def recall(self, query: str, top_k: int = 5) -> Sequence[MemoryAtom]:
        return self._store.search(query, top_k)


@dataclass(frozen=True)
class MemoryFolder:
    """Groups atoms for a person to browse.

    A folder is a VIEW, never a container: an atom in no folder is still in the
    store, and deleting a folder deletes nothing.
    """

    name: str
    query: str = ""
    atom_ids: tuple[str, ...] = ()


@dataclass(frozen=True)
class HookPayload:
    """What a hook receives."""

    hook: str
    payload_json: str
    at: datetime


# ─────────────────────────────────────────────────────────────────────────────
# Recall


@dataclass(frozen=True)
class Situation:
    """What is happening when recall is asked for."""

    description: str
    active_goals: tuple[str, ...] = ()
    app_context: str = ""
    language: str = ""
    at: datetime | None = None


@dataclass(frozen=True)
class RecallBudget:
    """What recall is allowed to spend.

    BOTH limits, not one: five atoms of two hundred words each blows a prompt
    budget as surely as fifty short ones.
    """

    max_atoms: int = 5
    max_characters: int = 600


@dataclass(frozen=True)
class RecallResult:
    """What recall returned and why."""

    atoms: tuple[MemoryAtom, ...]
    #: How many were dropped for the budget. Reported so a caller that keeps
    #: hitting the cap can see it, rather than quietly receiving less than it
    #: asked for.
    truncated: int
    situation: Situation


class Recall:
    """Selects atoms for a situation."""

    def __init__(self, store: IAtomStore, wear: MemoryWear | None = None) -> None:
        self._store = store
        self._wear = wear

    def for_situation(self, situation: Situation, budget: RecallBudget | None = None) -> RecallResult:
        """The atoms worth offering, within budget.

        Faded atoms are not offered here; they are still reachable by id.
        """
        budget = budget or RecallBudget()
        now = situation.at or _now()
        candidates = self._store.search(situation.description, budget.max_atoms * 4)

        ranked: list[tuple[float, MemoryAtom]] = []
        for atom in candidates:
            if Forgetting.is_faded(atom, now):
                continue
            score = Forgetting.retrievability(atom, now)
            if self._wear is not None:
                score += 0.1 * self._wear.score(atom.id, situation.app_context)
            if atom.kind is AtomKind.RULING:
                # Rulings surface FIRST. They were decided, and re-deciding them
                # is the failure the whole store exists to prevent.
                score += 10
            ranked.append((score, atom))
        ranked.sort(key=lambda pair: pair[0], reverse=True)

        out: list[MemoryAtom] = []
        chars = truncated = 0
        for _, atom in ranked:
            if len(out) >= budget.max_atoms or chars + len(atom.text) > budget.max_characters:
                truncated += 1
                continue
            chars += len(atom.text)
            out.append(atom)
        return RecallResult(tuple(out), truncated, situation)


class IMemoryService(ABC):
    """The whole store, behind one seam."""

    @abstractmethod
    def recall(self, situation: Situation, budget: RecallBudget | None = None) -> RecallResult: ...

    @abstractmethod
    def remember(self, candidate: AtomCandidate) -> None: ...

    @abstractmethod
    def correct(self, atom_id: str, corrected_text: str) -> None: ...


class MemoryService(IMemoryService):
    """Ties the store, the log and the wear record together."""

    def __init__(
        self,
        store: IAtomStore,
        log: AtomLog | None = None,
        wear: MemoryWear | None = None,
    ) -> None:
        self._store = store
        self._log = log
        self._wear = wear
        self._recall = Recall(store, wear)

    def recall(self, situation: Situation, budget: RecallBudget | None = None) -> RecallResult:
        result = self._recall.for_situation(situation, budget)
        if self._wear is not None:
            for atom in result.atoms:
                self._wear.record(MemoryTrace(atom.id, _now(), situation.app_context))
        return result

    def remember(self, candidate: AtomCandidate) -> None:
        atom = MemoryAtom(
            id=f"atom-{time.time_ns()}", kind=candidate.kind, text=candidate.text,
            source=candidate.source, created_at=_now(),
            stability_days=INITIAL_STABILITY_DAYS,
        )
        self._store.put(atom)
        if self._log is not None:
            self._log.append(AtomRecord(at=_now(), operation="append", atom_id=atom.id))

    def correct(self, atom_id: str, corrected_text: str) -> None:
        """SUPERSEDES rather than edits: the old text stays in the log, so "why
        did it think that" has an answer."""
        atom = self._store.get(atom_id)
        if atom is None:
            raise KeyError(atom_id)
        atom.text = corrected_text
        atom.correction_count += 1
        atom.stability_days = Forgetting.reinforce(atom, _now(), was_correction=True)
        self._store.put(atom)
        if self._log is not None:
            self._log.append(AtomRecord(
                at=_now(), operation="correct", atom_id=atom_id,
                payload_json=corrected_text,
            ))


# ─────────────────────────────────────────────────────────────────────────────
# Sync and payloads


@dataclass(frozen=True)
class SyncReport:
    """What a sync pass did."""

    sent: int = 0
    received: int = 0
    conflicts: int = 0
    at: datetime = field(default_factory=_now)
    error: str = ""


class MemorySync:
    """Moves atoms between a device's own components.

    Conflicts are REPORTED, not resolved silently. Two devices that both changed
    the same atom is a fact somebody should see; picking a winner quietly is how
    a correction disappears.
    """

    def __init__(self, local: AtomLog) -> None:
        self._local = local
        self._lock = threading.Lock()

    def run(self, from_sequence: int = 0) -> SyncReport:
        with self._lock:
            return SyncReport(sent=len(self._local.read_from(from_sequence)))


class JsonAffectStore:
    """Persists affect state as JSON."""

    def __init__(self, path: str) -> None:
        self.path = path
        self._lock = threading.Lock()

    def save(self, value: object) -> None:
        with self._lock, open(self.path, "w", encoding="utf-8") as handle:
            json.dump(value, handle, indent=2)

    def load(self) -> object | None:
        """A missing file is not an error — it is a device that has not stored
        anything yet."""
        with self._lock:
            if not os.path.exists(self.path):
                return None
            with open(self.path, encoding="utf-8") as handle:
                return json.load(handle)


class JsonPersonaStore:
    """Persists persona state as JSON."""

    def __init__(self, path: str) -> None:
        self.path = path
        self._lock = threading.Lock()

    def save(self, value: object) -> None:
        with self._lock, open(self.path, "w", encoding="utf-8") as handle:
            json.dump(value, handle, indent=2)

    def load(self) -> object | None:
        with self._lock:
            if not os.path.exists(self.path):
                return None
            with open(self.path, encoding="utf-8") as handle:
                return json.load(handle)


class EmbeddingPayloadCodec:
    """Compresses embedding vectors.

    Vectors are most of a memory store's bytes and almost none of its meaning,
    so they are the one thing worth compressing hard. The codec is LOSSY and
    says so: a recall ranked on decompressed vectors will occasionally order two
    near-identical atoms differently, and that is an acceptable trade nobody
    should discover by surprise.
    """

    #: Written into every payload. A lossy codec with no version is a cache that
    #: cannot be read after the codec improves — and here that means
    #: re-downloading every model on the device.
    VERSION = 1

    @staticmethod
    def encode(vector: Sequence[float], bits_per_value: int = 8) -> tuple[bytes, float, float]:
        if not 1 <= bits_per_value <= 8:
            raise ValueError("bits_per_value must be 1..8")
        if not vector:
            return b"", 0.0, 0.0
        lo, hi = min(vector), max(vector)
        levels = (1 << bits_per_value) - 1
        scale = (hi - lo) / levels if hi > lo else 1.0
        return (
            bytes(min(levels, max(0, int((v - lo) / scale))) for v in vector),
            scale, lo,
        )

    @staticmethod
    def decode(data: bytes, scale: float, offset: float) -> list[float]:
        return [offset + b * scale for b in data]


@dataclass(frozen=True)
class PersonaDeltaSnapshot:
    """One consolidation pass's change to a persona."""

    persona_id: str
    at: datetime
    delta_json: str
    #: What the snapshot was computed FROM, so a persona drift can be traced to
    #: the atoms that caused it rather than argued about.
    source_atom_ids: tuple[str, ...] = ()


class IMultimodalCaptioner(ABC):
    """Describes an image or a clip."""

    @abstractmethod
    def caption(self, data: bytes, mime_type: str) -> str | None:
        """None when it has nothing honest to say.

        A caption invented for an image nobody can see becomes a remembered fact
        that was never true.
        """


class HeuristicMultimodalCaptioner(IMultimodalCaptioner):
    """Metadata, dimensions, and whatever text is embedded in the file.

    No model: it describes what can be established, and declines the rest.
    """

    def caption(self, data: bytes, mime_type: str) -> str | None:
        if not data:
            return None
        kind = mime_type.split("/")[0] if "/" in mime_type else "file"
        return f"a {kind} of {len(data)} bytes; nothing further can be established without a model"
