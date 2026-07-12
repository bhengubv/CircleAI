# speech/cloud/openai_speech_synthesizer.py
#
# Port of CircleAI.Speech.Cloud/OpenAiSpeechSynthesizer.cs (C# — the EXACT spec).
#
# (3.2.0) ISpeechSynthesizer backed by OpenAI's /v1/audio/speech endpoint.
# response_format=pcm so the bytes we return are real PCM-16 mono at
# PcmSampleRateHz (24 kHz per OpenAI's docs). Fail-soft: empty ApiKey returns an
# empty SynthesisResult.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The JSON body rides body_json; the raw
# PCM response comes back on HttpResponse.content_bytes (C#
# ReadAsByteArrayAsync). Duration = samples / rate, samples = bytes / 2.

from __future__ import annotations

import logging
from datetime import timedelta
from typing import Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechSynthesizer, SynthesisResult
from ._audio_http import bearer_auth, combine_uri, is_null_or_whitespace
from .options import OpenAiVoiceOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.OpenAiSpeechSynthesizer")


def _empty() -> SynthesisResult:
    return SynthesisResult(b"", 0, timedelta(0))


class OpenAiSpeechSynthesizer(ISpeechSynthesizer):
    """(3.2.0) :class:`ISpeechSynthesizer` backed by OpenAI TTS. Returns PCM-16
    mono at ``OpenAiVoiceOptions.pcm_sample_rate_hz``. Mirrors
    ``CircleAI.Speech.Cloud.OpenAiSpeechSynthesizer``.
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
        return "openai-tts"

    @property
    def is_configured(self) -> bool:
        return not is_null_or_whitespace(self._options.api_key)

    async def synthesize_async(
        self,
        text: str,
        voice_id: Optional[str] = None,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> SynthesisResult:
        if not self.is_configured:
            return _empty()

        resolved_voice = self._options.default_voice if is_null_or_whitespace(voice_id) else voice_id

        response = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, "/v1/audio/speech"),
                headers={"Authorization": bearer_auth(self._options.api_key or "")},
                body_json={
                    "model": self._options.speech_model,
                    "input": text,
                    "voice": resolved_voice,
                    "response_format": "pcm",
                },
            )
        )
        if not response.is_success:
            self._logger.warning(
                "OpenAI synthesis returned %s: %s", response.status_code, response.text
            )
            return _empty()

        data = response.content_bytes
        samples = len(data) // 2
        duration = timedelta(seconds=samples / self._options.pcm_sample_rate_hz)
        return SynthesisResult(data, self._options.pcm_sample_rate_hz, duration)


__all__ = ["OpenAiSpeechSynthesizer"]
