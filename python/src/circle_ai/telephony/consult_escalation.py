# consult_escalation.py
#
# Port of CircleAI.Telephony ConsultEscalation.cs (C# — the EXACT spec).
#
# (3.3.0) Consult escalation: AI pauses the call, contacts a human expert
# out-of-band (chat / webhook / phone), conveys the question, receives an
# answer, and reads it back to the caller. Different from warm transfer — the
# caller stays with the AI, the human just answers behind the scenes.
#
# C# HttpClient + per-request timeout CTS -> the shared IHttpFetcher wrapped in
# asyncio.wait_for(timeout); a wait_for TimeoutError maps to the C# timeout
# branch that returns None. C# ILogger -> stdlib logging. The escalator walks
# channels in order; the first non-None answer wins, and any channel exception is
# logged-and-skipped exactly as the C# does.

from __future__ import annotations

import asyncio
import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import timedelta
from typing import List, Optional, Sequence

from ..integration.http import HttpRequest, IHttpFetcher

_logger = logging.getLogger("CircleAI.Telephony.ConsultEscalation")


@dataclass(frozen=True, slots=True)
class ConsultRequest:
    """(3.3.0) Question the AI asks a human expert.

    ``call_id``: source call id for the audit trail.
    ``question``: plain-English question text.
    ``context_json``: structured context (caller intent, last few utterances, record).
    ``urgency``: "normal" / "high".
    """

    call_id: str
    question: str
    context_json: str
    urgency: str = "normal"


@dataclass(frozen=True, slots=True)
class ConsultAnswer:
    """(3.3.0) Human reply.

    ``confidence`` == True means expert confirmed. Mirrors ``record(string
    Answer, bool Confidence, string? Notes)``.
    """

    answer: str
    confidence: bool
    notes: Optional[str] = None


class IConsultChannel(ABC):
    """(3.3.0) Channel for asking a human expert."""

    @property
    @abstractmethod
    def name(self) -> str:
        ...

    @abstractmethod
    async def ask_async(
        self, request: ConsultRequest, timeout: timedelta, *, ct: Optional[object] = None
    ) -> Optional[ConsultAnswer]:
        ...


class ConsultEscalator:
    """(3.3.0) Default escalation driver: try channels in order until one returns
    within the timeout."""

    def __init__(self, channels: Sequence[IConsultChannel], logger: Optional[logging.Logger] = None) -> None:
        if channels is None:
            raise ValueError("channels must not be None")
        self._channels: List[IConsultChannel] = list(channels)
        self._logger = logger if logger is not None else _logger

    async def escalate_async(
        self,
        request: ConsultRequest,
        timeout_per_channel: timedelta,
        *,
        ct: Optional[object] = None,
    ) -> Optional[ConsultAnswer]:
        """(3.3.0) Walk channels in order; first one to return a non-None answer wins."""
        if request is None:
            raise ValueError("request must not be None")
        for channel in self._channels:
            try:
                answer = await channel.ask_async(request, timeout_per_channel, ct=ct)
                if answer is not None:
                    self._logger.info("Consult %s answered by %s", request.call_id, channel.name)
                    return answer
            except Exception as ex:
                self._logger.warning("Consult channel %s threw: %s", channel.name, ex)
        return None


class HttpWebhookConsultChannel(IConsultChannel):
    """(3.3.0) HTTP webhook channel — POSTs the request, expects a JSON reply."""

    def __init__(self, http: IHttpFetcher, endpoint: str, name: str = "webhook") -> None:
        if http is None:
            raise ValueError("http must not be None")
        if endpoint is None:
            raise ValueError("endpoint must not be None")
        self._http = http
        self._endpoint = endpoint
        self._name = name

    @property
    def name(self) -> str:
        return self._name

    async def ask_async(
        self, request: ConsultRequest, timeout: timedelta, *, ct: Optional[object] = None
    ) -> Optional[ConsultAnswer]:
        # C# builds JsonContent.Create(request); mirror the record's field names.
        payload = {
            "callId": request.call_id,
            "question": request.question,
            "contextJson": request.context_json,
            "urgency": request.urgency,
        }
        try:
            resp = await asyncio.wait_for(
                self._http.send_async(HttpRequest(method="POST", url=self._endpoint, body_json=payload)),
                timeout=timeout.total_seconds(),
            )
        except asyncio.TimeoutError:
            return None

        if not resp.is_success:
            return None

        root = resp.json()
        if not isinstance(root, dict):
            return None
        answer = root.get("answer")
        if not answer or (isinstance(answer, str) and answer.isspace()):
            return None
        # confidence is true only when the JSON value is boolean true.
        confidence = root.get("confidence") is True
        notes = root.get("notes")
        return ConsultAnswer(answer, confidence, notes)
