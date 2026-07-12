# speech/cloud/_audio_http.py
#
# Shared audio + HTTP plumbing for the CircleAI.Speech.Cloud provider ports
# (C# is the EXACT spec). The C# recognizers/synthesizers each drive a
# System.Net.Http.HttpClient whose BaseAddress is the provider host; the Python
# ports inject the shared circle_ai.integration.http.IHttpFetcher instead, so
# this module reproduces the byte-level content shaping the C# did:
#
#   * ``wrap_pcm_as_wav(pcm, sample_rate)`` — the exact 44-byte little-endian WAV
#     header for 16-bit mono PCM that OpenAI / AssemblyAI / Cartesia wrap their
#     upload bytes in (verbatim field layout via ``struct``).
#   * ``strip_wav_header(data)`` — drop a leading 44-byte "RIFF...." envelope
#     (Google returns WAV; the C# strips it back to raw PCM).
#   * ``multipart_form_data(...)`` — build a ``multipart/form-data`` body + its
#     ``Content-Type: multipart/form-data; boundary=…`` the way
#     ``MultipartFormDataContent`` does (a binary file part + string parts).
#   * ``parse_pcm_rate(output_format, fallback)`` — the ``pcm_(\d+)`` reader
#     ElevenLabs uses to derive the sample rate from ``pcm_24000`` etc.
#   * ``combine_uri`` / ``bearer_auth`` — base-address join + Bearer header value.
#
# All requests route through the injected fetcher; no socket is opened here. The
# fetcher's HttpRequest carries binary bodies on ``body_bytes`` (+ content_type)
# and binary responses come back on ``HttpResponse.content_bytes``.

from __future__ import annotations

import os
import re
import struct
from typing import Iterable, Optional, Sequence, Tuple
from urllib.parse import urlsplit, urlunsplit


def combine_uri(base_address: str, path: str) -> str:
    """Resolve an absolute-path request against a base address the way
    ``HttpClient`` does when ``BaseAddress`` is set and the request path is
    absolute (starts with ``/``): scheme+authority from the base, path+query
    from the request."""
    base = urlsplit(base_address)
    rel = urlsplit(path)
    if rel.path.startswith("/"):
        new_path = rel.path
    else:
        cut = base.path.rfind("/")
        prefix = base.path[: cut + 1] if cut >= 0 else "/"
        new_path = prefix + rel.path
    return urlunsplit((base.scheme, base.netloc, new_path, rel.query, rel.fragment))


def bearer_auth(key: str) -> str:
    """C# ``new AuthenticationHeaderValue("Bearer", key)`` header value."""
    return f"Bearer {key}"


def wrap_pcm_as_wav(pcm: bytes, sample_rate: int) -> bytes:
    """Build the 44-byte WAV header + PCM body for 16-bit mono audio.

    Byte-for-byte identical to the C# ``WrapPcmAsWav``: RIFF/WAVE/fmt /data
    chunks, PCM tag 1, 1 channel, 16 bits, little-endian sizes. ``struct`` packs
    the same little-endian layout ``BitConverter.GetBytes`` produces on a
    little-endian host.
    """
    channels = 1
    bits_per_sample = 16
    byte_rate = sample_rate * channels * (bits_per_sample // 8)
    block_align = channels * (bits_per_sample // 8)
    data_size = len(pcm)
    chunk_size = 36 + data_size

    header = b"".join(
        (
            b"RIFF",
            struct.pack("<i", chunk_size),
            b"WAVE",
            b"fmt ",
            struct.pack("<i", 16),               # Subchunk1Size
            struct.pack("<h", 1),                # PCM = 1
            struct.pack("<h", channels),
            struct.pack("<i", sample_rate),
            struct.pack("<i", byte_rate),
            struct.pack("<h", block_align),
            struct.pack("<h", bits_per_sample),
            b"data",
            struct.pack("<i", data_size),
        )
    )
    return header + pcm


def strip_wav_header(data: bytes) -> bytes:
    """Strip a leading 44-byte WAV header if present. Mirrors the C#
    ``StripWavHeader`` (checks for the ``RIFF`` magic, drops 44 bytes)."""
    if len(data) > 44 and data[0:4] == b"RIFF":
        return data[44:]
    return data


def parse_pcm_rate(output_format: str, fallback: int) -> int:
    """Derive the PCM sample rate from an ElevenLabs ``output_format`` like
    ``pcm_24000``; fall back to ``fallback`` when it doesn't match. Mirrors the C#
    ``ParsePcmRate`` (``Regex.Match(outputFormat, "pcm_(\\d+)")``)."""
    m = re.search(r"pcm_(\d+)", output_format or "")
    if m:
        try:
            return int(m.group(1))
        except ValueError:
            return fallback
    return fallback


def multipart_form_data(
    file_part: Optional[Tuple[str, str, str, bytes]],
    string_parts: Sequence[Tuple[str, str]],
    boundary: Optional[str] = None,
) -> Tuple[bytes, str]:
    """Build a ``multipart/form-data`` body + content-type header.

    Mirrors ``MultipartFormDataContent``: an optional binary file part followed
    by string parts, each in its own boundary-delimited section, terminated by
    the closing ``--boundary--`` marker. CRLF line endings, matching the HTTP
    multipart wire format the C# emits.

    :param file_part: ``(field_name, file_name, content_type, bytes)`` for the
        binary part (e.g. ``("file", "audio.wav", "audio/wav", wav)``), or None.
    :param string_parts: ordered ``(field_name, value)`` string form fields.
    :param boundary: explicit boundary (else a random one is generated).
    :returns: ``(body_bytes, "multipart/form-data; boundary=…")``.
    """
    if boundary is None:
        boundary = "----CircleAIBoundary" + os.urandom(16).hex()
    crlf = b"\r\n"
    marker = ("--" + boundary).encode("ascii")
    buf = bytearray()

    if file_part is not None:
        field_name, file_name, content_type, content = file_part
        buf += marker + crlf
        buf += (
            f'Content-Disposition: form-data; name="{field_name}"; filename="{file_name}"'
        ).encode("utf-8") + crlf
        buf += f"Content-Type: {content_type}".encode("ascii") + crlf
        buf += crlf
        buf += content + crlf

    for field_name, value in string_parts:
        buf += marker + crlf
        buf += f'Content-Disposition: form-data; name="{field_name}"'.encode("utf-8") + crlf
        buf += crlf
        buf += value.encode("utf-8") + crlf

    buf += ("--" + boundary + "--").encode("ascii") + crlf
    return bytes(buf), f"multipart/form-data; boundary={boundary}"


def is_null_or_whitespace(s: Optional[str]) -> bool:
    return s is None or s.strip() == ""


__all__ = [
    "combine_uri",
    "bearer_auth",
    "wrap_pcm_as_wav",
    "strip_wav_header",
    "parse_pcm_rate",
    "multipart_form_data",
    "is_null_or_whitespace",
]
