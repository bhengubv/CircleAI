# companion/theory_of_mind.py
#
# ITheoryOfMind implementation. Ported from CircleAI.Companion — the C#
# reference (HerJarvisRealImplementations.cs):
#
#   * BeliefTrackerTheoryOfMind — bag-of-belief inference with confidence decay.
#
# Scans the interaction history for mental-state verbs (thinks / believes /
# wants / fears / hopes) and the clause that follows, weighting each belief by a
# positional decay (earlier mentions count more) and by verb strength (an
# explicit "believe" is weighted higher than the softer verbs). The resulting
# belief bag is serialised to JSON byte-for-byte the way .NET's
# ``JsonSerializer.Serialize(Dictionary<string,double>)`` renders it — whole
# numbers as integers (``1`` not ``1.0``), doubles as shortest round-trip, no
# whitespace — so cross-language fixtures agree exactly.

from __future__ import annotations

import math
import re
from typing import List, Optional, Tuple

from .herjarvis_contracts import ITheoryOfMind, OtherMindEstimate

# \b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)   (IgnoreCase)
_BELIEF_RX = re.compile(
    r"\b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)", re.IGNORECASE
)


# ── System.Text.Json string escaping (JavaScriptEncoder.Default) ──────────
#
# .NET's default JSON encoder escapes MORE than Python's json.dumps: it emits
# short escapes for \b \t \n \f \r \\, and \uXXXX (UPPERCASE hex) for the double
# quote, control chars, and — critically — every HTML-sensitive ASCII char
# (" & ' + < > `) AND every non-ASCII code point. Astral characters are emitted
# as a UTF-16 surrogate pair (two \uXXXX). This table was derived by probing the
# real encoder across the full ASCII range plus selected Unicode (see the
# theory_of_mind.json fixture, e.g. the "erin" case with & < >).

_SHORT_ESCAPES = {
    0x08: "\\b",
    0x09: "\\t",
    0x0A: "\\n",
    0x0C: "\\f",
    0x0D: "\\r",
    0x5C: "\\\\",  # backslash -> \\  (short form, not \)
}

# ASCII code points (0x20..0x7E) that .NET leaves LITERAL. Everything else in
# that range is \uXXXX-escaped.
_ASCII_LITERAL = frozenset(
    ord(c)
    for c in (
        " !#$%()*,-./0123456789:;=?@"
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_"
        "abcdefghijklmnopqrstuvwxyz{|}~"
    )
)


def _escape_string(s: str) -> str:
    """Escape ``s`` exactly as ``JavaScriptEncoder.Default`` does, wrapping it in
    double quotes (matching ``JsonSerializer``'s rendering of a string)."""
    out: List[str] = ['"']
    for ch in s:
        cp = ord(ch)
        short = _SHORT_ESCAPES.get(cp)
        if short is not None:
            out.append(short)
        elif cp in _ASCII_LITERAL:
            out.append(ch)
        elif cp <= 0xFFFF:
            out.append(f"\\u{cp:04X}")
        else:
            # Astral plane -> UTF-16 surrogate pair, each as \uXXXX (uppercase).
            v = cp - 0x10000
            hi = 0xD800 + (v >> 10)
            lo = 0xDC00 + (v & 0x3FF)
            out.append(f"\\u{hi:04X}\\u{lo:04X}")
    out.append('"')
    return "".join(out)


def _render_double(x: float) -> str:
    """Render a Python float the way .NET's ``JsonSerializer`` renders a double.

    * a finite integral value -> integer text (``1`` not ``1.0``)
    * everything else         -> shortest round-trip repr (matches .NET "R")
    """
    if math.isfinite(x) and x == int(x):
        return str(int(x))
    return repr(x)


def _serialize_beliefs(pairs: List[Tuple[str, float]]) -> str:
    """Serialise (key, value) pairs like ``Dictionary<string,double>`` does:
    STJ-escaped quoted keys, ``:`` / ``,`` separators with no whitespace, .NET
    double text."""
    body = ",".join(_escape_string(k) + ":" + _render_double(v) for k, v in pairs)
    return "{" + body + "}"


class BeliefTrackerTheoryOfMind(ITheoryOfMind):
    """Bag-of-belief theory-of-mind estimator with confidence decay.

    Mirrors ``CircleAI.Companion.HerJarvis.BeliefTrackerTheoryOfMind``.
    """

    async def estimate_async(
        self,
        target: str,
        interaction_history_json: str,
        *,
        ct: Optional[object] = None,
    ) -> OtherMindEstimate:
        if target is None or len(target.strip()) == 0:
            raise ValueError("target required")
        if interaction_history_json is None:
            raise ValueError("interaction_history_json required")

        # Case-insensitive belief bag keyed by "verb:claim"; first-seen key casing
        # is preserved for serialisation (StringComparer.OrdinalIgnoreCase).
        order: List[str] = []  # lower-keys in first-seen order
        values: dict[str, Tuple[str, float]] = {}  # lower-key -> (orig-key, weight)

        idx = 0
        for m in _BELIEF_RX.finditer(interaction_history_json):
            verb = m.group(1).lower()
            claim = m.group(2).strip()
            decay = 1.0 / (1.0 + idx * 0.1)
            weight = 1.0 if verb.startswith("believ") else 0.7
            key = verb + ":" + claim
            lk = key.lower()
            contribution = weight * decay
            existing = values.get(lk)
            if existing is None:
                values[lk] = (key, contribution)
                order.append(lk)
            else:
                values[lk] = (existing[0], existing[1] + contribution)
            idx += 1

        pairs = [values[lk] for lk in order]
        js = _serialize_beliefs(pairs)
        total = sum(v for _, v in pairs)
        conf = 0.0 if len(pairs) == 0 else min(1.0, total / 5.0)
        return OtherMindEstimate(target, js, conf)


__all__ = [
    "BeliefTrackerTheoryOfMind",
]
