"""voice_wav.py

Port of src/CircleAI.Voice/WavIo.cs — minimal RIFF/WAVE reading and PCM-16
packing, so a reference recording can become the float samples a voice needs.

Parity is asserted against fixtures/voice_wav_io.json.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

# Mimi's sample rate — what to_mono_24k resamples to.
VOICE_TARGET_RATE = 24000

_F32 = struct.Struct("<f")


def _f32(x: float) -> float:
    """Narrow a Python double to float32, matching C# `(float)` / TS Math.fround."""
    return _F32.unpack(_F32.pack(x))[0]


@dataclass(frozen=True)
class Wav:
    """Interleaved float samples in [-1,1], plus rate and channel count."""

    samples: list[float]
    rate: int
    channels: int


def parse_wav(raw: bytes) -> Wav:
    """Parse a RIFF/WAVE buffer."""
    if (
        len(raw) < 12
        or raw[0:4] != b"RIFF"
        or raw[8:12] != b"WAVE"
    ):
        raise ValueError("not a RIFF/WAVE file")

    fmt = channels = rate = bits = 0
    data = b""
    offset = 12

    # WALK THE CHUNKS. A WAV written by anything other than the simplest encoder
    # carries LIST/fact/cue chunks before the data, and assuming data starts at
    # byte 44 reads metadata as audio — which sounds like a short burst of noise
    # before the real recording.
    while offset + 8 <= len(raw):
        chunk_id = raw[offset : offset + 4]
        (size,) = struct.unpack_from("<i", raw, offset + 4)
        body = offset + 8
        if size < 0 or body + size > len(raw):
            size = len(raw) - body

        if chunk_id == b"fmt ":
            fmt, channels, rate = struct.unpack_from("<HHi", raw, body)
            (bits,) = struct.unpack_from("<H", raw, body + 14)
        elif chunk_id == b"data":
            data = raw[body : body + size]

        offset = body + size + (size & 1)  # chunks are word-aligned

    if channels == 0 or rate == 0 or not data:
        raise ValueError("no usable fmt/data chunk")

    # 3 is IEEE float; 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real format lives
    # in a sub-chunk — treated as PCM here, which is what it is in every file the
    # voice stack has met.
    pcm = fmt in (1, 0xFFFE)
    if pcm and bits == 8:
        samples = [_f32((b - 128) / 128.0) for b in data]
    elif pcm and bits == 16:
        samples = [_f32(v / 32768.0) for (v,) in struct.iter_unpack("<h", data[: len(data) // 2 * 2])]
    elif pcm and bits == 24:
        samples = []
        for i in range(0, len(data) - 2, 3):
            v = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)
            if v & 0x800000:
                v -= 0x1000000
            samples.append(_f32(v / 8388608.0))
    elif pcm and bits == 32:
        samples = [
            _f32(v / 2147483648.0)
            for (v,) in struct.iter_unpack("<i", data[: len(data) // 4 * 4])
        ]
    elif fmt == 3 and bits == 32:
        samples = [v for (v,) in struct.iter_unpack("<f", data[: len(data) // 4 * 4])]
    else:
        raise ValueError(
            f"WAV format {fmt} at {bits} bits is not decoded by this reader"
        )

    return Wav(samples=samples, rate=rate, channels=channels)


def to_mono_24k(wav: Wav, max_seconds: int = 30) -> list[float]:
    """Downmix to mono, resample to 24 kHz, and cap the length."""
    samples = wav.samples

    if wav.channels > 1:
        mono = []
        for i in range(len(samples) // wav.channels):
            frame = samples[i * wav.channels : (i + 1) * wav.channels]
            mono.append(_f32(sum(frame) / wav.channels))
        samples = mono

    if wav.rate != VOICE_TARGET_RATE:
        samples = _resample(samples, wav.rate, VOICE_TARGET_RATE)

    cap = max_seconds * VOICE_TARGET_RATE
    return samples[:cap] if len(samples) > cap else samples


def read_mono_24k(path: str | Path, max_seconds: int = 30) -> list[float]:
    """Read a WAV file as mono float samples at 24 kHz."""
    return to_mono_24k(parse_wav(Path(path).read_bytes()), max_seconds)


def to_pcm16(samples: list[float]) -> bytes:
    """Pack float samples in [-1,1] as little-endian signed 16-bit PCM."""
    out = bytearray(len(samples) * 2)
    for i, s in enumerate(samples):
        v = int(max(-1.0, min(1.0, s)) * 32767)
        struct.pack_into("<h", out, i * 2, v)
    return bytes(out)


def _resample(input_: list[float], from_rate: int, to_rate: int) -> list[float]:
    """Linear resample. Adequate here: the target is a speaker embedding, not playback."""
    if not input_:
        return []
    count = max(round(len(input_) * to_rate / from_rate), 1)
    step = (len(input_) - 1) / max(count - 1, 1)
    out = []
    for i in range(count):
        x = i * step
        lo = int(x)
        hi = min(lo + 1, len(input_) - 1)
        out.append(_f32(input_[lo] + (input_[hi] - input_[lo]) * (x - lo)))
    return out
