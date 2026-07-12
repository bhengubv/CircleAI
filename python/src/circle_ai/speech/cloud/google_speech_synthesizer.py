# speech/cloud/google_speech_synthesizer.py
#
# Port of CircleAI.Speech.Cloud/GoogleSpeechSynthesizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechSynthesizer backed by Google Cloud TTS v1 /v1/text:synthesize.
# API-key auth; input.text + voice + audioConfig LINEAR16 JSON body. The response
# carries base64 "audioContent" — a WAV envelope that the C# strips back to raw
# PCM. Fail-soft on missing key / non-2xx.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The JSON body rides body_json (the
# fetcher serialises it — note the C# uses JsonSerializer.Serialize(text) for the
# input string, which is exactly json.dumps of a str, so body_json is faithful);
# the response is read off resp.json() then base64-decoded + WAV-stripped.

from __future__ import annotations

import base64
import logging
from datetime import timedelta
from typing import Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechSynthesizer, SynthesisResult
from ._audio_http import combine_uri, is_null_or_whitespace, strip_wav_header
from .options import GoogleTtsOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.GoogleSpeechSynthesizer")


def _empty() -> SynthesisResult:
    return SynthesisResult(b"", 0, timedelta(0))


def _escape(value: str) -> str:
    from urllib.parse import quote

    return quote(value, safe="-_.~")


class GoogleSpeechSynthesizer(ISpeechSynthesizer):
    """(3.3.0) Google-backed :class:`ISpeechSynthesizer`.

    Mirrors ``CircleAI.Speech.Cloud.GoogleSpeechSynthesizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: GoogleTtsOptions,
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
        return "google-tts"

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

        voice = self._options.default_voice_name if is_null_or_whitespace(voice_id) else voice_id
        lang = self._options.language_code if is_null_or_whitespace(language_hint) else language_hint

        body = {
            "input": {"text": text},
            "voice": {"languageCode": lang, "name": voice},
            "audioConfig": {
                "audioEncoding": "LINEAR16",
                "sampleRateHertz": self._options.pcm_sample_rate_hz,
            },
        }

        path = f"/v1/text:synthesize?key={_escape(self._options.api_key or '')}"
        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, path),
                body_json=body,
            )
        )
        if not resp.is_success:
            self._logger.warning("Google TTS returned %s", resp.status_code)
            return _empty()

        doc = resp.json()
        if not isinstance(doc, dict) or "audioContent" not in doc:
            return _empty()
        b64 = doc.get("audioContent")
        if not b64 or not isinstance(b64, str):
            return _empty()

        try:
            raw = base64.b64decode(b64)
        except (ValueError, TypeError):
            return _empty()
        # Google returns a WAV envelope — strip it.
        pcm = strip_wav_header(raw)
        samples = len(pcm) // 2
        return SynthesisResult(
            pcm,
            self._options.pcm_sample_rate_hz,
            timedelta(seconds=samples / self._options.pcm_sample_rate_hz),
        )


__all__ = ["GoogleSpeechSynthesizer"]
