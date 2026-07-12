# telnyx_carrier.py
#
# Port of CircleAI.Telephony.Telnyx/TelnyxCarrier.cs (C# — the EXACT spec).
#
# (3.3.0) Telnyx v2 REST API adapter. Speaks Bearer-token auth, the /v2
# namespace, and Telnyx's Call Control surface for number provisioning +
# outbound dial + termination + transfer.
#
# The C# drives HttpClient directly (BaseAddress + default Bearer header,
# relative paths, StringContent application/json bodies + a PATCH for webhook
# config, JsonDocument reads). The Python port injects the shared
# circle_ai.integration.http.IHttpFetcher: absolute URLs (combine_uri), the
# Bearer Authorization header per request, JSON bodies ride body_json (the
# fetcher serialises them), and the C# JSON string-interpolation bodies are
# reproduced as dicts so the wire shape is identical. Fail-soft when credentials
# are missing, exactly as the C#.

from __future__ import annotations

import logging
from datetime import datetime, timezone
from decimal import Decimal
from typing import List, Optional

from ..integration.http import HttpRequest, IHttpFetcher
from .carriers_http import bearer_auth, combine_uri, escape_data_string, parse_decimal
from .contracts import ICallSession, ITelephonyCarrier, OutboundDialOptions
from .primitives import CallDirection, CallInfo, CallMediaFormat, ProvisionedNumber
from .telnyx_call_session import TelnyxCallSession, TelnyxPendingMediaStream
from .telnyx_options import TelnyxOptions

_logger = logging.getLogger("CircleAI.Telephony.Telnyx.TelnyxCarrier")
_ZERO = Decimal(0)


def _is_null_or_whitespace(s: Optional[str]) -> bool:
    return s is None or s.strip() == ""


class TelnyxCarrier(ITelephonyCarrier):
    """(3.3.0) :class:`ITelephonyCarrier` backed by Telnyx's v2 REST API.
    Fail-soft when credentials are missing. Mirrors
    ``CircleAI.Telephony.Telnyx.TelnyxCarrier``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: TelnyxOptions,
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
        return "telnyx"

    @property
    def is_configured(self) -> bool:
        return not _is_null_or_whitespace(self._options.api_key)

    def _auth_headers(self) -> dict:
        return {"Authorization": bearer_auth(self._options.api_key or "")}

    def _url(self, path: str) -> str:
        return combine_uri(self._options.base_address, path)

    async def _get(self, path: str) -> "object":
        return await self._http.send_async(
            HttpRequest(method="GET", url=self._url(path), headers=self._auth_headers())
        )

    async def _send_json(self, method: str, path: str, payload) -> "object":
        return await self._http.send_async(
            HttpRequest(
                method=method,
                url=self._url(path),
                headers=self._auth_headers(),
                body_json=payload,
            )
        )

    async def provision_number_async(
        self,
        country_code: str,
        area_code: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProvisionedNumber:
        self._ensure_configured()

        search_path = (
            f"/v2/available_phone_numbers?filter[country_code]={country_code}&filter[limit]=1"
        )
        if not _is_null_or_whitespace(area_code):
            search_path += f"&filter[national_destination_code]={escape_data_string(area_code)}"

        search = (await self._get(search_path)).ensure_success()
        doc = search.json()
        data = doc.get("data") if isinstance(doc, dict) else None
        first = data[0] if isinstance(data, list) and data else None
        if not isinstance(first, dict):
            raise RuntimeError(
                f"Telnyx has no available numbers in country='{country_code}', areaCode='{area_code}'."
            )

        phone_number = first.get("phone_number")

        # 2) Place a Number Order to purchase it.
        order_body = {"phone_numbers": [{"phone_number": phone_number}]}
        (await self._send_json("POST", "/v2/number_orders", order_body)).ensure_success()

        cost = _parse_monthly_cost(first)
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
        if _is_null_or_whitespace(self._options.call_control_connection_id):
            raise RuntimeError(
                "Telnyx ConfigureInboundWebhook requires call_control_connection_id on TelnyxOptions."
            )

        conn = self._options.call_control_connection_id
        # Update the Call Control Application's webhook URL (PATCH).
        (
            await self._send_json(
                "PATCH",
                f"/v2/call_control_applications/{conn}",
                {"webhook_event_url": inbound_webhook},
            )
        ).ensure_success()

        # Ensure the number is assigned to this connection (best-effort).
        assign_resp = await self._send_json(
            "PATCH",
            f"/v2/phone_numbers/{escape_data_string(phone_number)}",
            {"connection_id": conn},
        )
        if not assign_resp.is_success:
            self._logger.warning(
                "Telnyx assign number %s returned %s (may already be assigned)",
                phone_number,
                assign_resp.status_code,
            )

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
        if _is_null_or_whitespace(self._options.call_control_connection_id):
            raise RuntimeError(
                "Telnyx dial_async requires call_control_connection_id on TelnyxOptions."
            )
        opts = options if options is not None else OutboundDialOptions()

        body = {
            "connection_id": self._options.call_control_connection_id,
            "to": to_number,
            "from": opts.caller_id_override if opts.caller_id_override else from_number,
            "stream_url": stream_url,
            "stream_track": "both_tracks",
            "timeout_secs": opts.ring_timeout_seconds,
        }
        if opts.detect_answering_machine:
            body["answering_machine_detection"] = "detect"

        resp = (await self._send_json("POST", "/v2/calls", body)).ensure_success()
        doc = resp.json()
        data = doc.get("data") if isinstance(doc, dict) else None
        call_control_id = data.get("call_control_id") if isinstance(data, dict) else None

        pending = TelnyxPendingMediaStream(
            CallInfo(
                call_id=call_control_id,
                direction=CallDirection.OUTBOUND,
                from_=from_number,
                to=to_number,
                carrier_id=self.carrier_id,
                media_format=CallMediaFormat.PCM16000,
                started_at_utc=datetime.now(timezone.utc),
            )
        )
        return TelnyxCallSession(pending, self)

    async def list_numbers_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        if not self.is_configured:
            return []

        resp = await self._get("/v2/phone_numbers?page[size]=100")
        if not resp.is_success:
            self._logger.warning("Telnyx ListNumbers returned %s", resp.status_code)
            return []

        doc = resp.json()
        result: List[ProvisionedNumber] = []
        arr = doc.get("data") if isinstance(doc, dict) else None
        if isinstance(arr, list):
            for item in arr:
                if not isinstance(item, dict):
                    continue
                pn = item.get("phone_number")
                result.append(
                    ProvisionedNumber(
                        phone_number=pn,
                        carrier_id=self.carrier_id,
                        provisioned_at_utc=datetime.now(timezone.utc),
                        monthly_recurring_cost=_ZERO,
                    )
                )
        return result

    async def end_call_async(self, call_control_id: str, *, ct: Optional[object] = None) -> None:
        """(3.3.0) Hang up an in-progress call. Used by sessions on hang-up."""
        if not self.is_configured:
            return
        resp = await self._send_json(
            "POST", f"/v2/calls/{call_control_id}/actions/hangup", {}
        )
        if not resp.is_success:
            self._logger.warning("Telnyx Hangup %s returned %s", call_control_id, resp.status_code)

    async def transfer_call_async(
        self, call_control_id: str, target_number: str, *, ct: Optional[object] = None
    ) -> None:
        """(3.3.0) Transfer an in-progress call to a new destination."""
        self._ensure_configured()
        resp = await self._send_json(
            "POST",
            f"/v2/calls/{call_control_id}/actions/transfer",
            {"to": target_number},
        )
        if not resp.is_success:
            self._logger.warning("Telnyx Transfer %s returned %s", call_control_id, resp.status_code)

    def _ensure_configured(self) -> None:
        if not self.is_configured:
            raise RuntimeError(
                "Telnyx carrier is not configured. Set TelnyxOptions.api_key before "
                "calling REST operations."
            )


def _parse_monthly_cost(element) -> Optional[Decimal]:
    """Mirror ``ParseMonthlyCost``: read ``cost_information.monthly_cost`` as a
    number or numeric string; return ``None`` when absent/unparseable."""
    if not isinstance(element, dict):
        return None
    cost = element.get("cost_information")
    if not isinstance(cost, dict):
        return None
    return parse_decimal(cost, "monthly_cost")


__all__ = ["TelnyxCarrier"]
