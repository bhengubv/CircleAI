# speech/cloud/playht_speech_synthesizer.py
#
# Port of CircleAI.Speech.Cloud/PlayHtSpeechSynthesizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechSynthesizer backed by Play.HT streaming TTS /api/v2/tts/stream.
# "Bearer <key>" Authorization + X-USER-ID + Accept: audio/raw headers; JSON body
# (text / voice / voice_engine / output_format=raw / sample_rate / language).
# Returns raw PCM-16 audio. is_configured requires BOTH api_key AND user_id.
# Fail-soft on missing creds / non-2xx.
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
from ._audio_http import combine_uri, is_null_or_whitespace
from .options import PlayHtOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.PlayHtSpeechSynthesizer")


def _empty() -> SynthesisResult:
    return SynthesisResult(b"", 0, timedelta(0))


class PlayHtSpeechSynthesizer(ISpeechSynthesizer):
    """(3.3.0) Play.HT-backed :class:`ISpeechSynthesizer`.

    Mirrors ``CircleAI.Speech.Cloud.PlayHtSpeechSynthesizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: PlayHtOptions,
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
        return "playht"

    @property
    def is_configured(self) -> bool:
        return not is_null_or_whitespace(self._options.api_key) and not is_null_or_whitespace(
            self._options.user_id
        )

    async def synthesize_async(
        self,
        text: str,
        voice_id: Optional[str] = None,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> SynthesisResult:
        if not self.is_configured:
            return _empty()

        voice = self._options.default_voice if is_null_or_whitespace(voice_id) else voice_id

        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, "/api/v2/tts/stream"),
                headers={
                    "Authorization": f"Bearer {self._options.api_key or ''}",
                    "X-USER-ID": self._options.user_id or "",
                    "Accept": "audio/raw",
                },
                body_json={
                    "text": text,
                    "voice": voice,
                    "voice_engine": self._options.model,
                    "output_format": "raw",
                    "sample_rate": self._options.pcm_sample_rate_hz,
                    "language": language_hint if language_hint is not None else "english",
                },
            )
        )
        if not resp.is_success:
            self._logger.warning("Play.HT returned %s", resp.status_code)
            return _empty()

        data = resp.content_bytes
        samples = len(data) // 2
        return SynthesisResult(
            data,
            self._options.pcm_sample_rate_hz,
            timedelta(seconds=samples / self._options.pcm_sample_rate_hz),
        )


__all__ = ["PlayHtSpeechSynthesizer"]
