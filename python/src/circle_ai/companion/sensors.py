"""What the companion senses, and where it keeps what it learns.

AFFECT IS INFERRED, NEVER KNOWN. A face, a voice and a wrist all produce numbers
that CORRELATE with how somebody feels, and a system that treats a correlation
as a fact will confidently tell a person they are angry when they are
concentrating. So every mapper here returns a confidence alongside its guess,
every one has an UNCERTAIN answer it is willing to give, and low confidence is
carried through rather than rounded away.

NO RAW BIOMETRIC IS EVER STORED. A face embedding is not a face and a voice
embedding is not a recording, and the difference is the whole reason those are
what get kept. Nothing here writes an image or a waveform anywhere.
"""

from __future__ import annotations

import hashlib
import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import Enum
from typing import Callable, Sequence


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# Affect


class AffectLabel(Enum):
    """A coarse guess at how somebody seems.

    DELIBERATELY COARSE. Finer categories are not more accurate, they are more
    confidently wrong - the underlying signal does not distinguish irritation
    from concentration, and offering a label that claims to only makes the error
    harder to notice.
    """

    #: The honest default and a real answer. A mapper that must always choose a
    #: feeling will always find one.
    UNCERTAIN = "uncertain"
    CALM = "calm"
    ENGAGED = "engaged"
    STRESSED = "stressed"
    TIRED = "tired"
    LOW = "low"


@dataclass(frozen=True)
class AffectReading:
    """A guess, with how much to trust it."""

    label: AffectLabel = AffectLabel.UNCERTAIN
    #: 0..1. Below the mapper's floor this is forced to UNCERTAIN before it
    #: leaves, so nothing downstream has to remember to check.
    confidence: float = 0.0
    #: Where it came from, so two readings can be weighed against each other and
    #: a person can be told what was actually observed.
    source: str = ""
    at: datetime = field(default_factory=_now)

    @property
    def is_actionable(self) -> bool:
        return self.label is not AffectLabel.UNCERTAIN and self.confidence >= 0.5

    def describe(self) -> str:
        if self.label is AffectLabel.UNCERTAIN:
            return "not enough to say how things are"
        return f"seems {self.label.value} ({self.confidence:.0%} from {self.source})"


class AffectMapperBase:
    """Turns sensor numbers into a guess, and refuses to over-claim."""

    #: Under this, the answer is UNCERTAIN whatever the numbers say. Set where
    #: it is because a coin-flip dressed as an observation is worse than saying
    #: nothing.
    CONFIDENCE_FLOOR = 0.45

    def _settle(self, label: AffectLabel, confidence: float, source: str) -> AffectReading:
        c = max(0.0, min(1.0, confidence))
        if c < self.CONFIDENCE_FLOOR:
            return AffectReading(AffectLabel.UNCERTAIN, c, source)
        return AffectReading(label, c, source)


class FaceAffectMapper(AffectMapperBase):
    """From facial expression scores.

    THE WEAKEST OF THE THREE and treated as such. Expression is culturally
    variable, it is easily posed, and a camera sees a face lit by a phone in a
    dark room. Its confidence is deliberately scaled down so it loses to a voice
    or a wrist reading that disagrees.
    """

    #: Everything this mapper produces is multiplied by this. A face is
    #: corroboration, not evidence.
    TRUST = 0.7

    def map(self, scores: dict[str, float]) -> AffectReading:
        """Scores keyed by expression name, each 0..1."""
        if not scores:
            return AffectReading(source="face")
        label_for = {
            "neutral": AffectLabel.CALM, "happy": AffectLabel.ENGAGED,
            "angry": AffectLabel.STRESSED, "fear": AffectLabel.STRESSED,
            "sad": AffectLabel.LOW, "tired": AffectLabel.TIRED,
        }
        best = max(scores.items(), key=lambda kv: kv[1])
        # The MARGIN over the runner-up, not the top score alone. A face scoring
        # 0.6 happy and 0.58 sad has told us nothing, and reporting 0.6
        # confidence would be a lie about a near-tie.
        rest = sorted((v for k, v in scores.items() if k != best[0]), reverse=True)
        margin = best[1] - (rest[0] if rest else 0.0)
        return self._settle(
            label_for.get(best[0].lower(), AffectLabel.UNCERTAIN),
            margin * self.TRUST + best[1] * 0.2, "face")


class BiosignalAffectMapper(AffectMapperBase):
    """From heart rate and its variability.

    RESTING HEART RATE IS PER PERSON. Sixty is athletic for one person and
    elevated for another, so everything here is relative to that person's own
    baseline - an absolute threshold tells half the population they are
    permanently stressed.

    A baseline that has not been established yields UNCERTAIN rather than being
    guessed from a population average.
    """

    def __init__(self, resting_bpm: float = 0.0, baseline_hrv_ms: float = 0.0) -> None:
        self._resting = resting_bpm
        self._baseline_hrv = baseline_hrv_ms

    @property
    def has_baseline(self) -> bool:
        return self._resting > 0

    def map(self, bpm: float, hrv_ms: float = 0.0, moving: bool = False) -> AffectReading:
        if not self.has_baseline or bpm <= 0:
            return AffectReading(source="biosignal")
        if moving:
            # MOVEMENT INVALIDATES THE READING. A raised heart rate while
            # walking upstairs is not stress, and this is the most common false
            # positive a wrist device produces.
            return AffectReading(AffectLabel.UNCERTAIN, 0.0, "biosignal")
        lift = (bpm - self._resting) / max(1.0, self._resting)
        if lift > 0.25:
            return self._settle(AffectLabel.STRESSED, min(1.0, lift * 2), "biosignal")
        if self._baseline_hrv > 0 and hrv_ms > 0:
            drop = (self._baseline_hrv - hrv_ms) / self._baseline_hrv
            if drop > 0.3:
                return self._settle(AffectLabel.TIRED, min(1.0, drop * 1.6), "biosignal")
        if lift < -0.05:
            return self._settle(AffectLabel.CALM, 0.6, "biosignal")
        return self._settle(AffectLabel.CALM, 0.5, "biosignal")


class AffectStateVadExtensions:
    """Combining affect with voice activity.

    THE POINT: a voice-derived affect reading taken while nobody is speaking is
    reading room noise. Pairing the two is what stops an empty room being
    reported as a calm person.
    """

    @staticmethod
    def is_reading_trustworthy(reading: AffectReading, speech_present: bool) -> bool:
        if reading.source == "voice" and not speech_present:
            return False
        return reading.is_actionable

    @staticmethod
    def combine(readings: Sequence[AffectReading]) -> AffectReading:
        """Weighted by confidence, and a DISAGREEMENT lowers the result.

        Two sources that disagree are less informative than one that is sure,
        and averaging them into a confident middle is the standard way to turn
        two weak signals into one wrong strong one.
        """
        usable = [r for r in readings if r.label is not AffectLabel.UNCERTAIN]
        if not usable:
            return AffectReading(source="combined")
        totals: dict[AffectLabel, float] = {}
        for r in usable:
            totals[r.label] = totals.get(r.label, 0.0) + r.confidence
        best = max(totals.items(), key=lambda kv: kv[1])
        agreement = best[1] / sum(totals.values())
        return AffectReading(
            best[0], min(1.0, best[1] / len(usable)) * agreement, "combined")


# ─────────────────────────────────────────────────────────────────────────────
# Sensors


@dataclass(frozen=True)
class SpeakerIdentity:
    """Who the voice belongs to, probably."""

    speaker_id: str = ""
    confidence: float = 0.0
    #: True when the voice matched nobody enrolled. NOT the same as low
    #: confidence in a match: an unknown speaker is a fact, a weak match is a
    #: doubt.
    is_unknown: bool = True


class OnnxSpeakerIdentityAdapter:
    """Matches a voice against enrolled embeddings.

    ONLY EMBEDDINGS ARE HELD, never audio. An embedding cannot be played back,
    which is the difference between a device that recognises a household and one
    that has recorded it.

    A high threshold on purpose: mistaking one family member for another is
    worse than asking.
    """

    #: Cosine similarity. 0.72 is strict enough that similar voices in one
    #: household do not cross over.
    THRESHOLD = 0.72

    def __init__(self, embed: Callable[[Sequence[float]], list[float]] | None = None) -> None:
        self._embed = embed
        self._lock = threading.Lock()
        self._enrolled: dict[str, list[float]] = {}

    @property
    def is_available(self) -> bool:
        return self._embed is not None

    @staticmethod
    def cosine(a: Sequence[float], b: Sequence[float]) -> float:
        if not a or not b or len(a) != len(b):
            return 0.0
        dot = sum(x * y for x, y in zip(a, b))
        na = math.sqrt(sum(x * x for x in a))
        nb = math.sqrt(sum(y * y for y in b))
        return 0.0 if na == 0 or nb == 0 else dot / (na * nb)

    def enrol(self, speaker_id: str, samples: Sequence[Sequence[float]]) -> bool:
        """Averages SEVERAL samples into one template.

        One sample enrols the room and the microphone as much as the voice, and
        the person then fails to be recognised anywhere else in the house.
        """
        if self._embed is None or len(samples) < 2:
            return False
        vectors = [self._embed(s) for s in samples]
        width = min(len(v) for v in vectors)
        with self._lock:
            self._enrolled[speaker_id] = [
                sum(v[i] for v in vectors) / len(vectors) for i in range(width)]
        return True

    def identify(self, audio: Sequence[float]) -> SpeakerIdentity:
        if self._embed is None:
            return SpeakerIdentity()
        live = self._embed(audio)
        with self._lock:
            candidates = list(self._enrolled.items())
        if not candidates:
            return SpeakerIdentity()
        best_id, best_score = "", 0.0
        for speaker_id, template in candidates:
            score = self.cosine(live, template)
            if score > best_score:
                best_id, best_score = speaker_id, score
        if best_score < self.THRESHOLD:
            return SpeakerIdentity("", best_score, True)
        return SpeakerIdentity(best_id, best_score, False)

    def forget(self, speaker_id: str) -> bool:
        """Enrolment must be undoable, or it is not consent."""
        with self._lock:
            return self._enrolled.pop(speaker_id, None) is not None


class OnnxSpeechEmotionSensor(AffectMapperBase):
    """Affect from the voice itself, not the words.

    PROSODY ONLY - pace, pitch and energy. It never looks at what was said,
    which is what lets it run without the transcript and without keeping one.
    """

    def __init__(self, infer: Callable[[Sequence[float]], dict[str, float]] | None = None) -> None:
        self._infer = infer

    @property
    def is_available(self) -> bool:
        return self._infer is not None

    def sense(self, audio: Sequence[float], speech_present: bool = True) -> AffectReading:
        if self._infer is None or not speech_present or not audio:
            # No speech means no reading. Inferring emotion from silence reads
            # the room's air conditioning.
            return AffectReading(source="voice")
        scores = self._infer(audio)
        if not scores:
            return AffectReading(source="voice")
        label_for = {
            "neutral": AffectLabel.CALM, "happy": AffectLabel.ENGAGED,
            "angry": AffectLabel.STRESSED, "sad": AffectLabel.LOW,
            "tired": AffectLabel.TIRED,
        }
        best = max(scores.items(), key=lambda kv: kv[1])
        return self._settle(
            label_for.get(best[0].lower(), AffectLabel.UNCERTAIN), best[1], "voice")


class FaceCompanionBridge:
    """Brings face signals to the companion, or refuses to.

    THE CAMERA IS OFF UNLESS SOMEBODY TURNED IT ON, and "on" has a timeout.
    A camera that stays on because a screen was opened once is a camera that is
    always on.
    """

    def __init__(
        self,
        mapper: FaceAffectMapper | None = None,
        now: Callable[[], datetime] | None = None,
    ) -> None:
        self._mapper = mapper or FaceAffectMapper()
        self._now = now or _now
        self._enabled_until: datetime | None = None

    @property
    def is_enabled(self) -> bool:
        return self._enabled_until is not None and self._now() < self._enabled_until

    def enable_for(self, minutes: int = 5) -> datetime:
        """Time-limited, always. There is no way to turn this on permanently."""
        self._enabled_until = self._now() + timedelta(minutes=max(1, min(60, minutes)))
        return self._enabled_until

    def disable(self) -> None:
        self._enabled_until = None

    def read(self, scores: dict[str, float]) -> AffectReading:
        if not self.is_enabled:
            return AffectReading(source="face")
        return self._mapper.map(scores)


class AmbientCompanionMonitor:
    """Notices what is going on without recording it.

    IT KEEPS COUNTS AND LEVELS, NEVER AUDIO. The whole design question for an
    always-listening feature is what it retains, and the answer here is: a
    number per window, and nothing that can be played back.
    """

    def __init__(self, window: timedelta = timedelta(minutes=5)) -> None:
        self._window = window
        self._lock = threading.Lock()
        self._speech_seconds = 0.0
        self._quiet_seconds = 0.0
        self._events = 0
        self._since = _now()

    def observe(self, seconds: float, speech_present: bool) -> None:
        with self._lock:
            if speech_present:
                self._speech_seconds += seconds
            else:
                self._quiet_seconds += seconds
            self._events += 1

    def summary(self) -> dict[str, object]:
        with self._lock:
            total = self._speech_seconds + self._quiet_seconds
            return {
                "window_seconds": round(total, 1),
                "speech_fraction": round(self._speech_seconds / total, 3) if total else 0.0,
                "observations": self._events,
            }

    def reset(self) -> None:
        with self._lock:
            self._speech_seconds = self._quiet_seconds = 0.0
            self._events = 0
            self._since = _now()


class IoTCompanionPipeline:
    """Devices in the house, and what the companion may do with them.

    READ IS FREE, WRITE IS NOT. Asking a thermostat its temperature is not the
    same as changing it, and a pipeline that treats them alike will eventually
    unlock a door because a sentence was ambiguous.
    """

    #: Actions that are never taken without an explicit confirmation, whatever
    #: was asked. Each one is either a safety matter or irreversible.
    GUARDED = frozenset({"unlock", "open", "disarm", "off", "unmute"})

    def __init__(
        self,
        read: Callable[[str], object] | None = None,
        write: Callable[[str, object], bool] | None = None,
        confirm: Callable[[str, str], bool] | None = None,
    ) -> None:
        self._read = read
        self._write = write
        self._confirm = confirm

    def read_state(self, device_id: str) -> tuple[object | None, str]:
        if self._read is None:
            return None, "nothing is connected to this device"
        return self._read(device_id), ""

    def act(self, device_id: str, action: str, value: object = None) -> tuple[bool, str]:
        if self._write is None:
            return False, "nothing is connected to this device"
        if action.lower() in self.GUARDED:
            if self._confirm is None:
                # No confirmation route means the guarded action does not
                # happen. Falling through would be the worst possible default.
                return False, (
                    f"{action} needs you to confirm, and there is no way to ask "
                    f"you right now")
            if not self._confirm(device_id, action):
                return False, f"{action} was not confirmed"
        return self._write(device_id, value if value is not None else action), ""


# ─────────────────────────────────────────────────────────────────────────────
# Voice and delegation


@dataclass(frozen=True)
class NeuronVoice:
    """How the companion sounds, for one person, on one device.

    A VOICE IS A CHOICE AND NOT AN IDENTITY. The same companion sounds different
    on two devices if the person wanted that, and nothing about who they are
    depends on it.
    """

    voice_id: str = ""
    language: str = ""
    rate: float = 1.0
    pitch: float = 1.0
    #: What to fall back to when the chosen voice is not installed. Falling back
    #: to silence is how a device becomes mute after a factory reset.
    fallback_voice_id: str = ""

    def __post_init__(self) -> None:
        if not 0.5 <= self.rate <= 2.0:
            raise ValueError("a speaking rate outside 0.5-2.0 is not intelligible")
        if not 0.5 <= self.pitch <= 2.0:
            raise ValueError("a pitch outside 0.5-2.0 is not a voice")

    def resolve(self, installed: Sequence[str]) -> str:
        """The chosen voice, the fallback, or the first installed - in that
        order. Never empty when anything at all is installed."""
        for candidate in (self.voice_id, self.fallback_voice_id):
            if candidate and candidate in installed:
                return candidate
        return installed[0] if installed else ""


class EcdsaCryptoDelegation:
    """Lets one device act for another, narrowly and briefly.

    THE DELEGATION IS SIGNED BY THE GRANTER AND CARRIES ITS OWN LIMITS: what may
    be done, and until when. Nothing here holds a private key - signing and
    verifying are callables the platform keystore provides, because a key that
    reaches this process is a key that reaches a crash dump.
    """

    def __init__(
        self,
        sign: Callable[[bytes], bytes] | None = None,
        verify: Callable[[bytes, bytes, str], bool] | None = None,
    ) -> None:
        self._sign = sign
        self._verify = verify

    @staticmethod
    def canonical(
        granter: str, grantee: str, capability: str, expires_at: datetime
    ) -> bytes:
        """The exact bytes that get signed.

        FIELD-SEPARATED with a character that cannot appear in a field, so
        ("a", "b|c") and ("a|b", "c") cannot produce the same message - a
        confusion that would let a grant be reinterpreted as a different one.
        """
        for field_value in (granter, grantee, capability):
            if "\x1f" in field_value:
                raise ValueError("a delegation field cannot contain a separator")
        return "\x1f".join(
            (granter, grantee, capability, expires_at.isoformat())).encode()

    def delegate(
        self, granter: str, grantee: str, capability: str, minutes: int = 30
    ) -> dict[str, object] | None:
        if self._sign is None or not granter or not grantee or not capability:
            return None
        expires = _now() + timedelta(minutes=max(1, min(24 * 60, minutes)))
        message = self.canonical(granter, grantee, capability, expires)
        return {
            "granter": granter, "grantee": grantee, "capability": capability,
            "expires_at": expires.isoformat(),
            "signature": self._sign(message).hex(),
        }

    def accept(
        self, delegation: dict[str, object], granter_public_key: str,
        capability: str, now: datetime | None = None,
    ) -> tuple[bool, str]:
        """Checks EXPIRY FIRST, then capability, then the signature.

        Cheapest and most conclusive first: an expired grant is invalid however
        well it is signed, and verifying a signature on it wastes the one
        expensive operation in the check.
        """
        if self._verify is None:
            return False, "this device cannot verify a delegation"
        at = now or _now()
        try:
            expires = datetime.fromisoformat(str(delegation.get("expires_at", "")))
        except ValueError:
            return False, "this delegation has no readable expiry"
        if at >= expires:
            return False, "this delegation has expired"
        if str(delegation.get("capability", "")) != capability:
            return False, (
                f"this delegation is for "
                f"{delegation.get('capability')!r}, not {capability!r}")
        message = self.canonical(
            str(delegation.get("granter", "")), str(delegation.get("grantee", "")),
            capability, expires)
        try:
            signature = bytes.fromhex(str(delegation.get("signature", "")))
        except ValueError:
            return False, "this delegation's signature is not readable"
        if not self._verify(message, signature, granter_public_key):
            return False, "this delegation was not signed by that device"
        return True, "accepted"


# ─────────────────────────────────────────────────────────────────────────────
# What the companion keeps


class SqliteKnowledgeGraph:
    """Facts as subject-predicate-object, on disk.

    PARAMETERISED, ALWAYS. Every value here comes from something somebody said,
    and a graph built by concatenating strings into SQL is a graph anybody can
    rewrite by saying the right sentence. There is not one place in this class
    where a value is formatted into a statement.
    """

    SCHEMA = (
        "CREATE TABLE IF NOT EXISTS facts ("
        " subject TEXT NOT NULL, predicate TEXT NOT NULL, object TEXT NOT NULL,"
        " confidence REAL NOT NULL DEFAULT 1.0, at TEXT NOT NULL,"
        " PRIMARY KEY (subject, predicate, object))",
        # An index on the object as well as the subject: "who works at Circle"
        # is asked as often as "where does she work", and without it the second
        # is a full scan.
        "CREATE INDEX IF NOT EXISTS facts_object ON facts (object)",
    )

    def __init__(self, execute: Callable[[str, tuple], list[tuple]] | None = None) -> None:
        self._execute = execute

    def initialise(self) -> bool:
        if self._execute is None:
            return False
        for statement in self.SCHEMA:
            self._execute(statement, ())
        return True

    def assert_fact(
        self, subject: str, predicate: str, obj: str, confidence: float = 1.0
    ) -> bool:
        if self._execute is None or not (subject and predicate and obj):
            return False
        self._execute(
            "INSERT OR REPLACE INTO facts"
            " (subject, predicate, object, confidence, at) VALUES (?, ?, ?, ?, ?)",
            (subject, predicate, obj, max(0.0, min(1.0, confidence)),
             _now().isoformat()))
        return True

    def about(self, subject: str) -> list[tuple]:
        if self._execute is None:
            return []
        return self._execute(
            "SELECT predicate, object, confidence FROM facts WHERE subject = ?"
            " ORDER BY confidence DESC", (subject,))

    def forget(self, subject: str) -> bool:
        """Forgetting everything about somebody has to be one call.

        A person who asks to be forgotten should not depend on the caller
        enumerating predicates correctly.
        """
        if self._execute is None:
            return False
        self._execute("DELETE FROM facts WHERE subject = ? OR object = ?",
                      (subject, subject))
        return True


class SqliteHippoRagStore:
    """Passages plus the links between them.

    THE LINKS ARE THE POINT. Retrieval by similarity alone returns whatever is
    phrased like the question; following links from what matched returns what is
    actually related to it, which is how a recall answers about a person rather
    than about a wording.
    """

    SCHEMA = (
        "CREATE TABLE IF NOT EXISTS passages ("
        " id TEXT PRIMARY KEY, text TEXT NOT NULL, at TEXT NOT NULL)",
        "CREATE TABLE IF NOT EXISTS links ("
        " from_id TEXT NOT NULL, to_id TEXT NOT NULL, weight REAL NOT NULL,"
        " PRIMARY KEY (from_id, to_id))",
    )

    def __init__(self, execute: Callable[[str, tuple], list[tuple]] | None = None) -> None:
        self._execute = execute

    def initialise(self) -> bool:
        if self._execute is None:
            return False
        for statement in self.SCHEMA:
            self._execute(statement, ())
        return True

    def add(self, passage_id: str, text: str) -> bool:
        if self._execute is None or not passage_id:
            return False
        self._execute(
            "INSERT OR REPLACE INTO passages (id, text, at) VALUES (?, ?, ?)",
            (passage_id, text, _now().isoformat()))
        return True

    def link(self, from_id: str, to_id: str, weight: float = 1.0) -> bool:
        """Links are DIRECTED and stored once each way by the caller.

        Storing one direction and reading it both ways makes the weight mean two
        different things, and a link that is strong one way is often weak the
        other - a name recalls a meeting far better than a meeting recalls a
        name.
        """
        if self._execute is None or from_id == to_id:
            return False
        self._execute(
            "INSERT OR REPLACE INTO links (from_id, to_id, weight)"
            " VALUES (?, ?, ?)", (from_id, to_id, weight))
        return True

    def neighbours(self, passage_id: str, limit: int = 8) -> list[tuple]:
        if self._execute is None:
            return []
        return self._execute(
            "SELECT to_id, weight FROM links WHERE from_id = ?"
            " ORDER BY weight DESC LIMIT ?", (passage_id, max(1, limit)))


class CompanionRecallExtensions:
    """Turning what was remembered into something worth saying.

    THE HARD PART IS LEAVING THINGS OUT. A companion that recites everything it
    knows about somebody every time they speak is unusable and slightly
    frightening; one that mentions the right single thing is the whole product.
    """

    #: At most this many remembered items reach a prompt. Small on purpose -
    #: more context is not more relevance, and every extra item competes for the
    #: model's attention with what was actually asked.
    MAX_ITEMS = 5

    @staticmethod
    def rank(
        items: Sequence[tuple[str, float]], query_terms: Sequence[str]
    ) -> list[tuple[str, float]]:
        """Ranked by stored strength AND by overlap with what was asked.

        Strength alone surfaces the same favourite fact forever; overlap alone
        surfaces whatever happens to share a word.
        """
        terms = {t.lower() for t in query_terms if len(t) > 2}
        scored: list[tuple[str, float]] = []
        for text, strength in items:
            words = {w.strip(".,!?").lower() for w in text.split()}
            overlap = len(terms & words) / len(terms) if terms else 0.0
            scored.append((text, strength * 0.5 + overlap * 0.5))
        return sorted(scored, key=lambda kv: kv[1], reverse=True)

    @staticmethod
    def to_prompt(
        items: Sequence[tuple[str, float]], query_terms: Sequence[str] = (),
        floor: float = 0.25,
    ) -> str:
        """Returns EMPTY when nothing clears the floor.

        An empty string rather than a heading with nothing under it: a prompt
        that says "what I remember:" followed by nothing tells the model there
        is nothing to remember about this person, which is worse than saying
        nothing at all.
        """
        ranked = [
            (text, score)
            for text, score in CompanionRecallExtensions.rank(items, query_terms)
            if score >= floor
        ][:CompanionRecallExtensions.MAX_ITEMS]
        if not ranked:
            return ""
        return "worth remembering:\n" + "\n".join(f"- {text}" for text, _ in ranked)


# ─────────────────────────────────────────────────────────────────────────────
# Proactive


@dataclass(frozen=True)
class ProactiveSchedulerOptions:
    """When the companion may speak first.

    OFF BY DEFAULT. Something that talks to you unprompted has to be asked for;
    a device that starts doing it after an update is a device people turn off.
    """

    enabled: bool = False
    #: Never more often than this, whatever is queued. The single most important
    #: number here: a proactive assistant that interrupts twice is one that gets
    #: silenced permanently.
    min_interval: timedelta = timedelta(hours=2)
    #: Local hours it will not speak in. Defaults to overnight.
    quiet_from_hour: int = 21
    quiet_to_hour: int = 7
    max_per_day: int = 4

    def is_quiet_hour(self, hour: int) -> bool:
        """Handles a window that crosses midnight.

        21:00 to 07:00 is not `from <= h <= to`, and writing it that way makes
        the quiet hours apply during the DAY instead - the exact inverse of what
        was asked for.
        """
        if self.quiet_from_hour == self.quiet_to_hour:
            return False
        if self.quiet_from_hour < self.quiet_to_hour:
            return self.quiet_from_hour <= hour < self.quiet_to_hour
        return hour >= self.quiet_from_hour or hour < self.quiet_to_hour


class ProactiveSchedulerBackgroundService:
    """Decides whether now is a moment to say something."""

    def __init__(
        self,
        options: ProactiveSchedulerOptions | None = None,
        now: Callable[[], datetime] | None = None,
    ) -> None:
        self._options = options or ProactiveSchedulerOptions()
        self._now = now or _now
        self._lock = threading.Lock()
        self._last: datetime | None = None
        self._today: list[datetime] = []

    @property
    def options(self) -> ProactiveSchedulerOptions:
        return self._options

    def may_speak(self) -> tuple[bool, str]:
        opts = self._options
        if not opts.enabled:
            return False, "speaking first is turned off"
        at = self._now()
        if opts.is_quiet_hour(at.hour):
            return False, f"it is {at.hour:02d}:00 and these are quiet hours"
        with self._lock:
            self._today = [t for t in self._today if t.date() == at.date()]
            if len(self._today) >= opts.max_per_day:
                return False, "that is enough for one day"
            if self._last is not None and at - self._last < opts.min_interval:
                remaining = opts.min_interval - (at - self._last)
                return False, f"too soon - {int(remaining.total_seconds() // 60)} minutes to go"
        return True, "now is a reasonable moment"

    def record_spoken(self) -> None:
        """Recorded by the CALLER after it actually spoke, not when permission
        was given. A permission that was granted and not used must not count
        against the day's budget."""
        at = self._now()
        with self._lock:
            self._last = at
            self._today.append(at)

    def spoken_today(self) -> int:
        at = self._now()
        with self._lock:
            return sum(1 for t in self._today if t.date() == at.date())
