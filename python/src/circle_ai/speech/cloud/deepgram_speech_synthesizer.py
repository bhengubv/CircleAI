# speech/cloud/deepgram_speech_synthesizer.py
#
# Port of CircleAI.Speech.Cloud/DeepgramSpeechSynthesizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechSynthesizer backed by Deepgram Aura /v1/speak. "Token <key>"
# auth + JSON body { text }; encoding=linear16 returns raw PCM-16 mono at the
# requested sample rate. Fail-soft on missing key / non-2xx.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. JSON body rides body_json; the raw PCM
# response comes back on HttpResponse.content_bytes.

from __future__ import annotations

import logging
from datetime import timedelta
from typing import Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechSynthesizer, SynthesisResult
from ._audio_http import combine_uri, is_null_or_whitespace
from .options import DeepgramTtsOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.DeepgramSpeechSynthesizer")


def _empty() -> SynthesisResult:
    return SynthesisResult(b"", 0, timedelta(0))


def _escape(value: str) -> str:
    from urllib.parse import quote

    return quote(value, safe="-_.~")


class DeepgramSpeechSynthesizer(ISpeechSynthesizer):
    """(3.3.0) Deepgram Aura-backed :class:`ISpeechSynthesizer`.

    Mirrors ``CircleAI.Speech.Cloud.DeepgramSpeechSynthesizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: DeepgramTtsOptions,
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
        return "deepgram-aura"

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

        voice = self._options.voice if is_null_or_whitespace(voice_id) else voice_id
        path = (
            f"/v1/speak?model={_escape(voice)}&encoding=linear16"
            f"&sample_rate={self._options.pcm_sample_rate_hz}"
        )

        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, path),
                headers={"Authorization": f"Token {self._options.api_key or ''}"},
                body_json={"text": text},
            )
        )
        if not resp.is_success:
            self._logger.warning("Deepgram Aura returned %s", resp.status_code)
            return _empty()

        data = resp.content_bytes
        samples = len(data) // 2
        return SynthesisResult(
            data,
            self._options.pcm_sample_rate_hz,
            timedelta(seconds=samples / self._options.pcm_sample_rate_hz),
        )


__all__ = ["DeepgramSpeechSynthesizer"]
