# speech/cloud/elevenlabs_speech_synthesizer.py
#
# Port of CircleAI.Speech.Cloud/ElevenLabsSpeechSynthesizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechSynthesizer backed by ElevenLabs /v1/text-to-speech/{voice}.
# xi-api-key header; output_format=pcm_24000 (query) returns raw PCM-16 mono. The
# sample rate is parsed out of output_format (pcm_(\d+)) with the options rate as
# fallback. Fail-soft on missing key / non-2xx.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The JSON body rides body_json; the raw
# PCM response comes back on HttpResponse.content_bytes.

from __future__ import annotations

import logging
from datetime import timedelta
from typing import Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechSynthesizer, SynthesisResult
from ._audio_http import combine_uri, is_null_or_whitespace, parse_pcm_rate
from .options import ElevenLabsOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.ElevenLabsSpeechSynthesizer")


def _empty() -> SynthesisResult:
    return SynthesisResult(b"", 0, timedelta(0))


def _escape(value: str) -> str:
    from urllib.parse import quote

    return quote(value, safe="-_.~")


class ElevenLabsSpeechSynthesizer(ISpeechSynthesizer):
    """(3.3.0) ElevenLabs-backed :class:`ISpeechSynthesizer`.

    Mirrors ``CircleAI.Speech.Cloud.ElevenLabsSpeechSynthesizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: ElevenLabsOptions,
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
        return "elevenlabs"

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

        voice = self._options.default_voice_id if is_null_or_whitespace(voice_id) else voice_id
        rate = parse_pcm_rate(self._options.output_format, fallback=self._options.pcm_sample_rate_hz)

        path = f"/v1/text-to-speech/{_escape(voice)}?output_format={self._options.output_format}"
        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, path),
                headers={"xi-api-key": self._options.api_key or ""},
                body_json={"text": text, "model_id": self._options.model},
            )
        )
        if not resp.is_success:
            self._logger.warning("ElevenLabs returned %s", resp.status_code)
            return _empty()

        data = resp.content_bytes
        samples = len(data) // 2
        return SynthesisResult(data, rate, timedelta(seconds=samples / rate))


__all__ = ["ElevenLabsSpeechSynthesizer"]
