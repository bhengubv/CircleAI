# speech/cloud/openai_speech_recognizer.py
#
# Port of CircleAI.Speech.Cloud/OpenAiSpeechRecognizer.cs (C# — the EXACT spec).
#
# (3.2.0) ISpeechRecognizer backed by OpenAI's Whisper /v1/audio/transcriptions
# endpoint. Same multipart form upload as Concierge's OpenAiVoiceRuntime — PCM
# bytes wrapped in a WAV envelope (Whisper won't take headerless PCM), model +
# response_format=verbose_json + optional language part. Fail-soft: an empty
# ApiKey returns an empty TranscriptionResult rather than raising, so a fallback
# router can move on.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The multipart body is built with the
# _audio_http.multipart_form_data helper (byte-identical wire shape) and rides
# body_bytes with the multipart content-type; the verbose_json response is read
# off resp.json() exactly as the C# reads JsonDocument.

from __future__ import annotations

import logging
from datetime import timedelta
from typing import List, Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import (
    ISpeechRecognizer,
    TranscribedSegment,
    TranscriptionResult,
)
from ._audio_http import (
    bearer_auth,
    combine_uri,
    is_null_or_whitespace,
    multipart_form_data,
    wrap_pcm_as_wav,
)
from .options import OpenAiVoiceOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.OpenAiSpeechRecognizer")


def _empty() -> TranscriptionResult:
    return TranscriptionResult("", None, (), timedelta(0))


class OpenAiSpeechRecognizer(ISpeechRecognizer):
    """(3.2.0) :class:`ISpeechRecognizer` backed by OpenAI Whisper.

    Mirrors ``CircleAI.Speech.Cloud.OpenAiSpeechRecognizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: OpenAiVoiceOptions,
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
        return "openai-whisper"

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

        # Wrap PCM bytes in a WAV header so Whisper accepts them.
        wav_bytes = wrap_pcm_as_wav(audio_pcm16_mono, sample_rate_hz)

        string_parts = [
            ("model", self._options.transcription_model),
            ("response_format", "verbose_json"),
        ]
        if not is_null_or_whitespace(language_hint):
            string_parts.append(("language", language_hint))

        body, content_type = multipart_form_data(
            file_part=("file", "audio.wav", "audio/wav", wav_bytes),
            string_parts=string_parts,
        )

        response = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, "/v1/audio/transcriptions"),
                headers={"Authorization": bearer_auth(self._options.api_key or "")},
                body_bytes=body,
                content_type=content_type,
            )
        )
        if not response.is_success:
            self._logger.warning(
                "OpenAI transcription returned %s: %s", response.status_code, response.text
            )
            return _empty()

        doc = response.json()
        if not isinstance(doc, dict):
            return _empty()

        text = doc.get("text") if isinstance(doc.get("text"), str) else ""
        language = doc.get("language") if isinstance(doc.get("language"), str) else None
        duration = (
            timedelta(seconds=float(doc["duration"]))
            if isinstance(doc.get("duration"), (int, float))
            else timedelta(0)
        )

        segments: List[TranscribedSegment] = []
        segs = doc.get("segments")
        if isinstance(segs, list):
            for s in segs:
                if not isinstance(s, dict):
                    continue
                seg_text = s.get("text") if isinstance(s.get("text"), str) else ""
                seg_start = float(s["start"]) if isinstance(s.get("start"), (int, float)) else 0.0
                seg_end = float(s["end"]) if isinstance(s.get("end"), (int, float)) else seg_start
                segments.append(
                    TranscribedSegment(
                        text=seg_text,
                        offset=timedelta(seconds=seg_start),
                        duration=timedelta(seconds=max(0.0, seg_end - seg_start)),
                        language=language,
                        confidence=0.0,
                    )
                )

        return TranscriptionResult(text, language, tuple(segments), duration)


__all__ = ["OpenAiSpeechRecognizer"]
