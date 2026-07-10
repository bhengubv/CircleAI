# speech/audio_format_converter.py
#
# Port of CircleAI.Speech/AudioFormatConverter.cs (C# — the EXACT spec).
#
# (3.3.0) Audio format conversion. Phone carriers feed mu-law / a-law at 8 kHz;
# cloud STT/TTS speak linear PCM at 16/24/44.1 kHz. The converter handles every
# common path:
#   - mu-law 8 kHz   <-> PCM-16 16 kHz / 24 kHz
#   - a-law  8 kHz   <-> PCM-16 16 kHz / 24 kHz
#   - PCM-16 N kHz   ->  PCM-16 M kHz  (linear interpolation)
#
# All PCM is little-endian signed 16-bit mono, matching the C# BinaryPrimitives
# ReadInt16LittleEndian / WriteInt16LittleEndian sites via struct "<h".

from __future__ import annotations

import math
import struct
from enum import IntEnum

_SHORT_MAX = 32767
_SHORT_MIN = -32768


def _to_int16(v: int) -> int:
    """Wrap ``v`` into the signed 16-bit range the way a C# ``(short)`` cast does."""
    v &= 0xFFFF
    return v - 0x10000 if v >= 0x8000 else v


class AudioCodec(IntEnum):
    """(3.3.0) Carrier-native audio formats we know how to convert.

    Mirrors ``CircleAI.Speech.AudioCodec`` (stable ordinals)."""

    #: 16-bit signed linear PCM, little-endian, mono.
    PCM16 = 0
    #: G.711 μ-law (telephony, North America / Japan).
    MU_LAW = 1
    #: G.711 A-law (telephony, Europe).
    A_LAW = 2


# ── μ-law ──────────────────────────────────────────────────────────────────────


def _mu_law_to_linear(mu: int) -> int:
    # G.711 μ-law decode (ITU-T G.711).
    mu = (~mu) & 0xFF
    sign = mu & 0x80
    exponent = (mu >> 4) & 0x07
    mantissa = mu & 0x0F
    magnitude = ((mantissa << 3) + 0x84) << exponent
    sample = magnitude - 0x84
    return _to_int16(-sample if sign != 0 else sample)


def _linear_to_mu_law(pcm: int) -> int:
    bias = 0x84
    clip = 32635
    sign = (pcm >> 8) & 0x80
    v = pcm
    if sign != 0:
        v = -v
    if v > clip:
        v = clip
    v += bias

    if v >= 0x4000:
        exponent = 7
    elif v >= 0x2000:
        exponent = 6
    elif v >= 0x1000:
        exponent = 5
    elif v >= 0x0800:
        exponent = 4
    elif v >= 0x0400:
        exponent = 3
    elif v >= 0x0200:
        exponent = 2
    elif v >= 0x0100:
        exponent = 1
    else:
        exponent = 0

    mantissa = (v >> (exponent + 3)) & 0x0F
    return (~(sign | (exponent << 4) | mantissa)) & 0xFF


def decode_mu_law_to_pcm16(mulaw: bytes) -> bytes:
    out = bytearray(len(mulaw) * 2)
    for i, b in enumerate(mulaw):
        struct.pack_into("<h", out, i * 2, _mu_law_to_linear(b))
    return bytes(out)


def encode_pcm16_to_mu_law(pcm: bytes) -> bytes:
    samples = len(pcm) // 2
    out = bytearray(samples)
    for i in range(samples):
        (s,) = struct.unpack_from("<h", pcm, i * 2)
        out[i] = _linear_to_mu_law(s)
    return bytes(out)


# ── a-law ──────────────────────────────────────────────────────────────────────


def _a_law_to_linear(a: int) -> int:
    a ^= 0x55
    sign = a & 0x80
    exponent = (a >> 4) & 0x07
    mantissa = a & 0x0F
    if exponent != 0:
        magnitude = ((mantissa << 4) + 0x108) << (exponent - 1)
    else:
        magnitude = (mantissa << 4) + 0x08
    return _to_int16(-magnitude if sign != 0 else magnitude)


def _linear_to_a_law(pcm: int) -> int:
    sign = (pcm >> 8) & 0x80
    v = pcm
    if sign != 0:
        v = -v
    if v > 0x7FFF:
        v = 0x7FFF

    if v < 256:
        exponent = 0
        mantissa = v >> 4
    else:
        if v >= 0x4000:
            exponent = 7
        elif v >= 0x2000:
            exponent = 6
        elif v >= 0x1000:
            exponent = 5
        elif v >= 0x0800:
            exponent = 4
        elif v >= 0x0400:
            exponent = 3
        elif v >= 0x0200:
            exponent = 2
        else:
            exponent = 1
        mantissa = (v >> (exponent + 3)) & 0x0F
    return ((sign | (exponent << 4) | mantissa) ^ 0x55) & 0xFF


def decode_a_law_to_pcm16(alaw: bytes) -> bytes:
    out = bytearray(len(alaw) * 2)
    for i, b in enumerate(alaw):
        struct.pack_into("<h", out, i * 2, _a_law_to_linear(b))
    return bytes(out)


def encode_pcm16_to_a_law(pcm: bytes) -> bytes:
    samples = len(pcm) // 2
    out = bytearray(samples)
    for i in range(samples):
        (s,) = struct.unpack_from("<h", pcm, i * 2)
        out[i] = _linear_to_a_law(s)
    return bytes(out)


# ── resample (linear interpolation) ────────────────────────────────────────────


def resample_pcm16_linear(pcm: bytes, from_hz: int, to_hz: int) -> bytes:
    if from_hz == to_hz:
        return pcm
    src_samples = len(pcm) // 2
    dst_samples = int(src_samples * to_hz // from_hz)
    out = bytearray(dst_samples * 2)
    for i in range(dst_samples):
        src_idx = i * from_hz / to_hz
        idx0 = int(math.floor(src_idx))
        idx1 = min(idx0 + 1, src_samples - 1)
        frac = src_idx - idx0
        (s0,) = struct.unpack_from("<h", pcm, idx0 * 2)
        (s1,) = struct.unpack_from("<h", pcm, idx1 * 2)
        # C# does (short)(s0 + (s1 - s0) * frac) — truncates toward zero, then
        # wraps into int16.
        s = _to_int16(int(s0 + (s1 - s0) * frac))
        struct.pack_into("<h", out, i * 2, s)
    return bytes(out)


class AudioFormatConverter:
    """(3.3.0) Stateless audio-format converter.

    Mirrors ``CircleAI.Speech.AudioFormatConverter``. The decode / encode /
    resample building blocks are also exposed as module-level functions (the
    analogue of the C# ``public static`` helpers)."""

    @staticmethod
    def convert(
        input_bytes: bytes,
        input_codec: AudioCodec,
        input_sample_rate_hz: int,
        output_codec: AudioCodec,
        output_sample_rate_hz: int,
    ) -> bytes:
        """(3.3.0) Convert audio from one (codec, sample rate) to another. Returns
        the freshly allocated output buffer; caller does NOT need to size it."""
        if input_sample_rate_hz <= 0:
            raise ValueError("input_sample_rate_hz")
        if output_sample_rate_hz <= 0:
            raise ValueError("output_sample_rate_hz")

        # 1) Decode source to PCM-16.
        if input_codec == AudioCodec.PCM16:
            pcm_in = bytes(input_bytes)
        elif input_codec == AudioCodec.MU_LAW:
            pcm_in = decode_mu_law_to_pcm16(input_bytes)
        elif input_codec == AudioCodec.A_LAW:
            pcm_in = decode_a_law_to_pcm16(input_bytes)
        else:
            raise NotImplementedError(f"Unknown input codec {input_codec}")

        # 2) Resample if needed.
        pcm_resampled = (
            pcm_in
            if input_sample_rate_hz == output_sample_rate_hz
            else resample_pcm16_linear(pcm_in, input_sample_rate_hz, output_sample_rate_hz)
        )

        # 3) Encode to target codec.
        if output_codec == AudioCodec.PCM16:
            return pcm_resampled
        if output_codec == AudioCodec.MU_LAW:
            return encode_pcm16_to_mu_law(pcm_resampled)
        if output_codec == AudioCodec.A_LAW:
            return encode_pcm16_to_a_law(pcm_resampled)
        raise NotImplementedError(f"Unknown output codec {output_codec}")

    # Static building blocks (mirror the C# ``public static`` helpers).
    decode_mu_law_to_pcm16 = staticmethod(decode_mu_law_to_pcm16)
    encode_pcm16_to_mu_law = staticmethod(encode_pcm16_to_mu_law)
    decode_a_law_to_pcm16 = staticmethod(decode_a_law_to_pcm16)
    encode_pcm16_to_a_law = staticmethod(encode_pcm16_to_a_law)
    resample_pcm16_linear = staticmethod(resample_pcm16_linear)


__all__ = [
    "AudioCodec",
    "AudioFormatConverter",
    "decode_mu_law_to_pcm16",
    "encode_pcm16_to_mu_law",
    "decode_a_law_to_pcm16",
    "encode_pcm16_to_a_law",
    "resample_pcm16_linear",
]
