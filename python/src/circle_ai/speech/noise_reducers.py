# speech/noise_reducers.py
#
# Port of CircleAI.Speech/NoiseReducers.cs (C# — the EXACT spec).
#
# (3.3.0) Three noise reducers:
#   - NullNoiseReducer: no-op pass-through with backend_id="null".
#   - SpectralSubtractionNoiseReducer: lightweight no-model floor-noise
#     subtraction in the time domain (envelope-following gate).
#   - KrispNoiseReducer / DeepFilterNetNoiseReducer: thin shells that delegate to
#     a host-supplied INoiseReducerModelRunner — fall back to spectral subtraction
#     when no runner is wired.

from __future__ import annotations

import struct
from abc import ABC, abstractmethod
from typing import Optional

from .contracts import INoiseReducer

_SHORT_MAX = 32767
_SHORT_MIN = -32768


def _to_int16(v: int) -> int:
    v &= 0xFFFF
    return v - 0x10000 if v >= 0x8000 else v


class NullNoiseReducer(INoiseReducer):
    """(3.3.0) No-op reducer — DI default. Mirrors ``CircleAI.Speech.NullNoiseReducer``."""

    _instance: "NullNoiseReducer | None" = None

    @classmethod
    def instance(cls) -> "NullNoiseReducer":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    @property
    def is_available(self) -> bool:
        return True

    def reduce(self, audio_pcm16_mono: bytes, sample_rate_hz: int, destination: bytearray) -> int:
        destination[: len(audio_pcm16_mono)] = audio_pcm16_mono
        return len(audio_pcm16_mono)


class SpectralSubtractionNoiseReducer(INoiseReducer):
    """(3.3.0) Lightweight time-domain noise gate: attenuates samples below the
    estimated noise floor with a soft knee. Not as clean as a DNN but adds zero
    runtime cost and works on every device.

    Mirrors ``CircleAI.Speech.SpectralSubtractionNoiseReducer``."""

    __slots__ = ("_floor_estimate", "_attenuation")

    def __init__(self, floor_estimate: float = 0.008, attenuation: float = 0.25) -> None:
        self._floor_estimate = floor_estimate
        self._attenuation = attenuation

    @property
    def backend_id(self) -> str:
        return "passthrough"

    @property
    def is_available(self) -> bool:
        return True

    def reduce(self, audio_pcm16_mono: bytes, sample_rate_hz: int, destination: bytearray) -> int:
        if len(destination) < len(audio_pcm16_mono):
            raise ValueError("destination must be at least as long as input.")

        n = len(audio_pcm16_mono) // 2
        floor = int(self._floor_estimate * _SHORT_MAX)
        for i in range(n):
            (s,) = struct.unpack_from("<h", audio_pcm16_mono, i * 2)
            if abs(s) <= floor:
                # C# writes (short)(s * _attenuation) — float mult then truncate + wrap.
                struct.pack_into("<h", destination, i * 2, _to_int16(int(s * self._attenuation)))
            else:
                struct.pack_into("<h", destination, i * 2, s)
        return len(audio_pcm16_mono)


class INoiseReducerModelRunner(ABC):
    """(3.3.0) Host-supplied DNN runner for noise reduction.

    Mirrors ``CircleAI.Speech.INoiseReducerModelRunner``."""

    @abstractmethod
    def process(self, audio_pcm16_mono: bytes, sample_rate_hz: int, destination: bytearray) -> int:
        """Process one frame; write cleaned PCM-16 mono into the destination buffer."""
        ...


class KrispNoiseReducer(INoiseReducer):
    """(3.3.0) Krisp wrapper — uses the host's runner when present.

    Mirrors ``CircleAI.Speech.KrispNoiseReducer``."""

    __slots__ = ("_runner", "_fallback")

    def __init__(self, runner: Optional[INoiseReducerModelRunner] = None) -> None:
        self._runner = runner
        self._fallback = SpectralSubtractionNoiseReducer()

    @property
    def backend_id(self) -> str:
        return "krisp (fallback)" if self._runner is None else "krisp"

    @property
    def is_available(self) -> bool:
        return True

    def reduce(self, audio_pcm16_mono: bytes, sample_rate_hz: int, destination: bytearray) -> int:
        if self._runner is None:
            return self._fallback.reduce(audio_pcm16_mono, sample_rate_hz, destination)
        return self._runner.process(audio_pcm16_mono, sample_rate_hz, destination)


class DeepFilterNetNoiseReducer(INoiseReducer):
    """(3.3.0) DeepFilterNet wrapper.

    Mirrors ``CircleAI.Speech.DeepFilterNetNoiseReducer``."""

    __slots__ = ("_runner", "_fallback")

    def __init__(self, runner: Optional[INoiseReducerModelRunner] = None) -> None:
        self._runner = runner
        self._fallback = SpectralSubtractionNoiseReducer()

    @property
    def backend_id(self) -> str:
        return "deepfilternet (fallback)" if self._runner is None else "deepfilternet"

    @property
    def is_available(self) -> bool:
        return True

    def reduce(self, audio_pcm16_mono: bytes, sample_rate_hz: int, destination: bytearray) -> int:
        if self._runner is None:
            return self._fallback.reduce(audio_pcm16_mono, sample_rate_hz, destination)
        return self._runner.process(audio_pcm16_mono, sample_rate_hz, destination)


__all__ = [
    "NullNoiseReducer",
    "SpectralSubtractionNoiseReducer",
    "INoiseReducerModelRunner",
    "KrispNoiseReducer",
    "DeepFilterNetNoiseReducer",
]
