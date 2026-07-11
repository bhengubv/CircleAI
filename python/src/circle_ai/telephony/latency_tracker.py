# latency_tracker.py
#
# Port of CircleAI.Telephony LatencyTracker.cs (C# — the EXACT spec).
#
# (3.3.0) Per-stage latency tracking for the voice loop. Records observations
# into a fixed-size sliding window per stage and surfaces p50/p95/p99 + max via
# a snapshot API.
#
# C# ConcurrentDictionary<string, Queue<long>> with a per-queue monitor lock ->
# a plain dict guarded by a top-level lock, plus collections.deque per stage
# (bounded window). Percentile math mirrors the C# ceil-based index exactly.

from __future__ import annotations

import math
import threading
from collections import deque
from dataclasses import dataclass
from datetime import timedelta
from typing import Deque, Dict, List, Optional


class LatencyStage:
    """(3.3.0) One stage we track latency on. Mirrors ``LatencyStage`` constants."""

    ASR_FIRST_WORD = "asr.first_word"
    ASR_FINAL = "asr.final"
    LLM_FIRST_TOKEN = "llm.first_token"
    LLM_FULL_RESPONSE = "llm.full_response"
    TTS_FIRST_AUDIO = "tts.first_audio"
    TTS_FULL_AUDIO = "tts.full_audio"
    END_TO_END = "voice_loop.end_to_end"


@dataclass(frozen=True, slots=True)
class LatencySnapshot:
    """(3.3.0) Snapshot of latency for one stage. Mirrors ``LatencySnapshot``."""

    stage: str
    samples: int
    min: timedelta
    p50: timedelta
    p95: timedelta
    p99: timedelta
    max: timedelta


class LatencyTracker:
    """(3.3.0) Records latency observations and produces percentiles."""

    def __init__(self, window_size: int = 256) -> None:
        if window_size <= 0:
            raise ValueError("window_size must be positive")
        self._window_size = window_size
        self._lock = threading.Lock()
        self._observations: Dict[str, Deque[int]] = {}

    def record(self, stage: str, latency: timedelta) -> None:
        """Record one observation."""
        if not stage or stage.isspace():
            raise ValueError("stage required")
        if latency < timedelta(0):
            return
        ms = int(latency.total_seconds() * 1000)
        with self._lock:
            queue = self._observations.get(stage)
            if queue is None:
                queue = deque(maxlen=self._window_size)
                self._observations[stage] = queue
            queue.append(ms)

    def snapshot(self, stage: str) -> Optional[LatencySnapshot]:
        """Snapshot percentiles for one stage."""
        with self._lock:
            queue = self._observations.get(stage)
            if queue is None or len(queue) == 0:
                return None
            sorted_arr = sorted(queue)

        def percentile(p: float) -> timedelta:
            if len(sorted_arr) == 0:
                return timedelta(0)
            idx = int(math.ceil(p * len(sorted_arr))) - 1
            if idx < 0:
                idx = 0
            if idx >= len(sorted_arr):
                idx = len(sorted_arr) - 1
            return timedelta(milliseconds=sorted_arr[idx])

        return LatencySnapshot(
            stage=stage,
            samples=len(sorted_arr),
            min=timedelta(milliseconds=sorted_arr[0]),
            p50=percentile(0.50),
            p95=percentile(0.95),
            p99=percentile(0.99),
            max=timedelta(milliseconds=sorted_arr[-1]),
        )

    def snapshot_all(self) -> List[LatencySnapshot]:
        """Snapshot every tracked stage."""
        with self._lock:
            stages = list(self._observations.keys())
        result: List[LatencySnapshot] = []
        for stage in stages:
            snap = self.snapshot(stage)
            if snap is not None:
                result.append(snap)
        return result

    def reset(self, stage: str) -> None:
        with self._lock:
            queue = self._observations.get(stage)
            if queue is not None:
                queue.clear()

    def reset_all(self) -> None:
        with self._lock:
            self._observations.clear()
