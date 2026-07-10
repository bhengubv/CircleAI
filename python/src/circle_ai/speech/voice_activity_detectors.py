# speech/voice_activity_detectors.py
#
# Port of CircleAI.Speech/VoiceActivityDetectors.cs (C# — the EXACT spec).
#
# (3.3.0) Three voice-activity detectors:
#   - NullVoiceActivityDetector: always-speech, used as DI default.
#   - EnergyVoiceActivityDetector: RMS energy + zero-crossing rate + hangover
#     frames. Works on every device, no model needed.
#   - SileroVoiceActivityDetector: wraps a host-supplied IVadModelRunner (ONNX
#     runner). The runner is None by default — falls back to energy-based output
#     until a host wires the real model.

from __future__ import annotations

import math
import struct
from abc import ABC, abstractmethod
from datetime import timedelta
from typing import Optional

from .contracts import IVoiceActivityDetector, VadFrameResult

_SHORT_MAX = 32767


def _sign(x: int) -> int:
    return (x > 0) - (x < 0)


class NullVoiceActivityDetector(IVoiceActivityDetector):
    """(3.3.0) Always reports speech — DI default so nothing breaks before a real
    VAD is wired. Mirrors ``CircleAI.Speech.NullVoiceActivityDetector``."""

    _instance: "NullVoiceActivityDetector | None" = None

    @classmethod
    def instance(cls) -> "NullVoiceActivityDetector":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    @property
    def speech_threshold(self) -> float:
        return 0.5

    def classify(self, audio_pcm16_mono: bytes, sample_rate_hz: int, offset: timedelta) -> VadFrameResult:
        return VadFrameResult(is_speech=True, speech_probability=1.0, offset=offset)

    def reset(self) -> None:
        pass


class EnergyVoiceActivityDetector(IVoiceActivityDetector):
    """(3.3.0) Production-grade VAD using RMS energy + zero-crossing rate +
    hangover-frame smoothing. No ML model required — works on every device.

    Mirrors ``CircleAI.Speech.EnergyVoiceActivityDetector``."""

    __slots__ = ("_speech_threshold", "_energy_threshold", "_hangover_frames", "_hangover_remaining")

    def __init__(
        self,
        speech_threshold: float = 0.55,
        energy_threshold: float = 0.012,
        hangover_frames: int = 8,
    ) -> None:
        self._speech_threshold = speech_threshold
        self._energy_threshold = energy_threshold
        self._hangover_frames = hangover_frames
        self._hangover_remaining = 0

    @property
    def backend_id(self) -> str:
        return "energy"

    @property
    def speech_threshold(self) -> float:
        return self._speech_threshold

    def classify(self, audio_pcm16_mono: bytes, sample_rate_hz: int, offset: timedelta) -> VadFrameResult:
        if len(audio_pcm16_mono) < 2:
            return VadFrameResult(is_speech=False, speech_probability=0.0, offset=offset)

        n = len(audio_pcm16_mono) // 2
        samples = struct.unpack_from("<" + "h" * n, audio_pcm16_mono, 0)
        sum_squares = 0.0
        zero_crossings = 0
        previous = 0
        for i in range(n):
            s = samples[i]
            sum_squares += s * s
            if i > 0 and _sign(s) != _sign(previous) and s != 0 and previous != 0:
                zero_crossings += 1
            previous = samples[i]

        rms = math.sqrt(sum_squares / n) / _SHORT_MAX  # 0..1
        zcr_rate = zero_crossings / n

        # Speech: high RMS + moderate ZCR (~0.05-0.25 for voiced speech).
        energy_good = rms >= self._energy_threshold
        zcr_good = 0.02 <= zcr_rate <= 0.30
        raw_prob = (0.85 if zcr_good else 0.6) if energy_good else 0.1

        threshold = self._speech_threshold
        if raw_prob >= threshold:
            is_speech = True
            self._hangover_remaining = self._hangover_frames
        elif self._hangover_remaining > 0:
            is_speech = True
            self._hangover_remaining -= 1
            raw_prob = max(raw_prob, threshold)
        else:
            is_speech = False

        return VadFrameResult(is_speech=is_speech, speech_probability=raw_prob, offset=offset)

    def reset(self) -> None:
        self._hangover_remaining = 0


class IVadModelRunner(ABC):
    """(3.3.0) ONNX model runner contract supplied by the host package.

    Mirrors ``CircleAI.Speech.IVadModelRunner``."""

    @abstractmethod
    def score_frame(self, audio_pcm16_mono: bytes, sample_rate_hz: int) -> float:
        """Score one 30 ms / 16 kHz PCM-16 frame; result is 0..1."""
        ...


class SileroVoiceActivityDetector(IVoiceActivityDetector):
    """(3.3.0) Silero VAD wrapper. Delegates the per-frame score to a host
    ``IVadModelRunner``; when no runner is wired it transparently falls back to
    ``EnergyVoiceActivityDetector``'s scoring.

    Mirrors ``CircleAI.Speech.SileroVoiceActivityDetector``."""

    __slots__ = ("_runner", "_fallback", "_hangover_frames", "_hangover_remaining", "_speech_threshold")

    def __init__(
        self,
        runner: Optional[IVadModelRunner] = None,
        speech_threshold: float = 0.5,
        hangover_frames: int = 8,
    ) -> None:
        self._runner = runner
        self._fallback = EnergyVoiceActivityDetector(speech_threshold)
        self._speech_threshold = speech_threshold
        self._hangover_frames = hangover_frames
        self._hangover_remaining = 0

    @property
    def backend_id(self) -> str:
        return "silero (fallback)" if self._runner is None else "silero"

    @property
    def speech_threshold(self) -> float:
        return self._speech_threshold

    def classify(self, audio_pcm16_mono: bytes, sample_rate_hz: int, offset: timedelta) -> VadFrameResult:
        if self._runner is None:
            return self._fallback.classify(audio_pcm16_mono, sample_rate_hz, offset)

        prob = self._runner.score_frame(audio_pcm16_mono, sample_rate_hz)
        if prob >= self._speech_threshold:
            is_speech = True
            self._hangover_remaining = self._hangover_frames
        elif self._hangover_remaining > 0:
            is_speech = True
            self._hangover_remaining -= 1
        else:
            is_speech = False
        return VadFrameResult(is_speech=is_speech, speech_probability=prob, offset=offset)

    def reset(self) -> None:
        self._hangover_remaining = 0
        self._fallback.reset()


__all__ = [
    "NullVoiceActivityDetector",
    "EnergyVoiceActivityDetector",
    "IVadModelRunner",
    "SileroVoiceActivityDetector",
]
