"""test_voice_intent_router.py — CircleAI.Speech.Cloud voice-intent router.

Covers the ordered first-hit matching, named-group capture surfacing, empty +
no-match fallback, and the null router. C# (KeywordVoiceIntentRouter.cs) is spec.
"""
from __future__ import annotations

import re

import pytest

from circle_ai.speech import (
    IVoiceIntentRouter,
    KeywordVoiceIntentRouter,
    NullVoiceIntentRouter,
    VoiceIntent,
    VoiceIntentMatch,
)


def _router(*intents: VoiceIntent, fallback: str = "ask-ai") -> KeywordVoiceIntentRouter:
    return KeywordVoiceIntentRouter(list(intents), fallback_intent_name=fallback)


async def test_backend_id():
    assert _router().backend_id == "keyword"
    assert NullVoiceIntentRouter.instance().backend_id == "null"


async def test_first_matching_intent_wins():
    r = _router(
        VoiceIntent("call", re.compile(r"call (?P<who>\w+)", re.IGNORECASE)),
        VoiceIntent("text", re.compile(r"text (?P<who>\w+)", re.IGNORECASE)),
    )
    m = await r.route_async("Call Alice")
    assert m.intent_name == "call"
    assert m.transcript == "Call Alice"
    assert m.captures == {"who": "Alice"}


async def test_named_groups_surfaced_and_trimmed():
    r = _router(VoiceIntent("play", re.compile(r"play (?P<song>.+)")))
    m = await r.route_async("play   bohemian rhapsody  ")
    # transcript is trimmed; the captured group value is trimmed too.
    assert m.transcript == "play   bohemian rhapsody"
    assert m.captures == {"song": "bohemian rhapsody"}


async def test_no_named_groups_yields_empty_captures():
    r = _router(VoiceIntent("stop", re.compile(r"^stop$")))
    m = await r.route_async("stop")
    assert m.intent_name == "stop"
    assert m.captures == {}


async def test_no_match_falls_back():
    r = _router(VoiceIntent("weather", re.compile(r"weather")), fallback="ask-ai")
    m = await r.route_async("tell me a joke")
    assert m.intent_name == "ask-ai"
    assert m.transcript == "tell me a joke"
    assert m.captures == {}


async def test_empty_transcript_falls_back_with_empty_transcript():
    r = _router(VoiceIntent("weather", re.compile(r"weather")), fallback="fallback-x")
    m = await r.route_async("   ")
    assert m.intent_name == "fallback-x"
    assert m.transcript == ""
    assert m.captures == {}


async def test_none_transcript_treated_as_empty():
    r = _router(VoiceIntent("x", re.compile(r"x")))
    m = await r.route_async(None)  # type: ignore[arg-type]
    assert m.intent_name == "ask-ai"
    assert m.transcript == ""


async def test_empty_named_group_not_surfaced():
    # An optional group that did not participate must not appear in captures.
    r = _router(VoiceIntent("set", re.compile(r"set(?P<val> \w+)?")))
    m = await r.route_async("set")
    assert m.intent_name == "set"
    assert m.captures == {}


async def test_null_router_always_ask_ai():
    r = NullVoiceIntentRouter.instance()
    m = await r.route_async("literally anything")
    assert m.intent_name == "ask-ai"
    assert m.transcript == "literally anything"
    assert m.captures == {}


def test_blank_fallback_rejected():
    with pytest.raises(ValueError):
        KeywordVoiceIntentRouter([], fallback_intent_name="  ")
