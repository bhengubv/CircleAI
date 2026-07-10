# speech/echo_cancellers.py
#
# Port of CircleAI.Speech/EchoCancellers.cs (C# — the EXACT spec).
#
# (3.3.0) Three echo cancellers:
#   - NullEchoCanceller:   pass-through DI default.
#   - NlmsEchoCanceller:   normalised-LMS adaptive filter (pure Python, no model).
#   - WebRtcEchoCanceller: shell that delegates to a host-supplied
#     IEchoCancellerModelRunner (the WebRTC AEC3 implementation lives in the host
#     package); falls back to NLMS when no runner is wired.

from __future__ import annotations

import struct
from abc import ABC, abstractmethod
from typing import Optional

from .contracts import IEchoCanceller

_SHORT_MAX = 32767
_SHORT_MIN = -32768


def _clamp(value: float, lo: float, hi: float) -> float:
    return lo if value < lo else hi if value > hi else value


class NullEchoCanceller(IEchoCanceller):
    """(3.3.0) Pass-through DI default. Mirrors ``CircleAI.Speech.NullEchoCanceller``."""

    _instance: "NullEchoCanceller | None" = None

    @classmethod
    def instance(cls) -> "NullEchoCanceller":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    def cancel(
        self,
        near_end_microphone: bytes,
        far_end_reference: bytes,
        sample_rate_hz: int,
        destination: bytearray,
    ) -> int:
        destination[: len(near_end_microphone)] = near_end_microphone
        return len(near_end_microphone)

    def reset(self) -> None:
        pass


class NlmsEchoCanceller(IEchoCanceller):
    """(3.3.0) Normalised LMS adaptive-filter AEC. Pure Python, no model downloads,
    runs on every device. Filter length defaults to 256 taps (~16 ms @ 16 kHz)
    which covers typical phone-call echo paths.

    Mirrors ``CircleAI.Speech.NlmsEchoCanceller``."""

    __slots__ = ("_w", "_step_size", "_epsilon", "_filter_length", "_ref_buffer", "_ref_index")

    def __init__(self, filter_length: int = 256, step_size: float = 0.4, epsilon: float = 1e-6) -> None:
        self._filter_length = filter_length
        self._step_size = step_size
        self._epsilon = epsilon
        self._w = [0.0] * filter_length
        self._ref_buffer = [0.0] * filter_length
        self._ref_index = 0

    @property
    def backend_id(self) -> str:
        return "nlms"

    def cancel(
        self,
        near_end_microphone: bytes,
        far_end_reference: bytes,
        sample_rate_hz: int,
        destination: bytearray,
    ) -> int:
        if len(near_end_microphone) != len(far_end_reference):
            raise ValueError("near-end and far-end must be the same length.")
        if len(destination) < len(near_end_microphone):
            raise ValueError("destination must be at least as long as input.")

        w = self._w
        ref = self._ref_buffer
        flen = self._filter_length
        idx = self._ref_index
        sample_count = len(near_end_microphone) // 2
        for n in range(sample_count):
            (mic_raw,) = struct.unpack_from("<h", near_end_microphone, n * 2)
            (far_raw,) = struct.unpack_from("<h", far_end_reference, n * 2)
            mic_sample = mic_raw / _SHORT_MAX
            far_sample = far_raw / _SHORT_MAX

            # Push far-end into circular reference buffer.
            ref[idx] = far_sample

            # Estimated echo: dot(w, ref).
            echo_estimate = 0.0
            power = self._epsilon
            for k in range(flen):
                r_idx = (idx - k + flen) % flen
                x = ref[r_idx]
                echo_estimate += w[k] * x
                power += x * x

            # Error = mic - echo estimate.
            error = mic_sample - echo_estimate

            # Update filter weights.
            mu = self._step_size / power
            for k in range(flen):
                r_idx = (idx - k + flen) % flen
                w[k] += mu * error * ref[r_idx]

            idx = (idx + 1) % flen

            # Clamp + write. C# does (int)Math.Clamp(...) then (short) cast; the
            # clamp keeps it inside int16 so the cast is a no-op truncation.
            out_sample = int(_clamp(error * _SHORT_MAX, _SHORT_MIN, _SHORT_MAX))
            struct.pack_into("<h", destination, n * 2, out_sample)

        self._ref_index = idx
        return len(near_end_microphone)

    def reset(self) -> None:
        for i in range(self._filter_length):
            self._w[i] = 0.0
            self._ref_buffer[i] = 0.0
        self._ref_index = 0


class IEchoCancellerModelRunner(ABC):
    """(3.3.0) Host-supplied AEC model runner (e.g. WebRTC AEC3).

    Mirrors ``CircleAI.Speech.IEchoCancellerModelRunner``."""

    @abstractmethod
    def process(
        self,
        near_end: bytes,
        far_end: bytes,
        sample_rate_hz: int,
        destination: bytearray,
    ) -> int:
        ...

    @abstractmethod
    def reset(self) -> None:
        ...


class WebRtcEchoCanceller(IEchoCanceller):
    """(3.3.0) WebRTC AEC3 wrapper — falls back to NLMS when no runner is wired.

    Mirrors ``CircleAI.Speech.WebRtcEchoCanceller``."""

    __slots__ = ("_runner", "_fallback")

    def __init__(self, runner: Optional[IEchoCancellerModelRunner] = None) -> None:
        self._runner = runner
        self._fallback = NlmsEchoCanceller()

    @property
    def backend_id(self) -> str:
        return "webrtc-aec3 (fallback)" if self._runner is None else "webrtc-aec3"

    def cancel(
        self,
        near_end_microphone: bytes,
        far_end_reference: bytes,
        sample_rate_hz: int,
        destination: bytearray,
    ) -> int:
        if self._runner is None:
            return self._fallback.cancel(near_end_microphone, far_end_reference, sample_rate_hz, destination)
        return self._runner.process(near_end_microphone, far_end_reference, sample_rate_hz, destination)

    def reset(self) -> None:
        self._fallback.reset()
        if self._runner is not None:
            self._runner.reset()


__all__ = [
    "NullEchoCanceller",
    "NlmsEchoCanceller",
    "IEchoCancellerModelRunner",
    "WebRtcEchoCanceller",
]
