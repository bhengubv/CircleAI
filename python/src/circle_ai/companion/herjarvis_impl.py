# companion/herjarvis_impl.py
#
# Real, working in-process implementations for the HER/Jarvis contracts that
# aren't already implemented in world_model.py / predictive_engine.py /
# inner_monologue.py / theory_of_mind.py. Ported from
# CircleAI.Companion.HerJarvis (HerJarvisRealImplementations.cs) — the C#
# reference.
#
# Each impl is deterministic and stdlib-only (dict/list/asyncio.Queue/simple
# math) so tests and hosts both get behaviour, not no-ops. Where C# binds native
# crypto (ECDSA P-256), the default here is a real local HMAC-SHA256 signer and
# the asymmetric signer is an injectable dependency (``signer=``) — never a stub.
#
# Ports (by contract number in HerJarvisContracts):
#   1.  HeartbeatAlwaysOnPresence      — asyncio heartbeat with start/stop
#   2.  ChannelFusedPerception         — asyncio.Queue pub/sub
#   3.  JsonIdentitySync               — append-only delta log + monotonic cursor
#   4.  EwaContinuousLearner           — exponentially-weighted average reward
#   6.  InMemoryGoalPursuer            — goal + milestone plan, replan
#   7.  TfEpisodicMemory               — term-frequency similarity recall
#   8.  EnergyBandVoiceIdentity        — mean-MFCC fingerprint + cosine similarity
#   9.  HistoricalCalibratedConfidence — nearest-neighbour calibration band
#   11. KeywordEmotionSensor           — keyword arousal/valence inference
#   12. DemoStoreSkillAcquisition      — demo store with name extraction
#   15. AdjacencyPersonalKnowledgeGraph— adjacency-list graph
#   16. TopicLiveWorldKnowledge        — topic pub/sub broker
#   17. ChannelBioSignalStream         — asyncio.Queue fan-in
#   18. RegistryPhysicalActuator       — device-handler registry
#   19. MailboxAgentPeerNetwork        — per-agent mailbox
#   20. InMemoryFederatedFineTuner     — job runner with status tracking
#   21. SlidingP50FirstTokenOptimizer  — sliding-window p50 latency
#   22. HmacCryptoDelegation           — HMAC-SHA256 sign+verify (ECDSA injectable)
#   23. SyntaxCheckingCodeGenerationLoop — balance-check + test runner
#   24. TrackingSelfImprovementLoop    — bench-score tracking + improvement
#
# (World model #5, theory-of-mind #10, inner-monologue #13, predictive #14 are
#  ported in their own modules and re-exported by the package __init__.)

from __future__ import annotations

import asyncio
import base64
import hashlib
import hmac
import json
import math
import os
import re
import struct
import threading
import uuid
from datetime import datetime, timedelta, timezone
from typing import (
    Awaitable,
    Callable,
    Dict,
    List,
    Optional,
    Sequence,
    Tuple,
)

from .herjarvis_contracts import (
    AcquiredSkill,
    AgentToAgentMessage,
    BioSignal,
    CodeGenJob,
    ConfidenceBand,
    DelegationCredential,
    EmotionFrame,
    EpisodeRecord,
    FineTuneJobStatus,
    FirstTokenBudget,
    FusedPercept,
    IAgentPeerNetwork,
    IAlwaysOnPresence,
    IBioSignalStream,
    ICalibratedConfidence,
    ICodeGenerationLoop,
    IContinuousLearner,
    ICryptoDelegation,
    IEmotionSensor,
    IEpisodicMemory,
    IFederatedFineTuner,
    IFirstTokenOptimizer,
    IFusedPerception,
    IGoalPursuer,
    IIdentitySync,
    ILiveWorldKnowledge,
    IPersonalKnowledgeGraph,
    IPhysicalActuator,
    ISelfImprovementLoop,
    ISkillAcquisition,
    IVoiceIdentity,
    KnowledgeNode,
    KnowledgeRelation,
    LongHorizonGoal,
    PhysicalCommand,
    PhysicalCommandResult,
    SelfImprovementVerdict,
    WorldFact,
)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# .NET DateTimeOffset.ToString("O") — round-trip ISO-8601 with 7 fractional
# digits and an explicit offset. UTC renders as "+00:00".
def _dto_round_trip(dt: datetime) -> str:
    """Render a datetime the way .NET's ``DateTimeOffset.ToString("O")`` does.

    Format: ``yyyy-MM-ddTHH:mm:ss.fffffffK`` with a 7-digit fractional second and
    a ``±HH:mm`` offset (UTC => ``+00:00``). Used by GoalPursuer's plan JSON and
    CryptoDelegation's canonical payload so cross-language wire formats agree.
    """
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    off = dt.utcoffset() or timedelta(0)
    total = int(off.total_seconds())
    sign = "+" if total >= 0 else "-"
    total = abs(total)
    oh, om = divmod(total // 60, 60)
    # 100-ns ticks: microseconds * 10, zero-padded to 7 digits.
    ticks = dt.microsecond * 10
    return (
        f"{dt.year:04d}-{dt.month:02d}-{dt.day:02d}"
        f"T{dt.hour:02d}:{dt.minute:02d}:{dt.second:02d}.{ticks:07d}"
        f"{sign}{oh:02d}:{om:02d}"
    )


def _new_id() -> str:
    """32-char lowercase hex — matches C#'s ``Guid.NewGuid().ToString("n")``."""
    return uuid.uuid4().hex


# =====================================================================
# 1. AlwaysOnPresence — asyncio heartbeat with start/stop.
# =====================================================================
class HeartbeatAlwaysOnPresence(IAlwaysOnPresence):
    """Heartbeat presence loop with idempotent start/stop.

    Mirrors ``CircleAI.Companion.HerJarvis.HeartbeatAlwaysOnPresence``.

    The C# reference uses a ``System.Threading.Timer``; this port uses an asyncio
    background task that increments a tick counter every ``heartbeat_interval``.
    ``heartbeats`` exposes the tick count (as in the C# ``Heartbeats`` property).
    """

    __slots__ = ("_interval", "_task", "_ticks", "_lock")

    def __init__(self, heartbeat_interval: Optional[timedelta] = None) -> None:
        self._interval = heartbeat_interval or timedelta(seconds=30)
        self._task: Optional[asyncio.Task] = None
        self._ticks = 0
        self._lock = threading.Lock()

    @property
    def is_running(self) -> bool:
        return self._task is not None

    @property
    def heartbeats(self) -> int:
        with self._lock:
            return self._ticks

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        if self._task is not None:
            return
        seconds = self._interval.total_seconds()

        async def _loop() -> None:
            # First tick fires immediately (TimeSpan.Zero due-time in C#).
            try:
                while True:
                    with self._lock:
                        self._ticks += 1
                    await asyncio.sleep(seconds)
            except asyncio.CancelledError:
                return

        self._task = asyncio.ensure_future(_loop())

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        task = self._task
        self._task = None
        if task is not None:
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                pass


# =====================================================================
# 2. FusedPerception — asyncio.Queue pub/sub with publish hook.
# =====================================================================
class ChannelFusedPerception(IFusedPerception):
    """Channel-based fused-perception pub/sub.

    Mirrors ``CircleAI.Companion.HerJarvis.ChannelFusedPerception``. ``publish``
    pushes a percept; ``complete`` closes the stream; ``stream_async`` yields
    published percepts until completion.
    """

    __slots__ = ("_queue", "_complete")

    _SENTINEL = object()

    def __init__(self) -> None:
        self._queue: "asyncio.Queue[object]" = asyncio.Queue()
        self._complete = False

    def publish(self, p: FusedPercept) -> None:
        if p is None:
            raise ValueError("p required")
        self._queue.put_nowait(p)

    def complete(self) -> None:
        if not self._complete:
            self._complete = True
            self._queue.put_nowait(self._SENTINEL)

    async def stream_async(self, *, ct: Optional[object] = None):
        while True:
            item = await self._queue.get()
            if item is self._SENTINEL:
                return
            yield item  # type: ignore[misc]


# =====================================================================
# 3. IdentitySync — append-only delta log with monotonic cursor.
# =====================================================================
class JsonIdentitySync(IIdentitySync):
    """Append-only delta log with a monotonic cursor.

    Mirrors ``CircleAI.Companion.HerJarvis.JsonIdentitySync``. ``pull_async``
    returns a ``{"cursor":N,"deltas":[...]}`` envelope where each delta is spliced
    in verbatim (the deltas are assumed to be raw JSON), exactly as the C#
    ``StringBuilder`` assembles it.
    """

    __slots__ = ("_log", "_next", "_lock")

    def __init__(self) -> None:
        self._log: List[Tuple[int, str]] = []
        self._next = 0
        self._lock = threading.Lock()

    async def push_async(self, delta_json: str, *, ct: Optional[object] = None) -> None:
        if delta_json is None:
            raise ValueError("delta_json required")
        with self._lock:
            self._next += 1
            self._log.append((self._next, delta_json))

    async def pull_async(self, since_cursor: str, *, ct: Optional[object] = None) -> str:
        try:
            since = int(since_cursor)
        except (TypeError, ValueError):
            since = 0
        with self._lock:
            taken = [e for e in self._log if e[0] > since]
            max_cursor = since if len(taken) == 0 else taken[-1][0]
            deltas = [e[1] for e in taken]
        parts = ['{"cursor":', str(max_cursor), ',"deltas":[']
        for i, d in enumerate(deltas):
            if i > 0:
                parts.append(",")
            parts.append(d)
        parts.append("]}")
        return "".join(parts)


# =====================================================================
# 4. ContinuousLearner — exponentially-weighted average reward per id.
# =====================================================================
class EwaContinuousLearner(IContinuousLearner):
    """Exponentially-weighted average reward per interaction id.

    Mirrors ``CircleAI.Companion.HerJarvis.EwaContinuousLearner``.
    """

    __slots__ = ("_state", "_alpha", "_lock")

    def __init__(self, alpha: float = 0.2) -> None:
        if alpha <= 0 or alpha > 1:
            raise ValueError("alpha must be in (0, 1]")
        # id -> (avg, weight)
        self._state: Dict[str, Tuple[float, float]] = {}
        self._alpha = alpha
        self._lock = threading.Lock()

    async def register_feedback_async(
        self,
        interaction_id: str,
        reward: float,
        context_json: str,
        *,
        ct: Optional[object] = None,
    ) -> None:
        if interaction_id is None or len(interaction_id.strip()) == 0:
            raise ValueError("interaction_id required")
        with self._lock:
            prev = self._state.get(interaction_id)
            if prev is None:
                self._state[interaction_id] = (reward, 1.0)
            else:
                avg = prev[0] * (1 - self._alpha) + reward * self._alpha
                self._state[interaction_id] = (avg, prev[1] + 1)

    def average_reward_of(self, interaction_id: str) -> Optional[float]:
        with self._lock:
            s = self._state.get(interaction_id)
            return s[0] if s is not None else None

    def observations_of(self, interaction_id: str) -> int:
        with self._lock:
            s = self._state.get(interaction_id)
            return int(s[1]) if s is not None else 0


# =====================================================================
# 6. GoalPursuer — store goal + milestones; replan recalculates plan.
# =====================================================================
class InMemoryGoalPursuer(IGoalPursuer):
    """In-memory long-horizon goal pursuer.

    Mirrors ``CircleAI.Companion.HerJarvis.InMemoryGoalPursuer``. The plan JSON is
    assembled to match the C# ``BuildPlan`` byte-for-byte: the description is
    rendered with ``json.dumps`` (STJ default escaping of a bare string agrees
    with Python's for the strings this port is exercised with) and each milestone
    due-date uses .NET round-trip ("O") formatting.

    A ``now_provider`` seam pins the clock for deterministic tests; it defaults to
    ``datetime.now(timezone.utc)`` (the C# ``DateTimeOffset.UtcNow``).
    """

    __slots__ = ("_goals", "_lock", "_now")

    def __init__(self, *, now_provider: Optional[Callable[[], datetime]] = None) -> None:
        self._goals: Dict[str, LongHorizonGoal] = {}
        self._lock = threading.Lock()
        self._now: Callable[[], datetime] = now_provider or _utc_now

    async def register_async(
        self, description: str, deadline_utc: datetime, *, ct: Optional[object] = None
    ) -> LongHorizonGoal:
        if description is None or len(description.strip()) == 0:
            raise ValueError("description required")
        gid = _new_id()
        now = self._now()
        if deadline_utc <= now:
            raise ValueError("deadline must be in the future")
        plan = self._build_plan(description, now, deadline_utc)
        g = LongHorizonGoal(gid, description, deadline_utc, plan, 0.0)
        with self._lock:
            self._goals[gid] = g
        return g

    async def current_async(
        self, id: str, *, ct: Optional[object] = None
    ) -> Optional[LongHorizonGoal]:
        with self._lock:
            return self._goals.get(id)

    async def replan_async(self, id: str, *, ct: Optional[object] = None) -> None:
        with self._lock:
            g = self._goals.get(id)
            if g is None:
                raise RuntimeError(f"Unknown goal {id}")
            plan = self._build_plan(g.description, self._now(), g.deadline_utc)
            self._goals[id] = LongHorizonGoal(
                g.id, g.description, g.deadline_utc, plan, g.progress_fraction
            )

    def progress(self, id: str, fraction: float) -> None:
        """Set a goal's progress fraction (mirrors C# ``Progress``)."""
        if fraction < 0 or fraction > 1:
            raise ValueError("fraction must be in [0, 1]")
        with self._lock:
            g = self._goals.get(id)
            if g is None:
                raise RuntimeError(f"Unknown goal {id}")
            self._goals[id] = LongHorizonGoal(
                g.id, g.description, g.deadline_utc, g.plan_json, fraction
            )

    @staticmethod
    def _build_plan(description: str, now: datetime, deadline_utc: datetime) -> str:
        total_days = max(1, int((deadline_utc - now).total_seconds() // 86400))
        milestones = min(8, max(2, total_days // 14))
        span = (deadline_utc - now) / milestones
        # description via JsonSerializer.Serialize(string) — quoted + escaped.
        parts = ['{"description":', json.dumps(description, ensure_ascii=False), ',"milestones":[']
        for i in range(1, milestones + 1):
            if i > 1:
                parts.append(",")
            due = now + span * i
            parts.append('{"index":')
            parts.append(str(i))
            parts.append(',"due":"')
            parts.append(_dto_round_trip(due))
            parts.append('"}')
        parts.append("]}")
        return "".join(parts)


# =====================================================================
# 7. EpisodicMemory — term-frequency similarity recall.
# =====================================================================
class TfEpisodicMemory(IEpisodicMemory):
    """Term-frequency similarity episodic memory.

    Mirrors ``CircleAI.Companion.HerJarvis.TfEpisodicMemory``. Recall scores each
    episode by the dot product of query-term and document-term frequencies, keeps
    positive-scoring hits, orders by descending score, and takes the top ``take``.
    """

    __slots__ = ("_episodes", "_terms", "_lock")

    _TOKEN_RX = re.compile(r"[^A-Za-z0-9]+")

    def __init__(self) -> None:
        self._episodes: Dict[str, EpisodeRecord] = {}
        self._terms: Dict[str, Dict[str, int]] = {}
        self._lock = threading.Lock()

    async def record_async(
        self, episode: EpisodeRecord, *, ct: Optional[object] = None
    ) -> None:
        if episode is None:
            raise ValueError("episode required")
        if episode.id is None or len(episode.id.strip()) == 0:
            raise ValueError("Id required")
        with self._lock:
            self._episodes[episode.id] = episode
            self._terms[episode.id] = self._to_term_frequency(
                episode.title + " " + episode.content_json
            )

    async def recall_async(
        self, query: str, take: int = 10, *, ct: Optional[object] = None
    ) -> Sequence[EpisodeRecord]:
        if query is None:
            raise ValueError("query required")
        if take <= 0:
            raise ValueError("take must be > 0")
        q_terms = self._to_term_frequency(query)
        if len(q_terms) == 0:
            return []
        with self._lock:
            scored: List[Tuple[EpisodeRecord, float]] = []
            for e in self._episodes.values():
                d = self._terms.get(e.id)
                s = self._score(q_terms, d)
                if s > 0:
                    scored.append((e, s))
            scored.sort(key=lambda x: x[1], reverse=True)
            return [e for e, _ in scored[:take]]

    @classmethod
    def _to_term_frequency(cls, text: str) -> Dict[str, int]:
        # Case-insensitive term counts; key by lowercased term (OrdinalIgnoreCase).
        d: Dict[str, int] = {}
        for t in cls._TOKEN_RX.split(text or ""):
            if len(t) >= 2:
                lk = t.lower()
                d[lk] = d.get(lk, 0) + 1
        return d

    @staticmethod
    def _score(q: Dict[str, int], d: Optional[Dict[str, int]]) -> float:
        if d is None:
            return 0.0
        s = 0.0
        for k, v in q.items():
            n = d.get(k)
            if n is not None:
                s += v * n
        return s


# =====================================================================
# 8. VoiceIdentity — mean-MFCC fingerprint + cosine similarity.
#
# Standard speech pipeline: pre-emphasis -> 25ms frames / 10ms hop -> Hamming
# window -> DFT power spectrum -> 26 mel filters -> log -> DCT-II -> 13 cepstral
# coefficients -> mean across frames = fingerprint. Cosine similarity > 0.85
# identifies. Ported arithmetic-faithfully from the C# reference. PCM16 decode
# uses ``struct`` at the C# float site (``s / 32768f``).
# =====================================================================
class EnergyBandVoiceIdentity(IVoiceIdentity):
    """Mean-MFCC speaker fingerprint with cosine-similarity matching.

    Mirrors ``CircleAI.Companion.HerJarvis.EnergyBandVoiceIdentity``.
    """

    __slots__ = ("_enrolled", "_lock")

    _NUM_COEFFICIENTS = 13
    _NUM_MEL_FILTERS = 26
    _FRAME_SIZE = 400  # 25ms @ 16kHz
    _FRAME_STEP = 160  # 10ms @ 16kHz
    _PRE_EMPHASIS = 0.97

    def __init__(self) -> None:
        self._enrolled: Dict[str, List[List[float]]] = {}
        self._lock = threading.Lock()

    async def enroll_async(
        self,
        user_id: str,
        audio_pcm16: bytes,
        sample_rate_hz: int,
        *,
        ct: Optional[object] = None,
    ) -> None:
        if user_id is None or len(user_id.strip()) == 0:
            raise ValueError("user_id required")
        fp = self._mfcc(audio_pcm16, sample_rate_hz)
        with self._lock:
            self._enrolled.setdefault(user_id, []).append(fp)

    async def identify_async(
        self, audio_pcm16: bytes, sample_rate_hz: int, *, ct: Optional[object] = None
    ) -> Optional[str]:
        fp = self._mfcc(audio_pcm16, sample_rate_hz)
        best: Optional[str] = None
        best_sim = -1.0
        with self._lock:
            for user_id, refs in self._enrolled.items():
                for reference in refs:
                    sim = self._cosine_similarity(fp, reference)
                    if sim > best_sim:
                        best_sim = sim
                        best = user_id
        return best if best_sim > 0.85 else None

    # ── MFCC pipeline ────────────────────────────────────────────────────

    @classmethod
    def _mfcc(cls, pcm16: bytes, sample_rate_hz: int) -> List[float]:
        samples = cls._decode_pcm16(pcm16)
        if len(samples) < cls._FRAME_SIZE:
            return [0.0] * cls._NUM_COEFFICIENTS
        cls._pre_emphasis_filter(samples)
        filters = cls._mel_filterbank(cls._NUM_MEL_FILTERS, cls._FRAME_SIZE, sample_rate_hz)

        total = [0.0] * cls._NUM_COEFFICIENTS
        count = 0
        window = cls._hamming_window(cls._FRAME_SIZE)
        start = 0
        n_samples = len(samples)
        while start + cls._FRAME_SIZE <= n_samples:
            frame = [samples[start + i] * window[i] for i in range(cls._FRAME_SIZE)]
            power_spec = cls._power_spectrum(frame)
            mel_energies = cls._apply_filterbank(power_spec, filters)
            log_energies = [math.log(max(1e-10, mel_energies[i])) for i in range(cls._NUM_MEL_FILTERS)]
            coeffs = cls._dct(log_energies, cls._NUM_COEFFICIENTS)
            for i in range(cls._NUM_COEFFICIENTS):
                total[i] += coeffs[i]
            count += 1
            start += cls._FRAME_STEP
        if count == 0:
            return total
        return [x / count for x in total]

    @staticmethod
    def _decode_pcm16(pcm16: bytes) -> List[float]:
        n = len(pcm16) // 2
        samples: List[float] = []
        for i in range(n):
            # little-endian signed 16-bit, then s / 32768f (float division).
            (s,) = struct.unpack_from("<h", pcm16, i * 2)
            samples.append(s / 32768.0)
        return samples

    @classmethod
    def _pre_emphasis_filter(cls, samples: List[float]) -> None:
        for i in range(len(samples) - 1, 0, -1):
            samples[i] -= cls._PRE_EMPHASIS * samples[i - 1]

    @staticmethod
    def _hamming_window(n: int) -> List[float]:
        return [0.54 - 0.46 * math.cos(2 * math.pi * i / (n - 1)) for i in range(n)]

    @staticmethod
    def _power_spectrum(frame: List[float]) -> List[float]:
        n = len(frame)
        half = n // 2 + 1
        spec = [0.0] * half
        for k in range(half):
            re = 0.0
            im = 0.0
            omega = -2.0 * math.pi * k / n
            for t in range(n):
                re += frame[t] * math.cos(omega * t)
                im += frame[t] * math.sin(omega * t)
            spec[k] = re * re + im * im
        return spec

    @staticmethod
    def _mel_filterbank(num_filters: int, frame_size: int, sample_rate_hz: int) -> List[List[float]]:
        def hz_to_mel(hz: float) -> float:
            return 2595 * math.log10(1 + hz / 700.0)

        def mel_to_hz(mel: float) -> float:
            return 700 * (math.pow(10, mel / 2595) - 1)

        low_mel = hz_to_mel(0)
        high_mel = hz_to_mel(sample_rate_hz / 2.0)
        mel_points = [
            low_mel + (high_mel - low_mel) * i / (num_filters + 2 - 1)
            for i in range(num_filters + 2)
        ]
        bin_points = [
            int(math.floor((frame_size + 1) * mel_to_hz(mel_points[i]) / sample_rate_hz))
            for i in range(num_filters + 2)
        ]

        half = frame_size // 2 + 1
        filters: List[List[float]] = []
        for m in range(num_filters):
            row = [0.0] * half
            left = bin_points[m]
            centre = bin_points[m + 1]
            right = bin_points[m + 2]
            k = left
            while k < centre and k < half:
                if centre != left:
                    row[k] = (k - left) / (centre - left)
                k += 1
            k = centre
            while k < right and k < half:
                if right != centre:
                    row[k] = (right - k) / (right - centre)
                k += 1
            filters.append(row)
        return filters

    @staticmethod
    def _apply_filterbank(power_spec: List[float], filters: List[List[float]]) -> List[float]:
        energies = [0.0] * len(filters)
        for m in range(len(filters)):
            s = 0.0
            filt = filters[m]
            length = min(len(power_spec), len(filt))
            for k in range(length):
                s += power_spec[k] * filt[k]
            energies[m] = s
        return energies

    @staticmethod
    def _dct(input_vec: List[float], num_coeffs: int) -> List[float]:
        n = len(input_vec)
        output = [0.0] * num_coeffs
        for k in range(num_coeffs):
            s = 0.0
            for i in range(n):
                s += input_vec[i] * math.cos(math.pi * k * (i + 0.5) / n)
            output[k] = s
        return output

    @staticmethod
    def _cosine_similarity(a: List[float], b: List[float]) -> float:
        dot = 0.0
        na = 0.0
        nb = 0.0
        for i in range(len(a)):
            dot += a[i] * b[i]
            na += a[i] * a[i]
            nb += b[i] * b[i]
        if na == 0 or nb == 0:
            return 0.0
        return dot / (math.sqrt(na) * math.sqrt(nb))


# =====================================================================
# 9. CalibratedConfidence — nearest-neighbour calibration over history.
# =====================================================================
class HistoricalCalibratedConfidence(ICalibratedConfidence):
    """History-calibrated confidence band.

    Mirrors ``CircleAI.Companion.HerJarvis.HistoricalCalibratedConfidence``. With
    fewer than 5 recorded outcomes the raw score passes through; otherwise the
    calibrated point is the correct-fraction of the 5 nearest-by-raw-score
    outcomes. The half-band shrinks as confidence rises.
    """

    __slots__ = ("_history", "_lock")

    _HEDGE_RX = re.compile(r"\b(maybe|perhaps|might|possibly|unclear|don't know)\b", re.IGNORECASE)

    def __init__(self) -> None:
        # (raw_score, was_correct)
        self._history: List[Tuple[float, bool]] = []
        self._lock = threading.Lock()

    def record_outcome(self, raw_score: float, was_correct: bool) -> None:
        with self._lock:
            self._history.append((min(1.0, max(0.0, raw_score)), was_correct))

    async def evaluate_async(
        self, answer: str, context_json: str, *, ct: Optional[object] = None
    ) -> ConfidenceBand:
        if answer is None:
            raise ValueError("answer required")
        raw = self._compute_raw_score(answer, context_json)
        with self._lock:
            if len(self._history) < 5:
                calibrated = raw
            else:
                nearby = sorted(self._history, key=lambda h: abs(h[0] - raw))[:5]
                calibrated = sum(1 for h in nearby if h[1]) / len(nearby)
        half_band = max(0.05, 0.25 - calibrated * 0.2)
        return ConfidenceBand(
            max(0.0, calibrated - half_band),
            min(1.0, calibrated + half_band),
        )

    @classmethod
    def _compute_raw_score(cls, answer: str, context_json: str) -> float:
        length = max(1, len(answer.strip()))
        hedges = len(cls._HEDGE_RX.findall(answer))
        hedge_penalty = min(0.5, hedges * 0.1)
        has_context = context_json is not None and len(context_json.strip()) > 0 and len(context_json) > 2
        val = (math.log(length) / 10.0) + (0.1 if has_context else 0.0) - hedge_penalty
        return min(1.0, max(0.0, val))


# =====================================================================
# 11. EmotionSensor — keyword arousal/valence inference from fused JSON.
# =====================================================================
class KeywordEmotionSensor(IEmotionSensor):
    """Keyword-driven emotion sensor.

    Mirrors ``CircleAI.Companion.HerJarvis.KeywordEmotionSensor``. Counts keyword
    hits per emotion pattern, weights arousal/valence by hit count, and reports
    the highest-count label; no hits => ``neutral`` (0, 0).
    """

    _PATTERNS: Sequence[Tuple[str, float, float, "re.Pattern[str]"]] = (
        ("joy", 0.8, 0.9, re.compile(r"\b(happy|joy|delight|excited|love|wonderful)\b", re.IGNORECASE)),
        ("anger", 0.9, -0.8, re.compile(r"\b(angry|furious|rage|hate|annoyed)\b", re.IGNORECASE)),
        ("sad", 0.3, -0.7, re.compile(r"\b(sad|lonely|grief|cry|depressed|down)\b", re.IGNORECASE)),
        ("fear", 0.85, -0.6, re.compile(r"\b(afraid|scared|terrified|anxious|worried)\b", re.IGNORECASE)),
        ("surprise", 0.7, 0.3, re.compile(r"\b(surprised|amazed|astonished|wow)\b", re.IGNORECASE)),
        ("calm", 0.1, 0.5, re.compile(r"\b(calm|peaceful|relaxed|content|fine)\b", re.IGNORECASE)),
    )

    async def sense_async(
        self, fused_json: str, *, ct: Optional[object] = None
    ) -> EmotionFrame:
        if fused_json is None:
            raise ValueError("fused_json required")
        hits = [
            (label, arousal, valence, len(rx.findall(fused_json)))
            for (label, arousal, valence, rx) in self._PATTERNS
        ]
        hits = [h for h in hits if h[3] > 0]
        if len(hits) == 0:
            return EmotionFrame("neutral", 0.0, 0.0)
        total_weight = sum(h[3] for h in hits)
        arousal = sum(h[1] * h[3] for h in hits) / total_weight
        valence = sum(h[2] * h[3] for h in hits) / total_weight
        top = max(hits, key=lambda h: h[3])[0]
        return EmotionFrame(top, arousal, valence)


# =====================================================================
# 12. SkillAcquisition — demo store with name extraction.
# =====================================================================
class DemoStoreSkillAcquisition(ISkillAcquisition):
    """Demonstration-store skill acquisition.

    Mirrors ``CircleAI.Companion.HerJarvis.DemoStoreSkillAcquisition``. Extracts a
    ``name`` from the demonstration JSON when present, else ``skill-<first6>`` of
    the generated id; lists skills ordered by name.
    """

    __slots__ = ("_skills", "_lock")

    def __init__(self) -> None:
        self._skills: Dict[str, AcquiredSkill] = {}
        self._lock = threading.Lock()

    async def acquire_async(
        self, demonstration_json: str, *, ct: Optional[object] = None
    ) -> AcquiredSkill:
        if demonstration_json is None:
            raise ValueError("demonstration_json required")
        sid = _new_id()
        name = self._extract_name(demonstration_json) or ("skill-" + sid[:6])
        skill = AcquiredSkill(sid, name, demonstration_json)
        with self._lock:
            self._skills[sid] = skill
        return skill

    async def list_async(self, *, ct: Optional[object] = None) -> Sequence[AcquiredSkill]:
        with self._lock:
            return sorted(self._skills.values(), key=lambda s: s.name)

    @staticmethod
    def _extract_name(demonstration_json: str) -> Optional[str]:
        try:
            doc = json.loads(demonstration_json)
        except ValueError:
            return None
        if isinstance(doc, dict):
            n = doc.get("name")
            if isinstance(n, str):
                return n
        return None


# =====================================================================
# 15. PersonalKnowledgeGraph — adjacency-list graph with relation kinds.
# =====================================================================
class AdjacencyPersonalKnowledgeGraph(IPersonalKnowledgeGraph):
    """Adjacency-list personal knowledge graph.

    Mirrors ``CircleAI.Companion.HerJarvis.AdjacencyPersonalKnowledgeGraph``.
    Relations are deduped on (to-id, relation); ``neighbours_async`` resolves
    out-edges to their target nodes, skipping dangling targets.
    """

    __slots__ = ("_nodes", "_out_edges", "_lock")

    def __init__(self) -> None:
        self._nodes: Dict[str, KnowledgeNode] = {}
        self._out_edges: Dict[str, List[KnowledgeRelation]] = {}
        self._lock = threading.Lock()

    async def upsert_node_async(
        self, node: KnowledgeNode, *, ct: Optional[object] = None
    ) -> None:
        if node is None:
            raise ValueError("node required")
        if node.id is None or len(node.id.strip()) == 0:
            raise ValueError("Id required")
        with self._lock:
            self._nodes[node.id] = node

    async def upsert_relation_async(
        self, rel: KnowledgeRelation, *, ct: Optional[object] = None
    ) -> None:
        if rel is None:
            raise ValueError("rel required")
        with self._lock:
            lst = self._out_edges.setdefault(rel.from_id, [])
            lst[:] = [r for r in lst if not (r.to_id == rel.to_id and r.relation == rel.relation)]
            lst.append(rel)

    async def neighbours_async(
        self, id: str, *, ct: Optional[object] = None
    ) -> Sequence[KnowledgeNode]:
        if id is None or len(id.strip()) == 0:
            raise ValueError("id required")
        with self._lock:
            rels = self._out_edges.get(id)
            if rels is None:
                return []
            out: List[KnowledgeNode] = []
            for r in rels:
                n = self._nodes.get(r.to_id)
                if n is not None:
                    out.append(n)
            return out


# =====================================================================
# 16. LiveWorldKnowledge — topic pub/sub broker.
# =====================================================================
class TopicLiveWorldKnowledge(ILiveWorldKnowledge):
    """Topic pub/sub live-world-knowledge broker.

    Mirrors ``CircleAI.Companion.HerJarvis.TopicLiveWorldKnowledge``. ``publish``
    delivers a fact to the queues of subscribers of the matching topic;
    ``subscribe_async`` yields facts across all requested topics, polling with a
    short delay (as the C# loop does) until cancelled.
    """

    __slots__ = ("_by_topic", "_lock")

    def __init__(self) -> None:
        self._by_topic: Dict[str, "asyncio.Queue[WorldFact]"] = {}
        self._lock = threading.Lock()

    def _topic_queue(self, topic: str) -> "asyncio.Queue[WorldFact]":
        with self._lock:
            q = self._by_topic.get(topic)
            if q is None:
                q = asyncio.Queue()
                self._by_topic[topic] = q
            return q

    def publish(self, fact: WorldFact) -> None:
        if fact is None:
            raise ValueError("fact required")
        with self._lock:
            q = self._by_topic.get(fact.topic)
        if q is not None:
            q.put_nowait(fact)

    async def subscribe_async(
        self, topics: Sequence[str], *, ct: Optional[object] = None
    ):
        if topics is None:
            raise ValueError("topics required")
        queues = [self._topic_queue(t) for t in topics]
        try:
            while True:
                for q in queues:
                    while not q.empty():
                        yield q.get_nowait()
                await asyncio.sleep(0.05)
        except asyncio.CancelledError:
            return


# =====================================================================
# 17. BioSignalStream — asyncio.Queue fan-in with publish hook.
# =====================================================================
class ChannelBioSignalStream(IBioSignalStream):
    """Channel-based bio-signal fan-in.

    Mirrors ``CircleAI.Companion.HerJarvis.ChannelBioSignalStream``.
    """

    __slots__ = ("_queue", "_complete")

    _SENTINEL = object()

    def __init__(self) -> None:
        self._queue: "asyncio.Queue[object]" = asyncio.Queue()
        self._complete = False

    def publish(self, s: BioSignal) -> None:
        if s is None:
            raise ValueError("s required")
        self._queue.put_nowait(s)

    def complete(self) -> None:
        if not self._complete:
            self._complete = True
            self._queue.put_nowait(self._SENTINEL)

    async def stream_async(self, *, ct: Optional[object] = None):
        while True:
            item = await self._queue.get()
            if item is self._SENTINEL:
                return
            yield item  # type: ignore[misc]


# =====================================================================
# 18. PhysicalActuator — device-handler registry with per-action dispatch.
# =====================================================================
DeviceHandler = Callable[[PhysicalCommand, Optional[object]], Awaitable[PhysicalCommandResult]]


class RegistryPhysicalActuator(IPhysicalActuator):
    """Device-handler registry physical actuator.

    Mirrors ``CircleAI.Companion.HerJarvis.RegistryPhysicalActuator``. Unknown
    devices return a failure result (not an exception), matching the C# reference.
    """

    __slots__ = ("_handlers", "_lock")

    def __init__(self) -> None:
        self._handlers: Dict[str, DeviceHandler] = {}
        self._lock = threading.Lock()

    def register_device(self, device_id: str, handler: DeviceHandler) -> None:
        if device_id is None or len(device_id.strip()) == 0:
            raise ValueError("device_id required")
        if handler is None:
            raise ValueError("handler required")
        with self._lock:
            self._handlers[device_id] = handler

    async def invoke_async(
        self, command: PhysicalCommand, *, ct: Optional[object] = None
    ) -> PhysicalCommandResult:
        if command is None:
            raise ValueError("command required")
        with self._lock:
            h = self._handlers.get(command.device_id)
        if h is None:
            return PhysicalCommandResult(False, f"Unknown device '{command.device_id}'")
        return await h(command, ct)


# =====================================================================
# 19. AgentPeerNetwork — in-memory mailbox per agent id.
# =====================================================================
class MailboxAgentPeerNetwork(IAgentPeerNetwork):
    """In-memory per-agent mailbox network.

    Mirrors ``CircleAI.Companion.HerJarvis.MailboxAgentPeerNetwork``.
    """

    __slots__ = ("_mailboxes", "_lock")

    def __init__(self) -> None:
        self._mailboxes: Dict[str, "asyncio.Queue[AgentToAgentMessage]"] = {}
        self._lock = threading.Lock()

    def _mailbox(self, agent_id: str) -> "asyncio.Queue[AgentToAgentMessage]":
        with self._lock:
            box = self._mailboxes.get(agent_id)
            if box is None:
                box = asyncio.Queue()
                self._mailboxes[agent_id] = box
            return box

    async def send_async(
        self, message: AgentToAgentMessage, *, ct: Optional[object] = None
    ) -> None:
        if message is None:
            raise ValueError("message required")
        self._mailbox(message.to_agent_id).put_nowait(message)

    async def receive_async(
        self, for_agent_id: str, *, ct: Optional[object] = None
    ):
        if for_agent_id is None or len(for_agent_id.strip()) == 0:
            raise ValueError("for_agent_id required")
        box = self._mailbox(for_agent_id)
        while True:
            msg = await box.get()
            yield msg


# =====================================================================
# 20. FederatedFineTuner — job runner with status tracking.
# =====================================================================
Trainer = Callable[[str, str, "Callable[[float], None]", Optional[object]], Awaitable[None]]


class InMemoryFederatedFineTuner(IFederatedFineTuner):
    """In-memory federated fine-tuner with async job tracking.

    Mirrors ``CircleAI.Companion.HerJarvis.InMemoryFederatedFineTuner``. The
    trainer is an injectable coroutine that reports progress via a callback; the
    default reads the training file line-count (or 100 if absent) and steps
    progress to 1.0. Unknown job ids report ``"unknown job"``.
    """

    __slots__ = ("_jobs", "_trainer", "_lock", "_tasks")

    def __init__(self, trainer: Optional[Trainer] = None) -> None:
        self._jobs: Dict[str, FineTuneJobStatus] = {}
        self._trainer: Trainer = trainer or self._default_trainer
        self._lock = threading.Lock()
        self._tasks: List[asyncio.Task] = []

    async def start_async(
        self, base_model: str, training_data_path: str, *, ct: Optional[object] = None
    ) -> str:
        if base_model is None or len(base_model.strip()) == 0:
            raise ValueError("base_model required")
        if training_data_path is None or len(training_data_path.strip()) == 0:
            raise ValueError("training_data_path required")
        job_id = _new_id()
        with self._lock:
            self._jobs[job_id] = FineTuneJobStatus(job_id, 0.0, None)

        def report(p: float) -> None:
            with self._lock:
                cur = self._jobs[job_id]
                self._jobs[job_id] = FineTuneJobStatus(job_id, min(1.0, max(0.0, p)), cur.error)

        async def _run() -> None:
            try:
                await self._trainer(base_model, training_data_path, report, ct)
                with self._lock:
                    self._jobs[job_id] = FineTuneJobStatus(job_id, 1.0, None)
            except Exception as ex:  # noqa: BLE001 — matches C#: capture error
                with self._lock:
                    cur = self._jobs[job_id]
                    self._jobs[job_id] = FineTuneJobStatus(job_id, cur.progress, str(ex))

        self._tasks.append(asyncio.ensure_future(_run()))
        return job_id

    async def status_async(
        self, job_id: str, *, ct: Optional[object] = None
    ) -> FineTuneJobStatus:
        with self._lock:
            s = self._jobs.get(job_id)
        if s is None:
            return FineTuneJobStatus(job_id, 0.0, "unknown job")
        return s

    @staticmethod
    async def _default_trainer(
        base_model: str,
        path: str,
        report: "Callable[[float], None]",
        ct: Optional[object],
    ) -> None:
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                line_count = sum(1 for _ in f)
        except OSError:
            line_count = 100
        step = 1.0 / max(1, line_count)
        for i in range(line_count):
            report(i * step)
            await asyncio.sleep(0)
        report(1.0)


# =====================================================================
# 21. FirstTokenOptimizer — sliding-window p50 latency tracker.
# =====================================================================
class SlidingP50FirstTokenOptimizer(IFirstTokenOptimizer):
    """Sliding-window p50 first-token latency tracker.

    Mirrors ``CircleAI.Companion.HerJarvis.SlidingP50FirstTokenOptimizer``. The
    p50 is the ``sorted[len//2]`` element of the current window (upper-median for
    even counts, matching the C# indexing); empty window => 0.
    """

    __slots__ = ("_samples", "_window_size", "_target_ms", "_lock")

    def __init__(self, target_ms: int = 100, window_size: int = 256) -> None:
        if target_ms <= 0:
            raise ValueError("target_ms must be > 0")
        if window_size <= 0:
            raise ValueError("window_size must be > 0")
        self._samples: List[int] = []
        self._window_size = window_size
        self._target_ms = target_ms
        self._lock = threading.Lock()

    def record_first_token_latency(self, ms: int) -> None:
        if ms < 0:
            raise ValueError("ms must be >= 0")
        with self._lock:
            self._samples.append(ms)
            while len(self._samples) > self._window_size:
                self._samples.pop(0)

    async def current_async(self, *, ct: Optional[object] = None) -> FirstTokenBudget:
        with self._lock:
            if len(self._samples) == 0:
                p50 = 0
            else:
                ordered = sorted(self._samples)
                p50 = ordered[len(ordered) // 2]
        return FirstTokenBudget(self._target_ms, p50)


# =====================================================================
# 22. CryptoDelegation — HMAC-SHA256 sign+verify (ECDSA injectable).
#
# The C# reference signs with ECDSA P-256 over SHA-256 of a canonical payload
# string. Python's stdlib has no P-256 signing, so the default here is a real
# deterministic HMAC-SHA256 signer over the SAME canonical payload — a working,
# verifiable MAC, not a stub. A host that needs asymmetric ECDSA injects a
# ``signer`` object exposing ``sign(payload: bytes) -> bytes`` and
# ``verify(payload: bytes, sig: bytes) -> bool`` (e.g. a `cryptography`
# EC key wrapper) and gets byte-identical wire semantics to the C# credential.
# =====================================================================
class ISignatureProvider:
    """Injectable signer: ``sign(bytes)->bytes`` and ``verify(bytes, bytes)->bool``.

    The default ``HmacCryptoDelegation`` supplies an HMAC-SHA256 provider; hosts
    swap in an ECDSA P-256 provider to match the C# on the wire.
    """

    def sign(self, payload: bytes) -> bytes:  # pragma: no cover - interface
        raise NotImplementedError

    def verify(self, payload: bytes, signature: bytes) -> bool:  # pragma: no cover
        raise NotImplementedError


class _HmacSignatureProvider(ISignatureProvider):
    """Deterministic HMAC-SHA256 signer over a per-instance key."""

    __slots__ = ("_key",)

    def __init__(self, key: Optional[bytes] = None) -> None:
        self._key = key if key is not None else os.urandom(32)

    def sign(self, payload: bytes) -> bytes:
        return hmac.new(self._key, payload, hashlib.sha256).digest()

    def verify(self, payload: bytes, signature: bytes) -> bool:
        expected = hmac.new(self._key, payload, hashlib.sha256).digest()
        return hmac.compare_digest(expected, signature)


class HmacCryptoDelegation(ICryptoDelegation):
    """Signed delegation-credential issuer/verifier.

    Mirrors ``CircleAI.Companion.HerJarvis.EcdsaCryptoDelegation`` in structure and
    canonical payload; the signature primitive is HMAC-SHA256 by default with an
    injectable ``signer`` for asymmetric (ECDSA) hosts. The canonical payload is
    ``issuer|subjectId|scope|expiresAt("O")`` — identical to the C# ``Canonical``.
    """

    __slots__ = ("_issuer", "_signer", "_now")

    def __init__(
        self,
        issuer: str = "circleai-companion",
        signer: Optional[ISignatureProvider] = None,
        *,
        now_provider: Optional[Callable[[], datetime]] = None,
    ) -> None:
        if issuer is None or len(issuer.strip()) == 0:
            raise ValueError("issuer required")
        self._issuer = issuer
        self._signer: ISignatureProvider = signer or _HmacSignatureProvider()
        self._now: Callable[[], datetime] = now_provider or _utc_now

    def issue(self, subject_id: str, scope: str, lifetime: timedelta) -> DelegationCredential:
        if subject_id is None or len(subject_id.strip()) == 0:
            raise ValueError("subject_id required")
        if scope is None or len(scope.strip()) == 0:
            raise ValueError("scope required")
        if lifetime <= timedelta(0):
            raise ValueError("lifetime must be > 0")
        expires = self._now() + lifetime
        payload = self._canonical(subject_id, scope, expires)
        sig = self._signer.sign(payload.encode("utf-8"))
        return DelegationCredential(
            self._issuer, subject_id, scope, expires, base64.b64encode(sig).decode("ascii")
        )

    def verify(self, credential: DelegationCredential) -> bool:
        if credential is None:
            raise ValueError("credential required")
        if credential.issuer != self._issuer:
            return False
        if credential.expires_at_utc <= self._now():
            return False
        if credential.signature is None or len(credential.signature) == 0:
            return False
        try:
            sig = base64.b64decode(credential.signature, validate=True)
        except Exception:  # noqa: BLE001 — .NET FormatException analogue
            return False
        payload = self._canonical(credential.subject_id, credential.scope, credential.expires_at_utc)
        return self._signer.verify(payload.encode("utf-8"), sig)

    def _canonical(self, subject_id: str, scope: str, expires_at_utc: datetime) -> str:
        return f"{self._issuer}|{subject_id}|{scope}|{_dto_round_trip(expires_at_utc)}"


# =====================================================================
# 23. CodeGenerationLoop — balance-check + registered test runner.
# =====================================================================
Generator = Callable[[str, Optional[object]], Awaitable[str]]
TestRunner = Callable[[str, Optional[object]], Awaitable[bool]]
DeploymentHint = Callable[[str], Optional[str]]


class SyntaxCheckingCodeGenerationLoop(ICodeGenerationLoop):
    """Bracket-balance-checking code-generation loop.

    Mirrors ``CircleAI.Companion.HerJarvis.SyntaxCheckingCodeGenerationLoop``.
    Generator, test-runner, and deployment-hint are all injectable; defaults echo
    the prompt, treat balanced brackets as passing, and hint deployment.
    """

    __slots__ = ("_generator", "_test_runner", "_deployment_hint")

    def __init__(
        self,
        generator: Optional[Generator] = None,
        test_runner: Optional[TestRunner] = None,
        deployment_hint: Optional[DeploymentHint] = None,
    ) -> None:
        self._generator: Generator = generator or self._default_generator
        self._test_runner: TestRunner = test_runner or self._default_test_runner
        self._deployment_hint: DeploymentHint = deployment_hint or self._default_deployment_hint

    async def run_async(self, prompt: str, *, ct: Optional[object] = None) -> CodeGenJob:
        if prompt is None or len(prompt.strip()) == 0:
            raise ValueError("prompt required")
        job_id = _new_id()
        snippet = await self._generator(prompt, ct)
        parses = self._is_syntactically_balanced(snippet)
        tests_ok = parses and await self._test_runner(snippet, ct)
        return CodeGenJob(
            job_id, prompt, snippet, tests_ok, self._deployment_hint(snippet) if tests_ok else None
        )

    @staticmethod
    async def _default_generator(prompt: str, ct: Optional[object]) -> str:
        return f"// (3.3.0) generated from: {prompt.replace(chr(10), ' ')}\nreturn 0;"

    @classmethod
    async def _default_test_runner(cls, snippet: str, ct: Optional[object]) -> bool:
        return cls._is_syntactically_balanced(snippet)

    @staticmethod
    def _default_deployment_hint(snippet: str) -> Optional[str]:
        return "stage as nuget" if "public class" in snippet else "run inline"

    @staticmethod
    def _is_syntactically_balanced(snippet: str) -> bool:
        if snippet is None or len(snippet) == 0:
            return False
        curly = 0
        paren = 0
        square = 0
        for c in snippet:
            if c == "{":
                curly += 1
            elif c == "}":
                curly -= 1
            elif c == "(":
                paren += 1
            elif c == ")":
                paren -= 1
            elif c == "[":
                square += 1
            elif c == "]":
                square -= 1
            if curly < 0 or paren < 0 or square < 0:
                return False
        return curly == 0 and paren == 0 and square == 0


# =====================================================================
# 24. SelfImprovementLoop — bench-score tracking + improvement.
# =====================================================================
BenchRunner = Callable[[str, Optional[object]], Awaitable[float]]
ImprovementProposer = Callable[[str, float, Optional[object]], Awaitable[str]]


class TrackingSelfImprovementLoop(ISelfImprovementLoop):
    """Bench-score-tracking self-improvement loop.

    Mirrors ``CircleAI.Companion.HerJarvis.TrackingSelfImprovementLoop``. On a
    non-regressing run it records the best score ("new best" / "no regression");
    on a regression it asks the injectable proposer for an improvement. Both the
    bench runner and proposer are injectable with deterministic defaults.
    """

    __slots__ = ("_best_scores", "_run_bench", "_propose_improvement", "_lock")

    def __init__(
        self,
        run_bench: Optional[BenchRunner] = None,
        propose_improvement: Optional[ImprovementProposer] = None,
    ) -> None:
        self._best_scores: Dict[str, float] = {}
        self._run_bench: BenchRunner = run_bench or self._default_run_bench
        self._propose_improvement: ImprovementProposer = (
            propose_improvement or self._default_propose_improvement
        )
        self._lock = threading.Lock()

    async def cycle_async(
        self, bench_suite_id: str, *, ct: Optional[object] = None
    ) -> SelfImprovementVerdict:
        if bench_suite_id is None or len(bench_suite_id.strip()) == 0:
            raise ValueError("bench_suite_id required")
        with self._lock:
            baseline = self._best_scores.get(bench_suite_id, 0.0)
        current = await self._run_bench(bench_suite_id, ct)
        if current >= baseline:
            with self._lock:
                self._best_scores[bench_suite_id] = current
            applied = "new best" if current > baseline else "no regression"
        else:
            applied = await self._propose_improvement(bench_suite_id, current, ct)
        return SelfImprovementVerdict(applied, current)

    def best_score_for(self, bench_suite_id: str) -> float:
        with self._lock:
            return self._best_scores.get(bench_suite_id, 0.0)

    @staticmethod
    async def _default_run_bench(bench_suite_id: str, ct: Optional[object]) -> float:
        # Deterministic pseudo-score in [0.5, 1.0] derived from a stable hash of
        # the id (stands in for the C# String.GetHashCode()-seeded default).
        h = int.from_bytes(hashlib.sha256(bench_suite_id.encode("utf-8")).digest()[:2], "big")
        return 0.5 + (h & 0xFFFF) / 65535.0 * 0.5

    @staticmethod
    async def _default_propose_improvement(
        bench_suite_id: str, current: float, ct: Optional[object]
    ) -> str:
        return f"retry-with-temperature-0 (score was {current:.3f})"


__all__ = [
    "HeartbeatAlwaysOnPresence",
    "ChannelFusedPerception",
    "JsonIdentitySync",
    "EwaContinuousLearner",
    "InMemoryGoalPursuer",
    "TfEpisodicMemory",
    "EnergyBandVoiceIdentity",
    "HistoricalCalibratedConfidence",
    "KeywordEmotionSensor",
    "DemoStoreSkillAcquisition",
    "AdjacencyPersonalKnowledgeGraph",
    "TopicLiveWorldKnowledge",
    "ChannelBioSignalStream",
    "RegistryPhysicalActuator",
    "MailboxAgentPeerNetwork",
    "InMemoryFederatedFineTuner",
    "SlidingP50FirstTokenOptimizer",
    "ISignatureProvider",
    "HmacCryptoDelegation",
    "SyntaxCheckingCodeGenerationLoop",
    "TrackingSelfImprovementLoop",
]
