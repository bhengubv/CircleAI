"""``<think>...</think>`` reasoning router + stop-sequence handling.

Port of the routing state machine in ``CircleAI.Inference.MnnTokenRouter``.
Every MNN-backed generator (Qwen, KimiVl) feeds decoded text through this
router, which splits the stream into CONTENT and REASONING fragments and
honours caller-supplied stop sequences.

The C# router operates token-by-token off a native callback with a UTF-8
holdback so a ``</think>`` tag straddling a token boundary is never
mis-classified. Python has no token callback, so :func:`route_text` runs the
identical state machine over a full decoded string in one pass — producing
byte-identical fragment splits for any input the C# router would see, since
the C# holdback only defers *when* a boundary flushes, never *how* the final
split lands.
"""
from __future__ import annotations

from typing import List, Tuple

from ..models.models import ChatFragment, ChatFragmentKind

__all__ = [
    "THINK_OPEN",
    "THINK_CLOSE",
    "find_stop_sequence",
    "route_text",
]

THINK_OPEN = "<think>"
THINK_CLOSE = "</think>"


def find_stop_sequence(text: str, stops: List[str]) -> int:
    """Return the earliest index at which any non-empty stop sequence occurs,
    or -1 when none is present. Mirrors ``MnnTokenRouter.TryFindStopSequence``
    (first stop in list order that matches, returning its index).
    """
    for stop in stops:
        if not stop:
            continue
        idx = text.find(stop)
        if idx >= 0:
            return idx
    return -1


def route_text(
    text: str,
    stops: List[str],
    include_reasoning: bool,
) -> List[ChatFragment]:
    """Split ``text`` into tagged fragments via the ``<think>`` state machine.

    * Text outside ``<think>...</think>`` becomes CONTENT fragments.
    * Text inside becomes REASONING fragments (dropped when
      ``include_reasoning`` is ``False``).
    * The ``<think>`` / ``</think>`` tags themselves are stripped.
    * If a stop sequence matches, everything up to (but not including) the
      stop marker is routed and the remainder is discarded — mirroring the C#
      ``Stopped`` path where the trailing stop marker never leaks into content.

    Returns fragments in emission order. Adjacent same-kind runs are emitted as
    a single fragment (the C# router coalesces per flush; over a whole string
    that collapses to one fragment per contiguous region).
    """
    # Truncate at the earliest stop-sequence *position*. The C# router runs the
    # stop check token-by-token, so whichever stop appears first in the stream
    # fires first — i.e. the leftmost position across all stops. (This differs
    # from find_stop_sequence, which faithfully ports the C# TryFindStopSequence
    # first-in-list-order helper; here we need the stream-equivalent outcome.)
    stop_at = -1
    for stop in stops:
        if not stop:
            continue
        idx = text.find(stop)
        if idx >= 0 and (stop_at < 0 or idx < stop_at):
            stop_at = idx
    if stop_at >= 0:
        text = text[:stop_at]

    fragments: List[ChatFragment] = []
    pos = 0
    in_think = False
    n = len(text)

    while pos < n:
        if in_think:
            close_idx = text.find(THINK_CLOSE, pos)
            if close_idx >= 0:
                if close_idx > pos and include_reasoning:
                    fragments.append(
                        ChatFragment(kind=ChatFragmentKind.REASONING, text=text[pos:close_idx])
                    )
                pos = close_idx + len(THINK_CLOSE)
                in_think = False
                continue
            # No close tag — rest of the string is reasoning.
            if include_reasoning and pos < n:
                fragments.append(
                    ChatFragment(kind=ChatFragmentKind.REASONING, text=text[pos:n])
                )
            pos = n
        else:
            open_idx = text.find(THINK_OPEN, pos)
            if open_idx >= 0:
                if open_idx > pos:
                    fragments.append(
                        ChatFragment(kind=ChatFragmentKind.CONTENT, text=text[pos:open_idx])
                    )
                pos = open_idx + len(THINK_OPEN)
                in_think = True
                continue
            # No open tag — rest of the string is content.
            if pos < n:
                fragments.append(
                    ChatFragment(kind=ChatFragmentKind.CONTENT, text=text[pos:n])
                )
            pos = n

    return fragments


def split_content_reasoning(fragments: List[ChatFragment]) -> Tuple[str, str | None]:
    """Aggregate fragments into (content, reasoning). ``reasoning`` is ``None``
    when no REASONING fragment was emitted — matching the C#
    ``GenerateResponseAsync`` aggregation.
    """
    content_parts: List[str] = []
    reasoning_parts: List[str] = []
    for f in fragments:
        if f.kind == ChatFragmentKind.REASONING:
            reasoning_parts.append(f.text)
        else:
            content_parts.append(f.text)
    reasoning = "".join(reasoning_parts) if reasoning_parts else None
    return "".join(content_parts), reasoning
