# plivo_carrier.py
#
# Port of CircleAI.Telephony.Plivo/PlivoCarrier.cs (C# — the EXACT spec).
#
# (3.3.0) Plivo v1 REST API adapter. Speaks Basic auth (AuthId + AuthToken), the
# /v1/Account/{AuthId}/ namespace, and the AnswerUrl-driven Audio Streaming flow.
#
# The C# drives HttpClient directly (BaseAddress + default Basic header, relative
# paths, FormUrlEncodedContent bodies, a DELETE for hangup, JsonDocument reads).
# The Python port injects the shared circle_ai.integration.http.IHttpFetcher:
# absolute URLs (combine_uri), the Basic Authorization header per request, form
# bodies ride body_bytes with the application/x-www-form-urlencoded content-type,
# and the answer-URL query is composed with the same UriBuilder-append semantics
# (existing query preserved, stream= appended, value Uri.EscapeDataString'd).
# Fail-soft when credentials are missing, exactly as the C#.

from __future__ import annotations

import logging
from datetime import datetime, timezone
from decimal import Decimal
from typing import List, Optional
from urllib.parse import urlsplit, urlunsplit

from ..integration.http import HttpRequest, IHttpFetcher
from .carriers_http import (
    FORM_CONTENT_TYPE,
    basic_auth,
    combine_uri,
    escape_data_string,
    form_urlencoded,
    parse_decimal,
)
from .contracts import ICallSession, ITelephonyCarrier, OutboundDialOptions
from .plivo_call_session import PlivoCallSession, PlivoPendingMediaStream
from .plivo_options import PlivoOptions
from .primitives import CallDirection, CallInfo, CallMediaFormat, ProvisionedNumber

_logger = logging.getLogger("CircleAI.Telephony.Plivo.PlivoCarrier")
_ZERO = Decimal(0)


def _is_null_or_whitespace(s: Optional[str]) -> bool:
    return s is None or s.strip() == ""


class PlivoCarrier(ITelephonyCarrier):
    """(3.3.0) :class:`ITelephonyCarrier` backed by Plivo's v1 REST API.
    Fail-soft when credentials missing. Mirrors
    ``CircleAI.Telephony.Plivo.PlivoCarrier``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: PlivoOptions,
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
    def carrier_id(self) -> str:
        return "plivo"

    @property
    def is_configured(self) -> bool:
        return not _is_null_or_whitespace(self._options.auth_id) and not _is_null_or_whitespace(
            self._options.auth_token
        )

    def _auth_headers(self) -> dict:
        return {"Authorization": basic_auth(self._options.auth_id or "", self._options.auth_token or "")}

    def _url(self, path: str) -> str:
        return combine_uri(self._options.base_address, path)

    async def _get(self, path: str) -> "object":
        return await self._http.send_async(
            HttpRequest(method="GET", url=self._url(path), headers=self._auth_headers())
        )

    async def _post_form(self, path: str, pairs) -> "object":
        return await self._http.send_async(
            HttpRequest(
                method="POST",
                url=self._url(path),
                headers=self._auth_headers(),
                body_bytes=form_urlencoded(pairs),
                content_type=FORM_CONTENT_TYPE,
            )
        )

    async def _delete(self, path: str) -> "object":
        return await self._http.send_async(
            HttpRequest(method="DELETE", url=self._url(path), headers=self._auth_headers())
        )

    async def provision_number_async(
        self,
        country_code: str,
        area_code: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProvisionedNumber:
        self._ensure_configured()

        auth_id = self._options.auth_id
        path = f"/v1/Account/{auth_id}/PhoneNumber/?country_iso={country_code}&limit=1"
        if not _is_null_or_whitespace(area_code):
            path += f"&pattern={escape_data_string(area_code)}"

        search = (await self._get(path)).ensure_success()
        doc = search.json()
        objects = doc.get("objects") if isinstance(doc, dict) else None
        first = objects[0] if isinstance(objects, list) and objects else None
        if not isinstance(first, dict):
            raise RuntimeError(
                f"Plivo has no available numbers in country='{country_code}', areaCode='{area_code}'."
            )

        phone_number = first.get("number")

        buy_path = f"/v1/Account/{auth_id}/PhoneNumber/{phone_number}/"
        (await self._post_form(buy_path, [("app_id", "")])).ensure_success()

        cost = parse_decimal(first, "monthly_rental_rate")
        return ProvisionedNumber(
            phone_number=phone_number,
            carrier_id=self.carrier_id,
            provisioned_at_utc=datetime.now(timezone.utc),
            monthly_recurring_cost=cost if cost is not None else _ZERO,
        )

    async def configure_inbound_webhook_async(
        self,
        phone_number: str,
        inbound_webhook: str,
        *,
        ct: Optional[object] = None,
    ) -> None:
        self._ensure_configured()

        path = f"/v1/Account/{self._options.auth_id}/Number/{phone_number}/"
        (
            await self._post_form(
                path,
                [("answer_url", inbound_webhook), ("answer_method", "POST")],
            )
        ).ensure_success()

    async def dial_async(
        self,
        from_number: str,
        to_number: str,
        stream_url: str,
        options: Optional[OutboundDialOptions] = None,
        *,
        ct: Optional[object] = None,
    ) -> ICallSession:
        self._ensure_configured()
        if _is_null_or_whitespace(self._options.answer_url_base):
            raise RuntimeError(
                "Plivo dial_async requires PlivoOptions.answer_url_base. The host must "
                "serve XML containing a <Stream/> verb pointing to the stream_url."
            )
        opts = options if options is not None else OutboundDialOptions()

        # Compose the answer URL with the stream wss:// embedded as a query param
        # (UriBuilder-append: preserve existing query, add stream=<escaped>).
        answer_url = _append_stream_query(self._options.answer_url_base, stream_url)

        form_pairs = [
            ("from", opts.caller_id_override if opts.caller_id_override else from_number),
            ("to", to_number),
            ("answer_url", answer_url),
            ("answer_method", "POST"),
            ("ring_timeout", str(opts.ring_timeout_seconds)),
        ]
        if opts.detect_answering_machine:
            form_pairs.append(("machine_detection", "true"))

        path = f"/v1/Account/{self._options.auth_id}/Call/"
        resp = (await self._post_form(path, form_pairs)).ensure_success()
        doc = resp.json()
        request_uuid = doc.get("request_uuid") if isinstance(doc, dict) else None

        pending = PlivoPendingMediaStream(
            CallInfo(
                call_id=request_uuid,
                direction=CallDirection.OUTBOUND,
                from_=from_number,
                to=to_number,
                carrier_id=self.carrier_id,
                media_format=CallMediaFormat.MULAW8000,
                started_at_utc=datetime.now(timezone.utc),
            )
        )
        return PlivoCallSession(pending, self)

    async def list_numbers_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        if not self.is_configured:
            return []

        path = f"/v1/Account/{self._options.auth_id}/Number/?limit=100"
        resp = await self._get(path)
        if not resp.is_success:
            self._logger.warning("Plivo ListNumbers returned %s", resp.status_code)
            return []

        doc = resp.json()
        result: List[ProvisionedNumber] = []
        arr = doc.get("objects") if isinstance(doc, dict) else None
        if isinstance(arr, list):
            for item in arr:
                if not isinstance(item, dict):
                    continue
                pn = item.get("number")
                result.append(
                    ProvisionedNumber(
                        phone_number=pn,
                        carrier_id=self.carrier_id,
                        provisioned_at_utc=datetime.now(timezone.utc),
                        monthly_recurring_cost=_ZERO,
                    )
                )
        return result

    async def end_call_async(self, call_uuid: str, *, ct: Optional[object] = None) -> None:
        """(3.3.0) Hang up an in-progress call. Used by sessions on hang-up."""
        if not self.is_configured:
            return
        resp = await self._delete(f"/v1/Account/{self._options.auth_id}/Call/{call_uuid}/")
        if not resp.is_success:
            self._logger.warning("Plivo Hangup %s returned %s", call_uuid, resp.status_code)

    async def transfer_call_async(
        self, call_uuid: str, target_number: str, *, ct: Optional[object] = None
    ) -> None:
        """(3.3.0) Transfer an in-progress call by replaying the answer XML."""
        self._ensure_configured()
        xml = f"<Response><Dial><Number>{target_number}</Number></Dial></Response>"
        aleg_url = f"data:application/xml,{escape_data_string(xml)}"
        resp = await self._post_form(
            f"/v1/Account/{self._options.auth_id}/Call/{call_uuid}/",
            [("aleg_url", aleg_url), ("aleg_method", "POST")],
        )
        if not resp.is_success:
            self._logger.warning("Plivo Transfer %s returned %s", call_uuid, resp.status_code)

    def _ensure_configured(self) -> None:
        if not self.is_configured:
            raise RuntimeError(
                "Plivo carrier is not configured. Set PlivoOptions.auth_id and auth_token "
                "before calling REST operations."
            )


def _append_stream_query(answer_url_base: str, stream_url: str) -> str:
    """C# ``UriBuilder`` append: preserve any existing query, add
    ``stream=<Uri.EscapeDataString(stream_url)>`` joined with ``&`` (or nothing
    when there was no existing query)."""
    parts = urlsplit(answer_url_base)
    existing = parts.query
    separator = "" if not existing else "&"
    new_query = existing + separator + "stream=" + escape_data_string(stream_url)
    return urlunsplit((parts.scheme, parts.netloc, parts.path, new_query, parts.fragment))


__all__ = ["PlivoCarrier"]
