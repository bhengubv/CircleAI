"""test_world_model.py

Verifies the IWorldModel reasoning core ported from CircleAI.Companion:

  * BayesianWorldModel  — online Naive Bayes with Laplace smoothing.
  * FrequencyWorldModel — raw frequency tally.

Covers observation extraction (name=value, with .NET-faithful value rendering),
the "unknown"/0.5 fallback, learned prediction, softmax normalisation, and
case-insensitive keying. Mirrors the C# reference (BayesianWorldModel.cs +
HerJarvisRealImplementations.cs).
"""
from __future__ import annotations

import json
import math

import pytest

from circle_ai.companion.herjarvis_contracts import CausalPrediction, IWorldModel
from circle_ai.companion.world_model import BayesianWorldModel, FrequencyWorldModel


# ── contracts / construction ──────────────────────────────────────────────


def test_both_models_implement_iworldmodel() -> None:
    assert isinstance(BayesianWorldModel(), IWorldModel)
    assert isinstance(FrequencyWorldModel(), IWorldModel)


def test_bayesian_rejects_non_positive_alpha() -> None:
    with pytest.raises(ValueError):
        BayesianWorldModel(0.0)
    with pytest.raises(ValueError):
        BayesianWorldModel(-1.0)


def test_observe_rejects_blank_outcome() -> None:
    for m in (BayesianWorldModel(), FrequencyWorldModel()):
        with pytest.raises(ValueError):
            m.observe(["a=b"], "")
        with pytest.raises(ValueError):
            m.observe(["a=b"], "   ")


# ── fallback path ─────────────────────────────────────────────────────────


async def test_bayesian_untrained_returns_unknown_half() -> None:
    p = await BayesianWorldModel().predict_async(json.dumps({"sky": "grey"}))
    assert p == CausalPrediction("unknown", 0.5, ())


async def test_bayesian_empty_scenario_returns_unknown() -> None:
    b = BayesianWorldModel()
    b.observe(["sky=grey"], "rain")
    # No observations extracted -> unknown, empty supporters.
    p = await b.predict_async("{}")
    assert p.outcome == "unknown"
    assert p.probability == 0.5
    assert list(p.supporting_factors) == []


async def test_bayesian_non_object_json_returns_unknown() -> None:
    b = BayesianWorldModel()
    b.observe(["sky=grey"], "rain")
    for bad in ("[1,2,3]", '"a string"', "not json at all", "   "):
        p = await b.predict_async(bad)
        assert p.outcome == "unknown"


async def test_frequency_untrained_returns_unknown_half() -> None:
    p = await FrequencyWorldModel().predict_async(json.dumps({"sky": "grey"}))
    assert p.outcome == "unknown"
    assert p.probability == 0.5
    assert list(p.supporting_factors) == []


async def test_frequency_blank_json_returns_unknown() -> None:
    # Whitespace/empty parse to a JSON syntax error (== C# JsonException) -> no
    # observations -> unknown.
    f = FrequencyWorldModel()
    f.observe(["sky=grey"], "rain")
    for blank in ("   ", ""):
        p = await f.predict_async(blank)
        assert p.outcome == "unknown"


async def test_frequency_none_json_raises() -> None:
    # C# FrequencyWorldModel.PredictAsync(null) throws ArgumentNullException
    # (JsonDocument.Parse(null)); Bayesian, by contrast, guards and returns
    # unknown. Preserve that asymmetry.
    f = FrequencyWorldModel()
    f.observe(["sky=grey"], "rain")
    with pytest.raises(ValueError):
        await f.predict_async(None)  # type: ignore[arg-type]


async def test_bayesian_none_json_returns_unknown_not_raises() -> None:
    # The Bayesian counterpart guards IsNullOrWhiteSpace -> never throws.
    b = BayesianWorldModel()
    b.observe(["sky=grey"], "rain")
    p = await b.predict_async(None)  # type: ignore[arg-type]
    assert p.outcome == "unknown"
    assert p.probability == 0.5


# ── learned prediction ────────────────────────────────────────────────────


async def test_bayesian_predicts_dominant_outcome() -> None:
    b = BayesianWorldModel()
    b.observe(["sky=grey", "humidity=high"], "rain")
    b.observe(["sky=grey", "humidity=high"], "rain")
    b.observe(["sky=blue", "humidity=low"], "sunny")

    p = await b.predict_async(json.dumps({"sky": "grey", "humidity": "high"}))
    assert p.outcome == "rain"
    assert 0.5 < p.probability <= 1.0
    # supporting factors are the extracted observations, in order.
    assert list(p.supporting_factors) == ["sky=grey", "humidity=high"]


async def test_bayesian_probabilities_are_softmax_normalised() -> None:
    b = BayesianWorldModel()
    b.observe(["a=1"], "x")
    b.observe(["b=2"], "y")
    p = await b.predict_async(json.dumps({"a": "1"}))
    # A single top probability must be a valid probability in (0, 1].
    assert 0.0 < p.probability <= 1.0


async def test_frequency_predicts_and_reports_probability() -> None:
    f = FrequencyWorldModel()
    f.observe(["sky=grey"], "rain")
    f.observe(["sky=grey"], "rain")
    f.observe(["sky=grey"], "sunny")
    p = await f.predict_async(json.dumps({"sky": "grey"}))
    assert p.outcome == "rain"
    assert math.isclose(p.probability, 2.0 / 3.0)
    assert list(p.supporting_factors) == ["sky=grey"]


async def test_frequency_supporters_only_include_matched_observations() -> None:
    f = FrequencyWorldModel()
    f.observe(["sky=grey"], "rain")
    p = await f.predict_async(json.dumps({"sky": "grey", "unseen": "x"}))
    # "unseen=x" was never observed -> not a supporter.
    assert list(p.supporting_factors) == ["sky=grey"]


# ── observation extraction / value rendering ──────────────────────────────


async def test_observation_value_rendering_matches_dotnet() -> None:
    # Booleans render as True/False (.NET), null as empty string, numbers raw,
    # nested objects/arrays as compact JSON. Train on those exact keys and
    # confirm they are the supporters we get back.
    f = FrequencyWorldModel()
    f.observe(
        ["flag=True", "count=42", "ratio=3.5", "missing=", "nested={\"x\":1}", "arr=[1,2]"],
        "hit",
    )
    scenario = json.dumps(
        {
            "flag": True,
            "count": 42,
            "ratio": 3.5,
            "missing": None,
            "nested": {"x": 1},
            "arr": [1, 2],
        }
    )
    p = await f.predict_async(scenario)
    assert p.outcome == "hit"
    assert set(p.supporting_factors) == {
        "flag=True",
        "count=42",
        "ratio=3.5",
        "missing=",
        'nested={"x":1}',
        "arr=[1,2]",
    }


async def test_case_insensitive_outcome_and_observation_keys() -> None:
    b = BayesianWorldModel()
    b.observe(["Sky=Grey"], "Rain")
    b.observe(["sky=grey"], "rain")  # same outcome & obs, different casing
    p = await b.predict_async(json.dumps({"SKY": "GREY"}))
    # The two observations collapsed into one case-insensitive outcome bucket.
    assert p.outcome.lower() == "rain"


async def test_bayesian_blank_observations_are_skipped_on_observe() -> None:
    b = BayesianWorldModel()
    b.observe(["real=1", "", "   ", "also=2"], "outcome")
    # Only non-blank observations entered the vocab / conditional counts; a
    # prediction on a real observation still resolves to the single outcome.
    p = await b.predict_async(json.dumps({"real": "1"}))
    assert p.outcome == "outcome"
