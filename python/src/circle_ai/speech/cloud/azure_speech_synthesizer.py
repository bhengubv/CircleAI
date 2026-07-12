# speech/cloud/azure_speech_synthesizer.py
#
# Port of CircleAI.Speech.Cloud/AzureSpeechSynthesizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechSynthesizer backed by Azure Cognitive Services TTS. SSML body
# (application/ssml+xml) + X-Microsoft-OutputFormat=raw-{rate/1000}khz-16bit-mono-pcm
# returns raw PCM-16 mono. Fail-soft unless BOTH api_key AND base_address are set.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The SSML body rides body_text with the
# application/ssml+xml content-type; the raw PCM response comes back on
# HttpResponse.content_bytes.

from __future__ import annotations

import html
import logging
from datetime import timedelta
from typing import Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechSynthesizer, SynthesisResult
from ._audio_http import combine_uri, is_null_or_whitespace
from .options import AzureTtsOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.AzureSpeechSynthesizer")


def _empty() -> SynthesisResult:
    return SynthesisResult(b"", 0, timedelta(0))


class AzureSpeechSynthesizer(ISpeechSynthesizer):
    """(3.3.0) Azure-backed :class:`ISpeechSynthesizer`.

    Mirrors ``CircleAI.Speech.Cloud.AzureSpeechSynthesizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: AzureTtsOptions,
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
        return "azure-tts"

    @property
    def is_configured(self) -> bool:
        return not is_null_or_whitespace(self._options.api_key) and self._options.base_address is not None

    async def synthesize_async(
        self,
        text: str,
        voice_id: Optional[str] = None,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> SynthesisResult:
        if not self.is_configured:
            return _empty()

        voice = self._options.default_voice_name if is_null_or_whitespace(voice_id) else voice_id
        lang = self._options.language_code if is_null_or_whitespace(language_hint) else language_hint
        rate = self._options.pcm_sample_rate_hz

        ssml = (
            f"<speak version='1.0' xml:lang='{lang}'>\n"
            f"  <voice name='{voice}'>{html.escape(text, quote=True)}</voice>\n"
            f"</speak>"
        )

        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, "/cognitiveservices/v1"),
                headers={
                    "Ocp-Apim-Subscription-Key": self._options.api_key or "",
                    "X-Microsoft-OutputFormat": f"raw-{rate // 1000}khz-16bit-mono-pcm",
                    "User-Agent": "CircleAI",
                },
                body_text=ssml,
                content_type="application/ssml+xml",
            )
        )
        if not resp.is_success:
            self._logger.warning("Azure TTS returned %s", resp.status_code)
            return _empty()

        data = resp.content_bytes
        samples = len(data) // 2
        return SynthesisResult(data, rate, timedelta(seconds=samples / rate))


__all__ = ["AzureSpeechSynthesizer"]
