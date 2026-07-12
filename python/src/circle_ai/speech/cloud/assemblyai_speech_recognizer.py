# speech/cloud/assemblyai_speech_recognizer.py
#
# Port of CircleAI.Speech.Cloud/AssemblyAiSpeechRecognizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechRecognizer backed by AssemblyAI. Three-step flow: upload
# WAV-wrapped bytes to /v2/upload -> POST /v2/transcript with the upload_url ->
# poll /v2/transcript/{id} until status=completed (max 60 attempts of 500 ms =
# 30 s). Auth is the bare api key in the Authorization header. Fail-soft on
# missing key / non-2xx / timeout / error.
#
# The C# drives HttpClient directly (ByteArrayContent upload / StringContent JSON
# submit / GET poll, Task.Delay between polls). The Python port injects the shared
# circle_ai.integration.http.IHttpFetcher: the upload body rides body_bytes with
# application/octet-stream, the submit body rides body_json, and asyncio.sleep
# reproduces the poll delay. JSON is read off resp.json(). Word offsets are in ms
# (start/end / 1000) and audio_duration in seconds.

from __future__ import annotations

import asyncio
import logging
from datetime import timedelta
from typing import List, Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechRecognizer, TranscribedSegment, TranscriptionResult
from ._audio_http import combine_uri, is_null_or_whitespace, wrap_pcm_as_wav
from .options import AssemblyAiOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.AssemblyAiSpeechRecognizer")


def _empty() -> TranscriptionResult:
    return TranscriptionResult("", None, (), timedelta(0))


class AssemblyAiSpeechRecognizer(ISpeechRecognizer):
    """(3.3.0) AssemblyAI-backed :class:`ISpeechRecognizer`.

    Mirrors ``CircleAI.Speech.Cloud.AssemblyAiSpeechRecognizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: AssemblyAiOptions,
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
        return "assemblyai"

    @property
    def is_configured(self) -> bool:
        return not is_null_or_whitespace(self._options.api_key)

    def _url(self, path: str) -> str:
        return combine_uri(self._options.base_address, path)

    def _auth(self) -> dict:
        return {"Authorization": self._options.api_key or ""}

    async def transcribe_async(
        self,
        audio_pcm16_mono: bytes,
        sample_rate_hz: int,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> TranscriptionResult:
        if not self.is_configured:
            return _empty()

        # 1) Upload audio.
        wav = wrap_pcm_as_wav(audio_pcm16_mono, sample_rate_hz)
        upload_resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=self._url("/v2/upload"),
                headers=self._auth(),
                body_bytes=wav,
                content_type="application/octet-stream",
            )
        )
        if not upload_resp.is_success:
            self._logger.warning("AssemblyAI upload returned %s", upload_resp.status_code)
            return _empty()
        upload_doc = upload_resp.json()
        upload_url = (
            upload_doc.get("upload_url")
            if isinstance(upload_doc, dict) and isinstance(upload_doc.get("upload_url"), str)
            else None
        )
        if is_null_or_whitespace(upload_url):
            return _empty()

        # 2) Submit transcript job.
        submit_body = {"audio_url": upload_url, "speech_model": self._options.speech_model}
        if not is_null_or_whitespace(language_hint):
            submit_body["language_code"] = language_hint

        submit_resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=self._url("/v2/transcript"),
                headers=self._auth(),
                body_json=submit_body,
            )
        )
        if not submit_resp.is_success:
            self._logger.warning("AssemblyAI submit returned %s", submit_resp.status_code)
            return _empty()
        submit_doc = submit_resp.json()
        transcript_id = (
            submit_doc.get("id")
            if isinstance(submit_doc, dict) and isinstance(submit_doc.get("id"), str)
            else None
        )
        if is_null_or_whitespace(transcript_id):
            return _empty()

        # 3) Poll until completed (max 60 attempts of 500 ms = 30 s).
        for _attempt in range(60):
            await asyncio.sleep(0.5)

            poll_resp = await self._http.send_async(
                HttpRequest(
                    method="GET",
                    url=self._url(f"/v2/transcript/{transcript_id}"),
                    headers=self._auth(),
                )
            )
            if not poll_resp.is_success:
                continue

            poll_doc = poll_resp.json()
            if not isinstance(poll_doc, dict):
                continue

            status = poll_doc.get("status") if isinstance(poll_doc.get("status"), str) else None
            if status == "completed":
                text = poll_doc.get("text") if isinstance(poll_doc.get("text"), str) else ""
                lang = (
                    poll_doc.get("language_code")
                    if isinstance(poll_doc.get("language_code"), str)
                    else language_hint
                )
                duration = (
                    timedelta(seconds=float(poll_doc["audio_duration"]))
                    if isinstance(poll_doc.get("audio_duration"), (int, float))
                    else timedelta(0)
                )

                segments: List[TranscribedSegment] = []
                words = poll_doc.get("words")
                if isinstance(words, list):
                    for w in words:
                        if not isinstance(w, dict):
                            continue
                        start = (
                            float(w["start"]) / 1000.0
                            if isinstance(w.get("start"), (int, float))
                            else 0.0
                        )
                        end = (
                            float(w["end"]) / 1000.0
                            if isinstance(w.get("end"), (int, float))
                            else start
                        )
                        confidence = (
                            float(w["confidence"])
                            if isinstance(w.get("confidence"), (int, float))
                            else 0.0
                        )
                        segments.append(
                            TranscribedSegment(
                                text=w.get("text") if isinstance(w.get("text"), str) else "",
                                offset=timedelta(seconds=start),
                                duration=timedelta(seconds=max(0.0, end - start)),
                                language=lang,
                                confidence=confidence,
                            )
                        )

                return TranscriptionResult(text, lang, tuple(segments), duration)
            if status == "error":
                err = poll_doc.get("error") if isinstance(poll_doc.get("error"), str) else None
                self._logger.warning("AssemblyAI transcript error: %s", err)
                return _empty()

        self._logger.warning("AssemblyAI transcript %s timed out after 30 s", transcript_id)
        return _empty()


__all__ = ["AssemblyAiSpeechRecognizer"]
