# voice/onnx_speech_emotion_detector.py
#
# Port of CircleAI.Voice/OnnxSpeechEmotionDetector.cs (C# — the EXACT spec).
#
# (Phase E6) Real speech-emotion recognition. The C# reference runs a
# wav2vec2-style ONNX model over a raw float waveform and emits logits over the
# emotion classes; the winning softmax class wins, and arousal/valence are looked
# up from a built-in Russell-circumplex mapping. The ONNX runtime is an injected
# dependency here (IEmotionClassifier seam); the PCM->float front-end, softmax,
# label selection, and circumplex mapping are ported faithfully.

from __future__ import annotations

import math
import os
import struct
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple


@dataclass(frozen=True, slots=True)
class SpeechEmotionFrame:
    """(Phase E6) Output emotion frame from a speech-emotion model.

    Mirrors ``CircleAI.Voice.SpeechEmotionFrame``.

    :param label: Top-1 emotion label (lowercase, e.g. "happy", "angry").
    :param arousal: Russell-circumplex arousal coordinate in [-1, 1].
    :param valence: Russell-circumplex valence coordinate in [-1, 1].
    :param probability: Softmax probability of the winning class.
    """

    label: str
    arousal: float
    valence: float
    probability: float


# Default 4-class layout (SUPERB-ER + IEMOCAP), matching the C# DefaultLabels.
_DEFAULT_LABELS: Tuple[str, ...] = ("neutral", "happy", "angry", "sad")


def _default_labels() -> List[str]:
    return list(_DEFAULT_LABELS)


@dataclass(frozen=True, slots=True)
class SpeechEmotionConfig:
    """(Phase E6) Configuration for :class:`OnnxSpeechEmotionDetector`.

    Mirrors ``CircleAI.Voice.SpeechEmotionConfig``."""

    model_path: str
    labels: Optional[List[str]] = None
    sample_rate_hz: int = 16_000
    max_clip_ms: int = 8_000


# Russell circumplex coordinates for the standard discrete emotion labels
# (Posner 2005, Mehrabian/Russell). Anything outside maps to (0,0) = neutral.
_CIRCUMPLEX: Dict[str, Tuple[float, float]] = {
    "neutral": (0.00, 0.00),
    "happy": (0.55, 0.81),
    "happiness": (0.55, 0.81),
    "joy": (0.60, 0.82),
    "angry": (0.74, -0.62),
    "anger": (0.74, -0.62),
    "sad": (-0.43, -0.65),
    "sadness": (-0.43, -0.65),
    "fear": (0.78, -0.64),
    "fearful": (0.78, -0.64),
    "surprise": (0.85, 0.40),
    "surprised": (0.85, 0.40),
    "disgust": (0.45, -0.60),
    "disgusted": (0.45, -0.60),
    "calm": (-0.40, 0.45),
    "excited": (0.82, 0.70),
    "bored": (-0.65, -0.20),
    "frustrated": (0.55, -0.55),
    "contempt": (0.20, -0.55),
}


class ISpeechEmotionDetector(ABC):
    """(Phase E6) Mirrors ``CircleAI.Voice.ISpeechEmotionDetector`` (``IAsyncDisposable``)."""

    @abstractmethod
    async def sense_async(
        self, audio_pcm16: bytes, sample_rate_hz: int, ct: object = None
    ) -> Optional[SpeechEmotionFrame]:
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "ISpeechEmotionDetector":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


class IEmotionClassifier(ABC):
    """Injected neural classifier — the ONNX seam that stands in for the C#
    ``InferenceSession`` emotion model. Given the raw float waveform (shape
    [n_samples]) it returns the raw logits over the emotion classes."""

    @abstractmethod
    def classify(self, window: List[float]) -> List[float]:
        ...


class OnnxSpeechEmotionDetector(ISpeechEmotionDetector):
    """(Phase E6) Speech-emotion detector over an injected classifier.

    Mirrors ``CircleAI.Voice.OnnxSpeechEmotionDetector`` — the ONNX
    ``InferenceSession`` is replaced by the :class:`IEmotionClassifier` seam; the
    rest is a faithful port. ``require_model_file`` (default False) skips the C#
    ``File.Exists(ModelPath)`` guard for in-memory use.
    """

    def __init__(
        self,
        config: SpeechEmotionConfig,
        classifier: IEmotionClassifier,
        require_model_file: bool = False,
    ) -> None:
        if config is None:
            raise ValueError("config")
        if classifier is None:
            raise ValueError("classifier")
        if require_model_file and not os.path.isfile(config.model_path):
            raise FileNotFoundError(f"Speech-emotion model not found: {config.model_path}")
        self._config = config
        self._classifier = classifier
        self._labels = list(config.labels) if config.labels is not None else _default_labels()
        self._disposed = False

    async def sense_async(
        self, audio_pcm16: bytes, sample_rate_hz: int, ct: object = None
    ) -> Optional[SpeechEmotionFrame]:
        if self._disposed:
            raise RuntimeError("OnnxSpeechEmotionDetector is disposed")
        if len(audio_pcm16) == 0:
            return None
        if sample_rate_hz != self._config.sample_rate_hz:
            return None

        max_samples = sample_rate_hz * self._config.max_clip_ms // 1000
        n_samples = min(len(audio_pcm16) // 2, max_samples)
        if n_samples == 0:
            return None

        window = [0.0] * n_samples
        for i in range(n_samples):
            (s,) = struct.unpack_from("<h", audio_pcm16, i * 2)
            window[i] = s / 32768.0

        try:
            logits = list(self._classifier.classify(window))
            best_idx, best_prob = _softmax(logits)
            label = (self._labels[best_idx] if 0 <= best_idx < len(self._labels) else "unknown").lower()
            arousal, valence = _CIRCUMPLEX.get(label, (0.0, 0.0))
            return SpeechEmotionFrame(label, arousal, valence, best_prob)
        except Exception:  # noqa: BLE001 — matches C# catch -> null
            return None

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True


def _softmax(logits: List[float]) -> Tuple[int, float]:
    """Mirrors the C# private ``Softmax`` — returns (best index, best probability)."""
    if len(logits) == 0:
        return (-1, 0.0)
    max_v = logits[0]
    for i in range(1, len(logits)):
        if logits[i] > max_v:
            max_v = logits[i]
    denom = 0.0
    for v in logits:
        denom += math.exp(v - max_v)

    best_idx = 0
    best_prob = 0.0
    for i in range(len(logits)):
        p = math.exp(logits[i] - max_v) / denom
        if p > best_prob:
            best_prob = p
            best_idx = i
    return (best_idx, best_prob)


__all__ = [
    "SpeechEmotionFrame",
    "SpeechEmotionConfig",
    "ISpeechEmotionDetector",
    "IEmotionClassifier",
    "OnnxSpeechEmotionDetector",
]
