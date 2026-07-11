# call_cost_calculator.py
#
# Port of CircleAI.Telephony CallCostCalculator.cs (C# — the EXACT spec).
#
# (3.3.0) Track per-call cost across the four spend axes: carrier telephony
# minutes, STT seconds, TTS characters, LLM tokens (input + output). Caller
# pipeline emits usage events; the calculator turns them into a running cost
# figure that the orchestrator can compare against a budget ceiling.
#
# C# uses Interlocked on long fields for lock-free counters; Python has no
# atomic 64-bit ints, so a threading.Lock guards the counters to preserve the
# thread-safe semantics. C# decimal (exact money) -> decimal.Decimal, and the
# division constants are kept as Decimals so no float sneaks into the money math.

from __future__ import annotations

import threading
from dataclasses import dataclass
from datetime import timedelta
from decimal import Decimal


@dataclass(frozen=True, slots=True)
class CallPricing:
    """(3.3.0) Per-unit prices (USD or any consistent currency).

    Mirrors ``CircleAI.Telephony.CallPricing``.
    """

    carrier_per_minute: Decimal
    stt_per_second: Decimal
    tts_per_thousand_chars: Decimal
    llm_input_per_k_token: Decimal
    llm_output_per_k_token: Decimal


@dataclass(frozen=True, slots=True)
class CallCostBreakdown:
    """(3.3.0) Breakdown of where the money went. Mirrors ``CallCostBreakdown``."""

    carrier: Decimal
    stt: Decimal
    tts: Decimal
    llm_input: Decimal
    llm_output: Decimal
    total: Decimal


class CallCostCalculator:
    """(3.3.0) Tracks cost for one call."""

    def __init__(self, pricing: CallPricing) -> None:
        if pricing is None:
            raise ValueError("pricing must not be None")
        self._pricing = pricing
        self._lock = threading.Lock()
        self._carrier_ms = 0
        self._stt_ms = 0
        self._tts_chars = 0
        self._llm_input_tokens = 0
        self._llm_output_tokens = 0

    def add_carrier_time(self, duration: timedelta) -> None:
        """Add carrier telephony usage."""
        if duration < timedelta(0):
            return
        with self._lock:
            self._carrier_ms += int(duration.total_seconds() * 1000)

    def add_stt_time(self, duration: timedelta) -> None:
        """Add STT usage."""
        if duration < timedelta(0):
            return
        with self._lock:
            self._stt_ms += int(duration.total_seconds() * 1000)

    def add_tts_characters(self, chars: int) -> None:
        """Add TTS usage in characters."""
        if chars <= 0:
            return
        with self._lock:
            self._tts_chars += chars

    def add_llm_tokens(self, input_tokens: int, output_tokens: int) -> None:
        """Add LLM tokens."""
        with self._lock:
            if input_tokens > 0:
                self._llm_input_tokens += input_tokens
            if output_tokens > 0:
                self._llm_output_tokens += output_tokens

    def current_breakdown(self) -> CallCostBreakdown:
        """Snapshot the current total cost breakdown."""
        with self._lock:
            carrier_ms = self._carrier_ms
            stt_ms = self._stt_ms
            tts_chars = self._tts_chars
            llm_input_tokens = self._llm_input_tokens
            llm_output_tokens = self._llm_output_tokens

        carrier_min = Decimal(carrier_ms) / Decimal(60_000)
        stt_sec = Decimal(stt_ms) / Decimal(1000)
        tts_k = Decimal(tts_chars) / Decimal(1000)
        llm_input_k = Decimal(llm_input_tokens) / Decimal(1000)
        llm_output_k = Decimal(llm_output_tokens) / Decimal(1000)

        carrier = carrier_min * self._pricing.carrier_per_minute
        stt = stt_sec * self._pricing.stt_per_second
        tts = tts_k * self._pricing.tts_per_thousand_chars
        llm_in = llm_input_k * self._pricing.llm_input_per_k_token
        llm_out = llm_output_k * self._pricing.llm_output_per_k_token
        total = carrier + stt + tts + llm_in + llm_out

        return CallCostBreakdown(carrier, stt, tts, llm_in, llm_out, total)

    def reset(self) -> None:
        with self._lock:
            self._carrier_ms = 0
            self._stt_ms = 0
            self._tts_chars = 0
            self._llm_input_tokens = 0
            self._llm_output_tokens = 0
