# speech/cloud/azure_speech_recognizer.py
#
# Port of CircleAI.Speech.Cloud/AzureSpeechRecognizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechRecognizer backed by Microsoft Azure Cognitive Services
# Speech-to-Text REST endpoint. Raw PCM body with the
# "audio/wav; codecs=audio/pcm; samplerate=N" content-type, subscription-key
# header, and the detailed-format response (RecognitionStatus / DisplayText /
# Offset+Duration in 100-ns ticks / NBest[0].Confidence). Fail-soft unless BOTH
# api_key AND base_address are set.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The raw PCM body rides body_bytes with
# the codec content-type; JSON is read off resp.json(). Azure ticks (HNS, 100 ns)
# map to timedelta(microseconds=ticks/10).

from __future__ import annotations

import logging
from datetime import timedelta
from typing import Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechRecognizer, TranscribedSegment, TranscriptionResult
from ._audio_http import combine_uri, is_null_or_whitespace
from .options import AzureSpeechOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.AzureSpeechRecognizer")


def _empty() -> TranscriptionResult:
    return TranscriptionResult("", None, (), timedelta(0))


def _escape(value: str) -> str:
    from urllib.parse import quote

    return quote(value, safe="-_.~")


def _ticks_to_timedelta(ticks: int) -> timedelta:
    # C# TimeSpan.FromTicks: 1 tick == 100 nanoseconds == 0.1 microsecond.
    return timedelta(microseconds=ticks / 10)


class AzureSpeechRecognizer(ISpeechRecognizer):
    """(3.3.0) Azure-backed :class:`ISpeechRecognizer`.

    Mirrors ``CircleAI.Speech.Cloud.AzureSpeechRecognizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: AzureSpeechOptions,
        logger: Optional[logging.Logger] = None,
    ) -> None:
        if http is None:
            raise ValueError("http must not be None")
        if options is None:
            raise ValueError("options must not be None")
        self._http = http
        self._options = options
        self._logger = logger if logger is not None else _logger

    @property
    def backend_id(self) -> str:
        return "azure-stt"

    @property
    def is_configured(self) -> bool:
        return not is_null_or_whitespace(self._options.api_key) and self._options.base_address is not None

    async def transcribe_async(
        self,
        audio_pcm16_mono: bytes,
        sample_rate_hz: int,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> TranscriptionResult:
        if not self.is_configured:
            return _empty()

        lang = self._options.language_code if is_null_or_whitespace(language_hint) else language_hint
        path = (
            f"/speech/recognition/conversation/cognitiveservices/v1"
            f"?language={_escape(lang)}&format=detailed"
        )

        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, path),
                headers={
                    "Ocp-Apim-Subscription-Key": self._options.api_key or "",
                    "Accept": "application/json",
                },
                body_bytes=bytes(audio_pcm16_mono),
                content_type=f"audio/wav; codecs=audio/pcm; samplerate={sample_rate_hz}",
            )
        )
        if not resp.is_success:
            self._logger.warning("Azure STT returned %s", resp.status_code)
            return _empty()

        doc = resp.json()
        if not isinstance(doc, dict):
            return _empty()

        status = doc.get("RecognitionStatus") if isinstance(doc.get("RecognitionStatus"), str) else None
        if status != "Success":
            return _empty()

        text = doc.get("DisplayText") if isinstance(doc.get("DisplayText"), str) else ""

        offset_ticks = int(doc["Offset"]) if isinstance(doc.get("Offset"), (int, float)) else 0
        duration_ticks = int(doc["Duration"]) if isinstance(doc.get("Duration"), (int, float)) else 0
        duration = _ticks_to_timedelta(duration_ticks)

        confidence = 0.0
        nbest = doc.get("NBest")
        if isinstance(nbest, list) and len(nbest) > 0 and isinstance(nbest[0], dict):
            c = nbest[0].get("Confidence")
            if isinstance(c, (int, float)):
                confidence = float(c)

        segment = TranscribedSegment(
            text=text,
            offset=_ticks_to_timedelta(offset_ticks),
            duration=duration,
            language=lang,
            confidence=confidence,
        )

        return TranscriptionResult(text, lang, (segment,), duration)


__all__ = ["AzureSpeechRecognizer"]
