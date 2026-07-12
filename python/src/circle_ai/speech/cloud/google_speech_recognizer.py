# speech/cloud/google_speech_recognizer.py
#
# Port of CircleAI.Speech.Cloud/GoogleSpeechRecognizer.cs (C# — the EXACT spec).
#
# (3.3.0) ISpeechRecognizer backed by Google Cloud Speech-to-Text v1. API-key
# auth (?key=…); audio base64'd as LINEAR16 mono in the JSON body. Picks the top
# alternative across results, concatenating transcripts and flattening word
# offsets (Google encodes durations as e.g. "1.500s"). Fail-soft on missing key /
# non-2xx.
#
# The C# drives HttpClient directly; the Python port injects the shared
# circle_ai.integration.http.IHttpFetcher. The JSON body rides body_json (the
# fetcher serialises it — same wire shape as the C# StringContent JSON); the
# response is read off resp.json().

from __future__ import annotations

import base64
import logging
from datetime import timedelta
from typing import List, Optional

from ...integration.http import HttpRequest, IHttpFetcher
from ..contracts import ISpeechRecognizer, TranscribedSegment, TranscriptionResult
from ._audio_http import combine_uri, is_null_or_whitespace
from .options import GoogleSpeechOptions

_logger = logging.getLogger("CircleAI.Speech.Cloud.GoogleSpeechRecognizer")


def _empty() -> TranscriptionResult:
    return TranscriptionResult("", None, (), timedelta(0))


def _escape(value: str) -> str:
    from urllib.parse import quote

    return quote(value, safe="-_.~")


def _parse_seconds(element: dict, prop: str) -> float:
    # Google encodes durations as e.g. "1.500s".
    if not isinstance(element, dict) or prop not in element:
        return 0.0
    s = element.get(prop)
    if not isinstance(s, str) or s.strip() == "":
        return 0.0
    if s.endswith("s"):
        s = s[:-1]
    try:
        return float(s)
    except ValueError:
        return 0.0


class GoogleSpeechRecognizer(ISpeechRecognizer):
    """(3.3.0) Google-backed :class:`ISpeechRecognizer`.

    Mirrors ``CircleAI.Speech.Cloud.GoogleSpeechRecognizer``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: GoogleSpeechOptions,
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
        return "google-stt"

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

        lang = self._options.language_code if is_null_or_whitespace(language_hint) else language_hint
        audio_b64 = base64.b64encode(bytes(audio_pcm16_mono)).decode("ascii")

        body = {
            "config": {
                "encoding": "LINEAR16",
                "sampleRateHertz": sample_rate_hz,
                "languageCode": lang,
                "enableWordTimeOffsets": True,
                "enableWordConfidence": True,
            },
            "audio": {"content": audio_b64},
        }

        path = f"/v1/speech:recognize?key={_escape(self._options.api_key or '')}"
        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=combine_uri(self._options.base_address, path),
                body_json=body,
            )
        )
        if not resp.is_success:
            self._logger.warning("Google STT returned %s", resp.status_code)
            return _empty()

        doc = resp.json()
        if not isinstance(doc, dict):
            return _empty()

        # Pick the top alternative across results. Reproduce the C# StringBuilder
        # exactly: prepend a space only when content already exists, then append
        # this alternative's transcript (even when empty).
        all_text = ""
        segments: List[TranscribedSegment] = []
        results = doc.get("results")
        if isinstance(results, list):
            for r in results:
                if not isinstance(r, dict):
                    continue
                alts = r.get("alternatives")
                if not isinstance(alts, list) or len(alts) == 0:
                    continue
                alt = alts[0]
                if not isinstance(alt, dict):
                    continue
                transcript = alt.get("transcript") if isinstance(alt.get("transcript"), str) else ""
                if len(all_text) > 0:
                    all_text += " "
                all_text += transcript

                words = alt.get("words")
                if isinstance(words, list):
                    for w in words:
                        if not isinstance(w, dict):
                            continue
                        start = _parse_seconds(w, "startTime")
                        end = _parse_seconds(w, "endTime")
                        confidence = (
                            float(w["confidence"])
                            if isinstance(w.get("confidence"), (int, float))
                            else 0.0
                        )
                        segments.append(
                            TranscribedSegment(
                                text=w.get("word") if isinstance(w.get("word"), str) else "",
                                offset=timedelta(seconds=start),
                                duration=timedelta(seconds=max(0.0, end - start)),
                                language=lang,
                                confidence=confidence,
                            )
                        )

        return TranscriptionResult(all_text, lang, tuple(segments), timedelta(0))


__all__ = ["GoogleSpeechRecognizer"]
