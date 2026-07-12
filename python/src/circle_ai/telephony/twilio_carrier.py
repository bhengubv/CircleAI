# twilio_carrier.py
#
# Port of CircleAI.Telephony.Twilio/TwilioCarrier.cs (C# — the EXACT spec).
#
# (3.3.0) Twilio REST API adapter. Speaks to
# https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/... for number
# provisioning, webhook configuration, outbound dial, and call termination.
# Authenticates via HTTP Basic with AccountSid + AuthToken.
#
# The C# drives HttpClient directly (BaseAddress + default Basic auth header,
# relative paths, FormUrlEncodedContent bodies, JsonDocument reads). The Python
# port injects the shared circle_ai.integration.http.IHttpFetcher: each request
# is built as an absolute URL (combine_uri) with the Basic Authorization header
# attached per request, form bodies ride body_bytes with the
# application/x-www-form-urlencoded content-type, and JSON is read off resp.json().
# Fail-soft when credentials are missing, exactly as the C#.

from __future__ import annotations

import logging
from datetime import datetime, timezone
from typing import List, Optional

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
from .primitives import (
    CallDirection,
    CallInfo,
    CallMediaFormat,
    ProvisionedNumber,
)
from .twilio_call_session import PendingMediaStream, TwilioCallSession
from .twilio_options import TwilioOptions

_logger = logging.getLogger("CircleAI.Telephony.Twilio.TwilioCarrier")


def _is_null_or_whitespace(s: Optional[str]) -> bool:
    return s is None or s.strip() == ""


def _html_encode(text: str) -> str:
    import html

    return html.escape(text, quote=True)


class TwilioCarrier(ITelephonyCarrier):
    """(3.3.0) :class:`ITelephonyCarrier` backed by Twilio's REST API. Fail-soft
    when credentials are missing. Mirrors ``CircleAI.Telephony.Twilio.TwilioCarrier``.
    """

    def __init__(
        self,
        http: IHttpFetcher,
        options: TwilioOptions,
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
        return "twilio"

    @property
    def is_configured(self) -> bool:
        return not _is_null_or_whitespace(self._options.account_sid) and not _is_null_or_whitespace(
            self._options.auth_token
        )

    def _auth_headers(self) -> dict:
        # C# set the Basic credential on DefaultRequestHeaders once at ctor time
        # (only when configured); we attach it per request.
        return {"Authorization": basic_auth(self._options.account_sid or "", self._options.auth_token or "")}

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

    async def provision_number_async(
        self,
        country_code: str,
        area_code: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProvisionedNumber:
        self._ensure_configured()

        sid = self._options.account_sid
        path = f"/2010-04-01/Accounts/{sid}/AvailablePhoneNumbers/{country_code}/Local.json"
        if not _is_null_or_whitespace(area_code):
            path += f"?AreaCode={escape_data_string(area_code)}&Limit=1"
        else:
            path += "?Limit=1"

        available = (await self._get(path)).ensure_success()
        doc = available.json()
        numbers = doc.get("available_phone_numbers") if isinstance(doc, dict) else None
        first = numbers[0] if isinstance(numbers, list) and numbers else None
        if not isinstance(first, dict):
            raise RuntimeError(
                f"Twilio has no available numbers in country='{country_code}', areaCode='{area_code}'."
            )

        phone_number = first.get("phone_number")

        reserve_path = f"/2010-04-01/Accounts/{sid}/IncomingPhoneNumbers.json"
        (await self._post_form(reserve_path, [("PhoneNumber", phone_number)])).ensure_success()

        cost = parse_decimal(first, "price")
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

        sid = self._options.account_sid
        list_path = (
            f"/2010-04-01/Accounts/{sid}/IncomingPhoneNumbers.json"
            f"?PhoneNumber={escape_data_string(phone_number)}"
        )
        listed = (await self._get(list_path)).ensure_success()
        doc = listed.json()
        arr = doc.get("incoming_phone_numbers") if isinstance(doc, dict) else None
        entry = arr[0] if isinstance(arr, list) and arr else None
        if not isinstance(entry, dict):
            raise RuntimeError(f"Phone number '{phone_number}' is not owned on this Twilio account.")

        number_sid = entry.get("sid")
        config_path = f"/2010-04-01/Accounts/{sid}/IncomingPhoneNumbers/{number_sid}.json"
        (
            await self._post_form(
                config_path,
                [("VoiceUrl", inbound_webhook), ("VoiceMethod", "POST")],
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
        opts = options if options is not None else OutboundDialOptions()

        twiml = (
            f"<Response><Connect><Stream url='{_html_encode(stream_url)}'/></Connect></Response>"
        )

        form_pairs = [
            ("From", opts.caller_id_override if opts.caller_id_override else from_number),
            ("To", to_number),
            ("Twiml", twiml),
            ("Timeout", str(opts.ring_timeout_seconds)),
        ]
        if opts.detect_answering_machine:
            form_pairs.append(("MachineDetection", "Enable"))

        calls_path = f"/2010-04-01/Accounts/{self._options.account_sid}/Calls.json"
        resp = (await self._post_form(calls_path, form_pairs)).ensure_success()
        doc = resp.json()
        call_sid = doc.get("sid") if isinstance(doc, dict) else None

        pending = PendingMediaStream(
            CallInfo(
                call_id=call_sid,
                direction=CallDirection.OUTBOUND,
                from_=from_number,
                to=to_number,
                carrier_id=self.carrier_id,
                media_format=CallMediaFormat.MULAW8000,
                started_at_utc=datetime.now(timezone.utc),
            )
        )
        return TwilioCallSession(pending, self)

    async def list_numbers_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        if not self.is_configured:
            return []

        path = f"/2010-04-01/Accounts/{self._options.account_sid}/IncomingPhoneNumbers.json?PageSize=100"
        resp = await self._get(path)
        if not resp.is_success:
            self._logger.warning("Twilio ListNumbers returned %s", resp.status_code)
            return []

        doc = resp.json()
        result: List[ProvisionedNumber] = []
        arr = doc.get("incoming_phone_numbers") if isinstance(doc, dict) else None
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

    async def redirect_call_async(
        self, call_sid: str, twiml: str, *, ct: Optional[object] = None
    ) -> None:
        """(3.3.0) Redirect an in-progress call to fresh TwiML. Used by sessions
        on cold transfer."""
        self._ensure_configured()
        path = f"/2010-04-01/Accounts/{self._options.account_sid}/Calls/{call_sid}.json"
        resp = await self._post_form(path, [("Twiml", twiml)])
        if not resp.is_success:
            self._logger.warning("Twilio RedirectCall %s returned %s", call_sid, resp.status_code)

    async def end_call_async(self, call_sid: str, *, ct: Optional[object] = None) -> None:
        """(3.3.0) End a call by Twilio CallSid via the REST API. Used by sessions
        on hang-up."""
        if not self.is_configured:
            return
        path = f"/2010-04-01/Accounts/{self._options.account_sid}/Calls/{call_sid}.json"
        resp = await self._post_form(path, [("Status", "completed")])
        if not resp.is_success:
            self._logger.warning("Twilio EndCall %s returned %s", call_sid, resp.status_code)

    def _ensure_configured(self) -> None:
        if not self.is_configured:
            raise RuntimeError(
                "Twilio carrier is not configured. Set TwilioOptions.account_sid and "
                "auth_token before calling REST operations."
            )


from decimal import Decimal as _Decimal

_ZERO = _Decimal(0)


__all__ = ["TwilioCarrier"]
