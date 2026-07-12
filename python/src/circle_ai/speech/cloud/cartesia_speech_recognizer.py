# speech/cloud/cartesia_speech_recognizer.py
#
# Port of CircleAI.Speech.Cloud/CartesiaSpeechRecognizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechRecognizer backed by Cartesia's /v1/transcribe endpoint. Bearer
# auth + Cartesia-Version header + multipart upload of WAV-wrapped audio (file +
# model + optional language parts). Response: text / language / duration, no
# per-word segments. Fail-soft on missing key / non-2xx.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The multipart body is built with the
# _audio_http.multipart_form_data helper and rides body_bytes with the multipart
# content-type; JSON is read off resp.json().

from __future__ import annotations

import logging
from datetime import timedelta
from typing import Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechRecognizer, TranscriptionResult
from ._audio_http import (
    bearer_auth,
    combine_uri,
    is_null_or_whitespace,
    multipart_form_data,
    wrap_pcm_as_wav,
)
from .options import CartesiaSttOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.CartesiaSpeechRecognizer")


def _empty() -> TranscriptionResult:
    return TranscriptionResult("", None, (), timedelta(0))


class CartesiaSpeechRecognizer(ISpeechRecognizer):
    """(3.3.0) Cartesia-backed :class:`ISpeechRecognizer`.

    Mirrors ``CircleAI.Speech.Cloud.CartesiaSpeechRecognizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: CartesiaSttOptions,
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
        return "cartesia-stt"

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

        wav = wrap_pcm_as_wav(audio_pcm16_mono, sample_rate_hz)

        string_parts = [("model", self._options.model)]
        if not is_null_or_whitespace(language_hint):
            string_parts.append(("language", language_hint))

        body, content_type = multipart_form_data(
            file_part=("file", "audio.wav", "audio/wav", wav),
            string_parts=string_parts,
        )

        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, "/v1/transcribe"),
                headers={
                    "Authorization": bearer_auth(self._options.api_key or ""),
                    "Cartesia-Version": self._options.cartesia_version,
                },
                body_bytes=body,
                content_type=content_type,
            )
        )
        if not resp.is_success:
            self._logger.warning("Cartesia STT returned %s", resp.status_code)
            return _empty()

        doc = resp.json()
        if not isinstance(doc, dict):
            return _empty()

        text = doc.get("text") if isinstance(doc.get("text"), str) else ""
        lang = doc.get("language") if isinstance(doc.get("language"), str) else language_hint
        duration = (
            timedelta(seconds=float(doc["duration"]))
            if isinstance(doc.get("duration"), (int, float))
            else timedelta(0)
        )

        return TranscriptionResult(text, lang, (), duration)


__all__ = ["CartesiaSpeechRecognizer"]
