# companion/world_model.py
#
# IWorldModel implementations. Ported from CircleAI.Companion — the C# reference:
#
#   * BayesianWorldModel  (BayesianWorldModel.cs)          — online Naive Bayes
#   * FrequencyWorldModel (HerJarvisRealImplementations.cs) — frequency tally
#
# BayesianWorldModel is a real (small but honest) probabilistic graphical model:
# an online-learning Naive Bayes classifier over (observations -> outcome) pairs.
# At predict time, for every previously-seen outcome:
#     P(outcome | obs) ~ P(outcome) * prod_i P(obs_i | outcome)
# using Laplace smoothing so unseen pairs don't zero out, then softmax over the
# log-posteriors for a normalised probability.
#
# FrequencyWorldModel is the simpler predecessor: it learns P(outcome|observation)
# as a raw frequency tally.
#
# Both extract observations from a scenario JSON object as ``name=value`` pairs.
# The ``value`` rendering matches System.Text.Json's ``JsonElement.ToString()``
# byte-for-byte (booleans render as ``True``/``False``, null as empty string,
# nested objects/arrays as compact JSON) — see ``_element_to_string``.

from __future__ import annotations

import json
import math
import threading
from typing import Iterable, List, Optional, Tuple

from .herjarvis_contracts import CausalPrediction, IWorldModel


# ── shared JSON observation extraction ────────────────────────────────────


def _element_to_string(value: object) -> str:
    """Render a parsed-JSON value the way System.Text.Json's
    ``JsonElement.ToString()`` does.

    * string  -> the unescaped value, no quotes
    * bool    -> ``True`` / ``False``  (.NET capitalisation — Python's ``str``)
    * None    -> empty string
    * number  -> its compact numeric text
    * object/array -> compact JSON (``{"x":1}`` / ``[1,2]``, no spaces)
    """
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, bool):
        # Python's str(True) == "True", matching .NET's Boolean.ToString().
        return str(value)
    if isinstance(value, (int, float)):
        return json.dumps(value)
    # object / array -> compact JSON with no whitespace (matches STJ output).
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


def _extract_observations(scenario_json: str) -> List[str]:
    """Parse a scenario JSON *object* into ``name=value`` observation strings.

    Returns an empty list when the JSON is not an object or fails to parse
    (JSON-syntax errors). A ``None`` input raises ``ValueError`` — this mirrors
    ``FrequencyWorldModel``, whose C# ``JsonDocument.Parse(null)`` throws
    ``ArgumentNullException`` (it catches only ``JsonException``). Callers that
    want the null-safe behaviour (``BayesianWorldModel``) guard for ``None``
    before calling.
    """
    if scenario_json is None:
        # Matches C# ArgumentNullException from JsonDocument.Parse(null).
        raise ValueError("scenario_json required")
    try:
        root = json.loads(scenario_json)
    except ValueError:  # JSON syntax error == C# JsonException -> empty
        return []
    if not isinstance(root, dict):
        return []
    return [f"{name}={_element_to_string(val)}" for name, val in root.items()]


# ── case-insensitive counting maps (StringComparer.OrdinalIgnoreCase) ──────


class _CiCounter:
    """A case-insensitive string->count map.

    Keys compare case-insensitively (like ``StringComparer.OrdinalIgnoreCase``);
    the first-seen original casing of each key is preserved for iteration, which
    is what a C# ``ConcurrentDictionary``/``Dictionary`` keyed by that comparer
    exposes via its ``Key``.
    """

    __slots__ = ("_map",)

    def __init__(self) -> None:
        # lower-key -> (original-key, count)
        self._map: dict[str, Tuple[str, int]] = {}

    def add(self, key: str, amount: int = 1) -> None:
        lk = key.lower()
        existing = self._map.get(lk)
        if existing is None:
            self._map[lk] = (key, amount)
        else:
            self._map[lk] = (existing[0], existing[1] + amount)

    def get(self, key: str) -> int:
        existing = self._map.get(key.lower())
        return existing[1] if existing is not None else 0

    def total(self) -> int:
        return sum(v[1] for v in self._map.values())

    def is_empty(self) -> bool:
        return len(self._map) == 0

    def __len__(self) -> int:
        return len(self._map)

    def items(self) -> Iterable[Tuple[str, int]]:
        """(original-key, count) pairs."""
        return [(orig, cnt) for (orig, cnt) in self._map.values()]


# ======================================================================
# BayesianWorldModel — online-learning Naive Bayes over (obs -> outcome).
# ======================================================================
class BayesianWorldModel(IWorldModel):
    """Online Naive Bayes world model with Laplace smoothing.

    Mirrors ``CircleAI.Companion.BayesianWorldModel``.
    """

    __slots__ = (
        "_outcome_counts",
        "_cond_counts",
        "_vocab",
        "_total_observations",
        "_alpha",
        "_lock",
    )

    def __init__(self, laplace_alpha: float = 1.0) -> None:
        if laplace_alpha <= 0:
            raise ValueError("laplace_alpha must be > 0")
        self._outcome_counts = _CiCounter()
        # outcome -> (observation -> count)
        self._cond_counts: dict[str, _CiCounter] = {}
        self._vocab: set[str] = set()  # case-insensitive vocab (lowercased)
        self._total_observations: int = 0
        self._alpha: float = laplace_alpha
        self._lock = threading.Lock()

    def observe(self, observations: Iterable[str], outcome: str) -> None:
        """Update the model with one (observations -> outcome) example."""
        if observations is None:  # type: ignore[comparison-overlap]
            raise ValueError("observations required")
        if outcome is None or len(outcome.strip()) == 0:
            raise ValueError("outcome required")

        with self._lock:
            self._outcome_counts.add(outcome, 1)
            self._total_observations += 1

            key = outcome.lower()
            cond = self._cond_counts.get(key)
            if cond is None:
                cond = _CiCounter()
                self._cond_counts[key] = cond
            for obs in observations:
                if obs is None or len(obs.strip()) == 0:
                    continue
                cond.add(obs, 1)
                self._vocab.add(obs.lower())

    async def predict_async(
        self, scenario_json: str, *, ct: Optional[object] = None
    ) -> CausalPrediction:
        # BayesianWorldModel.ExtractObservations guards IsNullOrWhiteSpace and
        # returns empty (never throws) — so null/blank -> no observations here.
        if scenario_json is None or len(scenario_json.strip()) == 0:
            observations: List[str] = []
        else:
            observations = _extract_observations(scenario_json)
        with self._lock:
            if len(observations) == 0 or self._outcome_counts.is_empty():
                return CausalPrediction("unknown", 0.5, ())

            vocab_size = max(1, len(self._vocab))
            total_ex = max(1, self._total_observations)
            outcome_count_n = len(self._outcome_counts)

            scored: List[Tuple[str, float]] = []
            for outcome, outcome_count in self._outcome_counts.items():
                # Log P(outcome) — Laplace-smoothed prior.
                log_prior = math.log(
                    (outcome_count + self._alpha)
                    / (total_ex + self._alpha * outcome_count_n)
                )

                cond = self._cond_counts.get(outcome.lower())
                total_for_outcome = cond.total() if cond is not None else 0
                log_likelihood = 0.0
                for obs in observations:
                    n = cond.get(obs) if cond is not None else 0
                    p = (n + self._alpha) / (
                        total_for_outcome + self._alpha * vocab_size
                    )
                    log_likelihood += math.log(p)
                scored.append((outcome, log_prior + log_likelihood))

            # Softmax over log-posteriors for normalised probability.
            max_log_post = max(s[1] for s in scored)
            exp_sum = sum(math.exp(s[1] - max_log_post) for s in scored)
            top = max(scored, key=lambda s: s[1])
            prob = math.exp(top[1] - max_log_post) / exp_sum
            return CausalPrediction(top[0], prob, tuple(observations))


# ======================================================================
# FrequencyWorldModel — learn P(outcome|observation) from evidence.
# ======================================================================
class FrequencyWorldModel(IWorldModel):
    """Frequency-tally world model.

    Mirrors ``CircleAI.Companion.HerJarvis.FrequencyWorldModel``.
    """

    __slots__ = ("_counts", "_lock")

    def __init__(self) -> None:
        # observation -> (outcome -> count), both case-insensitive
        self._counts: dict[str, _CiCounter] = {}
        self._lock = threading.Lock()

    def observe(self, observations: Iterable[str], outcome: str) -> None:
        """Tell the model: when these observations happen, this outcome was seen."""
        if observations is None:  # type: ignore[comparison-overlap]
            raise ValueError("observations required")
        if outcome is None or len(outcome.strip()) == 0:
            raise ValueError("outcome required")
        with self._lock:
            for obs in observations:
                lk = obs.lower()
                inner = self._counts.get(lk)
                if inner is None:
                    inner = _CiCounter()
                    self._counts[lk] = inner
                inner.add(outcome, 1)

    async def predict_async(
        self, scenario_json: str, *, ct: Optional[object] = None
    ) -> CausalPrediction:
        # NOTE: FrequencyWorldModel does NOT null-guard the JSON before parsing
        # (unlike BayesianWorldModel); a None/blank input simply yields no
        # observations here, exactly as the C# try/catch does.
        observations = _extract_observations(scenario_json)
        with self._lock:
            tally = _CiCounter()
            supporters: List[str] = []
            for obs in observations:
                inner = self._counts.get(obs.lower())
                if inner is None:
                    continue
                supporters.append(obs)
                for outcome, cnt in inner.items():
                    tally.add(outcome, cnt)
            if tally.is_empty():
                return CausalPrediction("unknown", 0.5, tuple(supporters))
            total = tally.total()
            top = max(tally.items(), key=lambda kv: kv[1])
            return CausalPrediction(top[0], top[1] / total, tuple(supporters))


__all__ = [
    "BayesianWorldModel",
    "FrequencyWorldModel",
]
