# speech/cloud/deepgram_speech_recognizer.py
#
# Port of CircleAI.Speech.Cloud/DeepgramSpeechRecognizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechRecognizer backed by Deepgram's /v1/listen endpoint. Single-shot
# HTTP POST with raw PCM (encoding=linear16), "Token <key>" auth, and the
# results.channels[0].alternatives[0] response shape (transcript + per-word
# segments). Fail-soft on missing key / non-2xx.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The raw PCM body rides body_bytes with
# the audio/raw content-type; JSON is read off resp.json().

from __future__ import annotations

import logging
from datetime import timedelta
from typing import List, Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechRecognizer, TranscribedSegment, TranscriptionResult
from ._audio_http import combine_uri, is_null_or_whitespace
from .options import DeepgramOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.DeepgramSpeechRecognizer")


def _empty() -> TranscriptionResult:
    return TranscriptionResult("", None, (), timedelta(0))


def _escape(value: str) -> str:
    from urllib.parse import quote

    return quote(value, safe="-_.~")


class DeepgramSpeechRecognizer(ISpeechRecognizer):
    """(3.3.0) Deepgram-backed :class:`ISpeechRecognizer`.

    Mirrors ``CircleAI.Speech.Cloud.DeepgramSpeechRecognizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: DeepgramOptions,
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
        return "deepgram"

    @property
    def is_configured(self) -> bool:
        return not is_null_or_whitespace(self._options.api_key)

    async def transcribe_async(
        self,
        audio_pcm16_mono: bytes,
        sample_rate_hz: int,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> TranscriptionResult:
        if not self.is_configured:
            return _empty()

        path = (
            f"/v1/listen?model={_escape(self._options.model)}&encoding=linear16"
            f"&sample_rate={sample_rate_hz}&channels=1&punctuate=true"
        )
        if not is_null_or_whitespace(language_hint):
            path += f"&language={_escape(language_hint)}"

        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, path),
                headers={"Authorization": f"Token {self._options.api_key or ''}"},
                body_bytes=bytes(audio_pcm16_mono),
                content_type="audio/raw",
            )
        )
        if not resp.is_success:
            self._logger.warning("Deepgram returned %s", resp.status_code)
            return _empty()

        doc = resp.json()
        if not isinstance(doc, dict):
            return _empty()

        # Response shape: results.channels[0].alternatives[0].transcript
        results = doc.get("results")
        if not isinstance(results, dict):
            return _empty()
        channels = results.get("channels")
        if not isinstance(channels, list) or len(channels) == 0:
            return _empty()
        first_channel = channels[0]
        if not isinstance(first_channel, dict):
            return _empty()
        alts = first_channel.get("alternatives")
        if not isinstance(alts, list) or len(alts) == 0:
            return _empty()
        first_alt = alts[0]
        if not isinstance(first_alt, dict):
            return _empty()

        text = first_alt.get("transcript") if isinstance(first_alt.get("transcript"), str) else ""

        segments: List[TranscribedSegment] = []
        words = first_alt.get("words")
        if isinstance(words, list):
            for w in words:
                if not isinstance(w, dict):
                    continue
                start = float(w["start"]) if isinstance(w.get("start"), (int, float)) else 0.0
                end = float(w["end"]) if isinstance(w.get("end"), (int, float)) else start
                confidence = (
                    float(w["confidence"]) if isinstance(w.get("confidence"), (int, float)) else 0.0
                )
                segments.append(
                    TranscribedSegment(
                        text=w.get("word") if isinstance(w.get("word"), str) else "",
                        offset=timedelta(seconds=start),
                        duration=timedelta(seconds=end - start),
                        language=language_hint,
                        confidence=confidence,
                    )
                )

        duration = timedelta(0)
        meta = doc.get("metadata")
        if isinstance(meta, dict) and isinstance(meta.get("duration"), (int, float)):
            duration = timedelta(seconds=float(meta["duration"]))

        return TranscriptionResult(text, language_hint, tuple(segments), duration)


__all__ = ["DeepgramSpeechRecognizer"]
