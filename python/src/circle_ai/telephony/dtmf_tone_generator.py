# dtmf_tone_generator.py
#
# Port of CircleAI.Telephony DtmfToneGenerator.cs (C# — the EXACT spec).
#
# (3.3.0) Generate the dual-tone audio for DTMF digits, and a helper that sends
# them through any ICallSession via SendAudioAsync — works regardless of whether
# the carrier supports out-of-band DTMF.
#
# C# static class -> a module of functions. PCM-16 little-endian samples are
# packed with struct into a bytearray (the C# BinaryPrimitives.WriteInt16LE +
# byte[] path). Math.Clamp(s, -1, 1) * short.MaxValue -> the same clamp then a
# truncation toward zero (C# ``(short)`` cast) via int().

from __future__ import annotations

import math
import struct
from datetime import timedelta
from typing import Dict, Optional, Tuple

from .contracts import ICallSession
from .primitives import AudioFrame, CallMediaFormat

_SHORT_MAX = 32767
_SHORT_MIN = -32768

# (3.3.0) Standard DTMF frequencies (low row x high column).
_FREQUENCIES: Dict[str, Tuple[int, int]] = {
    "1": (697, 1209),
    "2": (697, 1336),
    "3": (697, 1477),
    "A": (697, 1633),
    "4": (770, 1209),
    "5": (770, 1336),
    "6": (770, 1477),
    "B": (770, 1633),
    "7": (852, 1209),
    "8": (852, 1336),
    "9": (852, 1477),
    "C": (852, 1633),
    "*": (941, 1209),
    "0": (941, 1336),
    "#": (941, 1477),
    "D": (941, 1633),
}


def _clamp(value: float, lo: float, hi: float) -> float:
    if value < lo:
        return lo
    if value > hi:
        return hi
    return value


def generate(digit: str, sample_rate_hz: int, duration_ms: int = 150, amplitude: float = 0.5) -> bytes:
    """(3.3.0) Generate one PCM-16 mono buffer for the digit at the given sample rate.

    ``digit``: DTMF digit 0-9, *, #, A, B, C, D.
    """
    if sample_rate_hz <= 0:
        raise ValueError("sample_rate_hz out of range")
    if duration_ms <= 0:
        raise ValueError("duration_ms out of range")
    key = digit.upper()
    pair = _FREQUENCIES.get(key)
    if pair is None:
        raise ValueError(f"Unsupported DTMF digit '{digit}'.")

    low, high = pair
    samples = sample_rate_hz * duration_ms // 1000
    buf = bytearray(samples * 2)
    for i in range(samples):
        t = i / sample_rate_hz
        s = 0.5 * amplitude * (math.sin(2 * math.pi * low * t) + math.sin(2 * math.pi * high * t))
        val = int(_clamp(s, -1, 1) * _SHORT_MAX)
        struct.pack_into("<h", buf, i * 2, val)
    return bytes(buf)


def generate_sequence(
    digits: str,
    sample_rate_hz: int,
    tone_duration_ms: int = 150,
    inter_digit_gap_ms: int = 50,
    amplitude: float = 0.5,
) -> bytes:
    """(3.3.0) Generate a full string of digits with gap silence between them."""
    if not digits:
        return b""
    gap_samples = sample_rate_hz * inter_digit_gap_ms // 1000
    gap = bytes(gap_samples * 2)

    out = bytearray()
    for i, ch in enumerate(digits):
        tone = generate(ch, sample_rate_hz, tone_duration_ms, amplitude)
        out.extend(tone)
        if i < len(digits) - 1:
            out.extend(gap)
    return bytes(out)


async def send_through_session_async(
    session: ICallSession,
    digits: str,
    sample_rate_hz: int = 8000,
    tone_duration_ms: int = 150,
    inter_digit_gap_ms: int = 50,
    *,
    ct: Optional[object] = None,
) -> None:
    """(3.3.0) Send ``digits`` over the call via in-band tones."""
    if session is None:
        raise ValueError("session must not be None")
    if not digits:
        return

    pcm = generate_sequence(digits, sample_rate_hz, tone_duration_ms, inter_digit_gap_ms)
    if sample_rate_hz == 8000:
        fmt = CallMediaFormat.MULAW8000
    elif sample_rate_hz == 16000:
        fmt = CallMediaFormat.PCM16000
    elif sample_rate_hz == 24000:
        fmt = CallMediaFormat.PCM24000
    else:
        fmt = CallMediaFormat.PCM16000
    await session.send_audio_async(AudioFrame(pcm, fmt, timedelta(0)), ct=ct)
