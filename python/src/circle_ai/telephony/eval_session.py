# eval_session.py
#
# Port of CircleAI.Telephony EvalSession.cs (C# — the EXACT spec).
#
# (3.3.0) Drive an end-to-end voice-pipeline test against a real LLM without
# needing a carrier minute. The harness feeds a scripted conversation (user
# utterances) through the same pipeline production uses, then collects everything
# the AI said back for assertion.
#
# C# delegate EvalTurnHandler (Task<string>(string, CancellationToken)) -> an
# async Callable. Keyword matching uses str.casefold() 'in' checks (C#
# IndexOf(..., OrdinalIgnoreCase) >= 0). Latency is timed with a monotonic clock
# so it never goes negative.

from __future__ import annotations

import time
from dataclasses import dataclass
from datetime import timedelta
from typing import Awaitable, Callable, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class EvalTurn:
    """(3.3.0) One scripted turn from a fake caller.

    ``user_transcript``: what the caller said (already-transcribed).
    ``expected_keywords``: optional keywords the AI's response should include.
    """

    user_transcript: str
    expected_keywords: Optional[Sequence[str]] = None


@dataclass(frozen=True, slots=True)
class EvalTurnResult:
    """(3.3.0) Outcome of one eval turn."""

    assistant_response: str
    missing_keywords: List[str]
    latency: timedelta


@dataclass(frozen=True, slots=True)
class EvalRunResult:
    """(3.3.0) Overall eval result."""

    turns: List[EvalTurnResult]
    all_keywords_hit: bool
    total_latency: timedelta


# (3.3.0) Function that runs one turn through the AI under test.
EvalTurnHandler = Callable[[str, Optional[object]], Awaitable[str]]


class EvalSession:
    """(3.3.0) Drives an EvalSession against a real LLM-based handler."""

    def __init__(self, handler: EvalTurnHandler) -> None:
        if handler is None:
            raise ValueError("handler must not be None")
        self._handler = handler

    async def run_async(
        self, script: Sequence[EvalTurn], *, ct: Optional[object] = None
    ) -> EvalRunResult:
        """(3.3.0) Run the script and assemble results."""
        if script is None:
            raise ValueError("script must not be None")
        results: List[EvalTurnResult] = []
        total = timedelta(0)
        all_hit = True
        for turn in script:
            started = time.monotonic()
            response = await self._handler(turn.user_transcript, ct)
            elapsed = timedelta(seconds=time.monotonic() - started)
            total += elapsed

            missing: List[str] = []
            if turn.expected_keywords is not None:
                haystack = response.casefold()
                for kw in turn.expected_keywords:
                    if kw.casefold() not in haystack:
                        missing.append(kw)
            if len(missing) > 0:
                all_hit = False
            results.append(EvalTurnResult(response, missing, elapsed))
        return EvalRunResult(results, all_hit, total)
