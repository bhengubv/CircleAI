# speech/cloud/cartesia_speech_synthesizer.py
#
# Port of CircleAI.Speech.Cloud/CartesiaSpeechSynthesizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechSynthesizer backed by Cartesia Sonic /v1/tts/bytes. Bearer auth
# + Cartesia-Version header + JSON body (model_id / transcript / voice{mode,id} /
# output_format{container,encoding,sample_rate} / language). Returns raw PCM-16
# mono. Fail-soft on missing key / non-2xx.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The JSON body rides body_json (same
# nested wire shape as the C# anonymous object); the raw PCM response comes back
# on HttpResponse.content_bytes.

from __future__ import annotations

import logging
from datetime import timedelta
from typing import Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechSynthesizer, SynthesisResult
from ._audio_http import bearer_auth, combine_uri, is_null_or_whitespace
from .options import CartesiaTtsOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.CartesiaSpeechSynthesizer")


def _empty() -> SynthesisResult:
    return SynthesisResult(b"", 0, timedelta(0))


class CartesiaSpeechSynthesizer(ISpeechSynthesizer):
    """(3.3.0) Cartesia Sonic-backed :class:`ISpeechSynthesizer`.

    Mirrors ``CircleAI.Speech.Cloud.CartesiaSpeechSynthesizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: CartesiaTtsOptions,
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
        return "cartesia-tts"

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

        body = {
            "model_id": self._options.model,
            "transcript": text,
            "voice": {"mode": "id", "id": voice},
            "output_format": {
                "container": self._options.output_container,
                "encoding": self._options.output_encoding,
                "sample_rate": self._options.pcm_sample_rate_hz,
            },
            "language": language_hint if language_hint is not None else "en",
        }

        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, "/v1/tts/bytes"),
                headers={
                    "Authorization": bearer_auth(self._options.api_key or ""),
                    "Cartesia-Version": self._options.cartesia_version,
                },
                body_json=body,
            )
        )
        if not resp.is_success:
            self._logger.warning("Cartesia TTS returned %s", resp.status_code)
            return _empty()

        data = resp.content_bytes
        samples = len(data) // 2
        return SynthesisResult(
            data,
            self._options.pcm_sample_rate_hz,
            timedelta(seconds=samples / self._options.pcm_sample_rate_hz),
        )


__all__ = ["CartesiaSpeechSynthesizer"]
