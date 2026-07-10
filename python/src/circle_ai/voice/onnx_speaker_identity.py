# voice/onnx_speaker_identity.py
#
# Port of CircleAI.Voice/OnnxSpeakerIdentity.cs (C# — the EXACT spec).
#
# (Phase E5) Neural speaker diarisation / identification. The C# reference runs an
# ECAPA-TDNN-style ONNX model that emits a fixed speaker vector per utterance. The
# ONNX runtime is an injected dependency here (ISpeakerEmbedder seam); everything
# else — the log-mel front-end, L2 normalisation, cosine-similarity match, running
# centroid enrollment, and the JSON enrollment store — is ported faithfully so the
# behaviour is deterministic given an embedder.
#
# Enrollment averages all observed embeddings per user and persists centroids to a
# JSON file. Identification: cosine-similarity against every enrolled centroid; the
# user above MatchThreshold wins, else None.

from __future__ import annotations

import asyncio
import json
import math
import os
import struct
import tempfile
from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import IntEnum
from typing import Dict, List, Optional


class SpeakerEmbedderInputKind(IntEnum):
    """Mirrors ``CircleAI.Voice.SpeakerEmbedderInputKind`` (stable ordinals)."""

    LOG_MEL = 0
    RAW_WAVEFORM = 1


@dataclass(frozen=True, slots=True)
class EnrolledSpeaker:
    """(Phase E5) Per-user enrollment record used for cosine-similarity ID.

    Mirrors ``CircleAI.Voice.EnrolledSpeaker``."""

    user_id: str
    centroid: List[float]
    sample_count: int


@dataclass(frozen=True, slots=True)
class SpeakerIdentityConfig:
    """(Phase E5) Configuration for :class:`OnnxSpeakerIdentity`.

    Mirrors ``CircleAI.Voice.SpeakerIdentityConfig``."""

    model_path: str
    enrollment_store_path: str
    input_kind: SpeakerEmbedderInputKind = SpeakerEmbedderInputKind.LOG_MEL
    sample_rate_hz: int = 16_000
    n_mel_bins: int = 80
    mel_frame_ms: int = 25
    mel_hop_ms: int = 10
    min_utterance_ms: int = 1_000
    max_utterance_ms: int = 8_000
    match_threshold: float = 0.55


class ISpeakerIdentity(ABC):
    """(Phase E5) Identify-or-enroll surface. Mirrors ``CircleAI.Voice.ISpeakerIdentity``
    (``IAsyncDisposable``)."""

    @abstractmethod
    async def identify_async(self, audio_pcm16: bytes, sample_rate_hz: int, ct: object = None) -> Optional[str]:
        ...

    @abstractmethod
    async def enroll_async(self, user_id: str, audio_pcm16: bytes, sample_rate_hz: int, ct: object = None) -> None:
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "ISpeakerIdentity":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


class ISpeakerEmbedder(ABC):
    """Injected neural embedder — the ONNX seam that stands in for the C#
    ``InferenceSession`` speaker model.

    Given the model input (a raw waveform, or a log-mel spectrogram as a
    ``[n_mel_bins][n_frames]`` matrix depending on the configured input kind),
    returns the raw (pre-normalisation) speaker embedding vector."""

    @abstractmethod
    def embed_waveform(self, window: List[float]) -> List[float]:
        """Embed a raw float waveform, shape [n_samples]."""
        ...

    @abstractmethod
    def embed_log_mel(self, log_mel: List[List[float]]) -> List[float]:
        """Embed a log-mel spectrogram, shape [n_mel_bins][n_frames]."""
        ...


class OnnxSpeakerIdentity(ISpeakerIdentity):
    """(Phase E5) Cosine-similarity speaker identity over an injected embedder.

    Mirrors ``CircleAI.Voice.OnnxSpeakerIdentity`` — the ONNX ``InferenceSession``
    is replaced by the :class:`ISpeakerEmbedder` seam; the rest is a faithful port.
    ``require_model_file`` (default False) skips the C# ``File.Exists(ModelPath)``
    guard so an in-memory embedder can be used without a real file on disk.
    """

    def __init__(
        self,
        config: SpeakerIdentityConfig,
        embedder: ISpeakerEmbedder,
        require_model_file: bool = False,
    ) -> None:
        if config is None:
            raise ValueError("config")
        if embedder is None:
            raise ValueError("embedder")
        if require_model_file and not os.path.isfile(config.model_path):
            raise FileNotFoundError(f"Speaker-embedding model not found: {config.model_path}")
        self._config = config
        self._embedder = embedder
        self._enrolled: Dict[str, EnrolledSpeaker] = {}
        self._store_lock = asyncio.Lock()
        self._disposed = False
        self._load_enrollment_store()

    async def identify_async(self, audio_pcm16: bytes, sample_rate_hz: int, ct: object = None) -> Optional[str]:
        if self._disposed:
            raise RuntimeError("OnnxSpeakerIdentity is disposed")
        if len(audio_pcm16) == 0:
            return None
        if not self._enrolled:
            return None

        embedding = self._compute_embedding(audio_pcm16, sample_rate_hz)
        if embedding is None:
            return None

        best: Optional[str] = None
        best_sim = -math.inf
        for user_id, speaker in self._enrolled.items():
            sim = _cosine_similarity(embedding, speaker.centroid)
            if sim > best_sim:
                best_sim = sim
                best = user_id
        return best if best_sim >= self._config.match_threshold else None

    async def enroll_async(self, user_id: str, audio_pcm16: bytes, sample_rate_hz: int, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("OnnxSpeakerIdentity is disposed")
        if user_id is None or not user_id.strip():
            raise ValueError("userId required")
        if len(audio_pcm16) == 0:
            raise ValueError("audio required")

        embedding = self._compute_embedding(audio_pcm16, sample_rate_hz)
        if embedding is None:
            raise RuntimeError("Embedding extraction failed")

        # Case-insensitive key match, matching the C# OrdinalIgnoreCase dictionary.
        existing_key = self._find_key_ci(user_id)
        if existing_key is None:
            self._enrolled[user_id] = EnrolledSpeaker(user_id, embedding, 1)
        else:
            prev = self._enrolled[existing_key]
            n = prev.sample_count
            new_centroid = [0.0] * len(prev.centroid)
            for i in range(len(new_centroid)):
                new_centroid[i] = (prev.centroid[i] * n + embedding[i]) / (n + 1)
            _l2_normalise(new_centroid)
            self._enrolled[existing_key] = EnrolledSpeaker(prev.user_id, new_centroid, n + 1)

        await self._save_enrollment_store()

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True

    # ── Embedding extraction ─────────────────────────────────────────────────────

    def _compute_embedding(self, pcm16: bytes, sample_rate_hz: int) -> Optional[List[float]]:
        try:
            if sample_rate_hz != self._config.sample_rate_hz:
                return None
            min_samples = sample_rate_hz * self._config.min_utterance_ms // 1000
            max_samples = sample_rate_hz * self._config.max_utterance_ms // 1000
            n_samples = len(pcm16) // 2
            if n_samples < min_samples:
                return None
            if n_samples > max_samples:
                n_samples = max_samples

            window = [0.0] * n_samples
            for i in range(n_samples):
                (s,) = struct.unpack_from("<h", pcm16, i * 2)
                window[i] = s / 32768.0

            if self._config.input_kind == SpeakerEmbedderInputKind.RAW_WAVEFORM:
                output = self._embedder.embed_waveform(window)
            else:
                output = self._embedder.embed_log_mel(self._log_mel(window))
            output = list(output)
            _l2_normalise(output)
            return output
        except Exception:  # noqa: BLE001 — matches C# catch -> null
            return None

    def _log_mel(self, window: List[float]) -> List[List[float]]:
        cfg = self._config
        frame_size = cfg.sample_rate_hz * cfg.mel_frame_ms // 1000
        hop_size = cfg.sample_rate_hz * cfg.mel_hop_ms // 1000
        num_frames = max(1, (len(window) - frame_size) // hop_size + 1)
        hamming = _hamming_window(frame_size)
        filters = _mel_filterbank(cfg.n_mel_bins, frame_size, cfg.sample_rate_hz)

        # Shape [n_mel_bins][n_frames], matching the C# tensor [1, NMelBins, NFrames].
        out = [[0.0] * num_frames for _ in range(cfg.n_mel_bins)]
        frame = [0.0] * frame_size
        for fi in range(num_frames):
            start = fi * hop_size
            for i in range(frame_size):
                sample = window[start + i] if start + i < len(window) else 0.0
                frame[i] = sample * hamming[i]
            power = _power_spectrum(frame)
            for m in range(cfg.n_mel_bins):
                filt = filters[m]
                total = 0.0
                length = min(len(power), len(filt))
                for k in range(length):
                    total += power[k] * filt[k]
                out[m][fi] = math.log(max(1e-10, total))
        return out

    def _load_enrollment_store(self) -> None:
        try:
            if not os.path.isfile(self._config.enrollment_store_path):
                return
            with open(self._config.enrollment_store_path, "r", encoding="utf-8") as f:
                records = json.load(f)
            if records is None:
                return
            for r in records:
                speaker = EnrolledSpeaker(
                    user_id=r["UserId"],
                    centroid=[float(x) for x in r["Centroid"]],
                    sample_count=int(r["SampleCount"]),
                )
                self._enrolled[speaker.user_id] = speaker
        except Exception:  # noqa: BLE001 — matches C# catch -> log + continue
            return

    async def _save_enrollment_store(self) -> None:
        async with self._store_lock:
            path = self._config.enrollment_store_path
            directory = os.path.dirname(path)
            if directory:
                os.makedirs(directory, exist_ok=True)
            records = [
                {
                    "UserId": s.user_id,
                    "Centroid": list(s.centroid),
                    "SampleCount": s.sample_count,
                }
                for s in self._enrolled.values()
            ]
            payload = json.dumps(records)
            tmp = path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(payload)
            os.replace(tmp, path)

    def _find_key_ci(self, user_id: str) -> Optional[str]:
        lowered = user_id.lower()
        for key in self._enrolled:
            if key.lower() == lowered:
                return key
        return None


# ── Linear-algebra helpers (module-level, mirror the C# static helpers) ─────────


def _l2_normalise(v: List[float]) -> None:
    sum_sq = 0.0
    for x in v:
        sum_sq += x * x
    norm = math.sqrt(sum_sq)
    if norm < 1e-9:
        return
    for i in range(len(v)):
        v[i] = v[i] / norm


def _cosine_similarity(a: List[float], b: List[float]) -> float:
    if len(a) != len(b):
        return -1.0
    dot = 0.0
    for i in range(len(a)):
        dot += a[i] * b[i]
    return dot


def _hamming_window(n: int) -> List[float]:
    w = [0.0] * n
    for i in range(n):
        w[i] = 0.54 - 0.46 * math.cos(2 * math.pi * i / (n - 1))
    return w


def _power_spectrum(frame: List[float]) -> List[float]:
    n = len(frame)
    half = n // 2 + 1
    spec = [0.0] * half
    for k in range(half):
        re = 0.0
        im = 0.0
        omega = -2.0 * math.pi * k / n
        for t in range(n):
            re += frame[t] * math.cos(omega * t)
            im += frame[t] * math.sin(omega * t)
        spec[k] = re * re + im * im
    return spec


def _mel_filterbank(num_filters: int, frame_size: int, sample_rate_hz: int) -> List[List[float]]:
    def hz_to_mel(hz: float) -> float:
        return 2595 * math.log10(1 + hz / 700.0)

    def mel_to_hz(mel: float) -> float:
        return 700 * (math.pow(10, mel / 2595) - 1)

    low_mel = hz_to_mel(0)
    high_mel = hz_to_mel(sample_rate_hz / 2.0)
    mel_points = [0.0] * (num_filters + 2)
    for i in range(len(mel_points)):
        mel_points[i] = low_mel + (high_mel - low_mel) * i / (len(mel_points) - 1)
    bin_points = [0] * len(mel_points)
    for i in range(len(mel_points)):
        bin_points[i] = int(math.floor((frame_size + 1) * mel_to_hz(mel_points[i]) / sample_rate_hz))

    half = frame_size // 2 + 1
    filters: List[List[float]] = [[0.0] * half for _ in range(num_filters)]
    for m in range(num_filters):
        left = bin_points[m]
        centre = bin_points[m + 1]
        right = bin_points[m + 2]
        for k in range(left, min(centre, half)):
            if centre != left:
                filters[m][k] = (k - left) / float(centre - left)
        for k in range(centre, min(right, half)):
            if right != centre:
                filters[m][k] = (right - k) / float(right - centre)
    return filters


__all__ = [
    "SpeakerEmbedderInputKind",
    "EnrolledSpeaker",
    "SpeakerIdentityConfig",
    "ISpeakerIdentity",
    "ISpeakerEmbedder",
    "OnnxSpeakerIdentity",
]
