# http_tool_bridge.py
#
# Port of CircleAI.Tools HttpToolBridge.cs (C# — the EXACT spec).
#
# HTTP-backed implementation of IToolBridge that routes tool calls to the
# TheGeekNetwork APIs over REST. Tool-name -> endpoint mapping is provided for
# the representative operations defined in TheGeekNetworkTools; unmapped tools
# return a structured error rather than raising.
#
# No network calls happen during construction or via the available_tools
# property — only invoke_async hits the wire, and it does so through the
# injected IHttpFetcher seam (circle_ai.integration.http) rather than a live
# HttpClient, so the logic is fully testable with InMemoryHttpFetcher.
#
# Porting notes:
#   * EndpointMapping (Method, PathTemplate, Body) -> a small frozen dataclass.
#     Body strategy is one of "none" | "query" | "json".
#   * ResolveUrl substitutes {placeholder} segments from arguments (URL-escaped),
#     strips those keys out of the body/query (BuildBodyArgs), and appends a
#     query string for BodyQuery mappings.
#   * The C# reads a JSON body when the response content-type contains "json",
#     else the raw string. HttpResponse here has no content-type header, so we
#     attempt JSON parse first (matching "read structured body when it is JSON")
#     and fall back to the raw text — behaviour-equivalent for the API surface.
#   * URL escaping uses urllib.parse.quote with an empty `safe` set so it matches
#     Uri.EscapeDataString (RFC 3986 unreserved kept, everything else percent-
#     encoded, including '/').

from __future__ import annotations

import json as _json
from dataclasses import dataclass
from typing import Any, Dict, Iterable, List, Optional
from urllib.parse import quote

from ..integration.http import HttpRequest, IHttpFetcher
from .the_geek_network_tools import TheGeekNetworkTools
from .tool_types import IToolBridge, ToolDefinition, ToolInvocation, ToolResult

# Body strategy values.
_BODY_NONE = "none"
_BODY_QUERY = "query"
_BODY_JSON = "json"


@dataclass(frozen=True, slots=True)
class _EndpointMapping:
    method: str
    path_template: str
    body: str


def _escape_data_string(value: str) -> str:
    """Mirror ``Uri.EscapeDataString`` — percent-encode everything except the
    RFC 3986 unreserved set ``A-Z a-z 0-9 - _ . ~`` (so '/' is escaped too)."""
    return quote(value, safe="")


def _extract_placeholders(template: str) -> Iterable[str]:
    """Yield each ``{name}`` placeholder in ``template`` in order (mirrors the
    C# ExtractPlaceholders)."""
    i = 0
    n = len(template)
    while i < n:
        open_idx = template.find("{", i)
        if open_idx < 0:
            return
        close_idx = template.find("}", open_idx + 1)
        if close_idx < 0:
            return
        yield template[open_idx + 1:close_idx]
        i = close_idx + 1


def _render_query_value(value: Any) -> Optional[str]:
    """Mirror RenderQueryValue: str verbatim, bool -> 'true'/'false',
    everything else via str()."""
    if isinstance(value, str):
        return value
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


class HttpToolBridge(IToolBridge):
    """HTTP-backed :class:`IToolBridge` routing tool calls to TheGeekNetwork
    APIs over REST through an injected :class:`IHttpFetcher`. Mirrors
    ``CircleAI.Tools.HttpToolBridge``.
    """

    def __init__(
        self,
        base_url: str,
        http_fetcher: IHttpFetcher,
        tools: Optional[List[ToolDefinition]] = None,
    ) -> None:
        if base_url is None or base_url.strip() == "":
            raise ValueError("base_url must be non-empty")
        if http_fetcher is None:
            raise ValueError("http_fetcher must not be None")

        self._http = http_fetcher
        # Ensure a single trailing slash so relative path joins are predictable.
        self._base_url = base_url if base_url.endswith("/") else base_url + "/"
        self._tools = tools if tools is not None else TheGeekNetworkTools.get_all_tools()
        self._routes = self._build_routes()

    @property
    def available_tools(self) -> List[ToolDefinition]:
        return self._tools

    async def invoke_async(
        self,
        invocation: ToolInvocation,
        *,
        ct: Optional[object] = None,
    ) -> ToolResult:
        if invocation is None:
            raise ValueError("invocation must not be None")

        mapping = self._routes.get(invocation.tool_name)
        if mapping is None:
            return ToolResult(
                tool_name=invocation.tool_name,
                success=False,
                error=(
                    f"Tool '{invocation.tool_name}' is not registered in this "
                    f"bridge instance."
                ),
            )

        try:
            url = self._resolve_url(mapping, invocation.arguments)
            request = self._build_request(mapping, url, invocation.arguments)
            response = await self._http.send_async(request)

            body = self._read_body(response)

            if not response.is_success:
                return ToolResult(
                    tool_name=invocation.tool_name,
                    success=False,
                    result=body,
                    error=f"HTTP {response.status_code} {response.reason}".rstrip(),
                )

            return ToolResult(
                tool_name=invocation.tool_name,
                success=True,
                result=body,
            )
        except Exception as ex:  # noqa: BLE001 — mirror the C# catch-all.
            return ToolResult(
                tool_name=invocation.tool_name,
                success=False,
                error=str(ex),
            )

    # ── Internal: response parsing ──────────────────────────────────────────

    @staticmethod
    def _read_body(response: object) -> Any:
        text = getattr(response, "text", "") or ""
        if text == "":
            return None
        try:
            return _json.loads(text)
        except (ValueError, TypeError):
            return text

    # ── Internal: URL / request building ────────────────────────────────────

    def _resolve_url(
        self, mapping: _EndpointMapping, arguments: Dict[str, Any]
    ) -> str:
        path = mapping.path_template
        for placeholder in _extract_placeholders(mapping.path_template):
            raw = arguments.get(placeholder)
            if raw is None:
                raise RuntimeError(
                    f"Tool argument '{placeholder}' is required to build URL "
                    f"'{mapping.path_template}'."
                )
            path = path.replace(
                "{" + placeholder + "}", _escape_data_string(str(raw))
            )

        url = self._base_url + path

        if mapping.body == _BODY_QUERY:
            query = self._build_query_string(self._build_body_args(mapping, arguments))
            if query:
                sep = "&" if "?" in url else "?"
                url = url + sep + query

        return url

    def _build_request(
        self,
        mapping: _EndpointMapping,
        url: str,
        arguments: Dict[str, Any],
    ) -> HttpRequest:
        if mapping.body == _BODY_JSON:
            body = self._build_body_args(mapping, arguments)
            return HttpRequest(method=mapping.method, url=url, body_json=body)
        return HttpRequest(method=mapping.method, url=url)

    @staticmethod
    def _build_body_args(
        mapping: _EndpointMapping, arguments: Dict[str, Any]
    ) -> Dict[str, Any]:
        # Drop placeholders from the body/query — they're already in the URL.
        placeholders = set(_extract_placeholders(mapping.path_template))
        return {k: v for k, v in arguments.items() if k not in placeholders}

    @staticmethod
    def _build_query_string(args: Dict[str, Any]) -> str:
        if not args:
            return ""
        parts: List[str] = []
        for key, value in args.items():
            if value is None:
                continue
            rendered = _render_query_value(value)
            if rendered is None:
                continue
            parts.append(f"{_escape_data_string(key)}={_escape_data_string(rendered)}")
        return "&".join(parts)

    # ── Internal: routing table ─────────────────────────────────────────────

    @staticmethod
    def _build_routes() -> Dict[str, _EndpointMapping]:
        m = _EndpointMapping
        return {
            # Account
            "tgn.account.get_profile": m("GET", "account/v1/users/{user_id}", _BODY_NONE),
            "tgn.account.update_profile": m("PATCH", "account/v1/users/me", _BODY_JSON),
            # Audit
            "tgn.audit.list_events": m("GET", "audit/v1/events", _BODY_QUERY),
            # Auth
            "tgn.auth.request_otp": m("POST", "auth/v1/otp/request", _BODY_JSON),
            "tgn.auth.verify_otp": m("POST", "auth/v1/otp/verify", _BODY_JSON),
            "tgn.auth.push_to_app": m("POST", "auth/v1/push-to-app", _BODY_JSON),
            # BidBaas
            "tgn.bidbaas.list_active_auctions": m("GET", "bidbaas/v1/auctions/active", _BODY_QUERY),
            "tgn.bidbaas.place_bid": m("POST", "bidbaas/v1/auctions/{auction_id}/bids", _BODY_JSON),
            "tgn.bidbaas.get_auction_details": m("GET", "bidbaas/v1/auctions/{auction_id}", _BODY_NONE),
            # BillPayment
            "tgn.billpayment.list_billers": m("GET", "billpayment/v1/billers", _BODY_QUERY),
            "tgn.billpayment.pay_bill": m("POST", "billpayment/v1/payments", _BODY_JSON),
            # Blockchain
            "tgn.blockchain.get_transaction": m("GET", "blockchain/v1/transactions/{tx_hash}", _BODY_NONE),
            "tgn.blockchain.get_address_info": m("GET", "blockchain/v1/addresses/{address}", _BODY_NONE),
            # Butler
            "tgn.butler.log_interaction": m("POST", "butler/v1/interactions", _BODY_JSON),
            "tgn.butler.get_user_context": m("GET", "butler/v1/users/{user_id}/context", _BODY_NONE),
            # CircleAether
            "tgn.circleaether.get_node_status": m("GET", "circleaether/v1/nodes/{device_id}/status", _BODY_NONE),
            "tgn.circleaether.list_nearby_peers": m("GET", "circleaether/v1/peers/nearby", _BODY_QUERY),
            # Ecommerce
            "tgn.ecommerce.search_products": m("GET", "ecommerce/v1/products/search", _BODY_QUERY),
            "tgn.ecommerce.get_product": m("GET", "ecommerce/v1/products/{product_id}", _BODY_NONE),
            # Electricity
            "tgn.electricity.buy_token": m("POST", "electricity/v1/tokens", _BODY_JSON),
            "tgn.electricity.list_recent_purchases": m("GET", "electricity/v1/purchases", _BODY_QUERY),
            # Geo
            "tgn.geo.get_user_location": m("GET", "geo/v1/users/me/location", _BODY_NONE),
            "tgn.geo.geocode_address": m("GET", "geo/v1/geocode", _BODY_QUERY),
            # Glocell
            "tgn.glocell.list_products": m("GET", "glocell/v1/products", _BODY_QUERY),
            # Incentives
            "tgn.incentives.get_qi_balance": m("GET", "incentives/v1/qi/balance", _BODY_NONE),
            "tgn.incentives.list_active_quests": m("GET", "incentives/v1/quests/active", _BODY_QUERY),
            # KiffStore
            "tgn.kiffstore.search_items": m("GET", "kiffstore/v1/items/search", _BODY_QUERY),
            # Ledger
            "tgn.ledger.get_account_balance": m("GET", "ledger/v1/accounts/{account_id}/balance", _BODY_NONE),
            "tgn.ledger.list_entries": m("GET", "ledger/v1/accounts/{account_id}/entries", _BODY_QUERY),
            # Localization
            "tgn.localization.translate_text": m("POST", "localization/v1/translate", _BODY_JSON),
            "tgn.localization.list_supported_languages": m("GET", "localization/v1/languages", _BODY_NONE),
            # Maps
            "tgn.maps.geocode": m("GET", "maps/v1/geocode", _BODY_QUERY),
            "tgn.maps.reverse_geocode": m("GET", "maps/v1/reverse-geocode", _BODY_QUERY),
            # MapsData
            "tgn.mapsdata.search_pois": m("GET", "mapsdata/v1/pois/search", _BODY_QUERY),
            # Media
            "tgn.media.create_upload_url": m("POST", "media/v1/uploads", _BODY_JSON),
            "tgn.media.get_media": m("GET", "media/v1/media/{media_id}", _BODY_NONE),
            # Messaging
            "tgn.messaging.send_message": m("POST", "messaging/v1/messages", _BODY_JSON),
            "tgn.messaging.list_conversations": m("GET", "messaging/v1/conversations", _BODY_QUERY),
            "tgn.messaging.get_messages": m("GET", "messaging/v1/conversations/{conversation_id}/messages", _BODY_QUERY),
            # Notification
            "tgn.notification.send_push": m("POST", "notification/v1/push", _BODY_JSON),
            "tgn.notification.list_for_user": m("GET", "notification/v1/notifications", _BODY_QUERY),
            # OpSupport
            "tgn.opsupport.create_ticket": m("POST", "opsupport/v1/tickets", _BODY_JSON),
            "tgn.opsupport.get_system_status": m("GET", "opsupport/v1/status", _BODY_NONE),
            # Panik
            "tgn.panik.trigger_sos": m("POST", "panik/v1/alerts", _BODY_JSON),
            "tgn.panik.cancel_sos": m("POST", "panik/v1/alerts/{alert_id}/cancel", _BODY_JSON),
            # Payfast
            "tgn.payfast.create_payment": m("POST", "payfast/v1/payments", _BODY_JSON),
            # Sdpkt
            "tgn.sdpkt.get_balance": m("GET", "sdpkt/v1/wallet/balance", _BODY_NONE),
            "tgn.sdpkt.send_payment": m("POST", "sdpkt/v1/wallet/transfers", _BODY_JSON),
            "tgn.sdpkt.get_transactions": m("GET", "sdpkt/v1/wallet/transactions", _BODY_QUERY),
            # ShhMoney
            "tgn.shhmoney.create_discreet_payment": m("POST", "shhmoney/v1/payments", _BODY_JSON),
            # SleptOn
            "tgn.slepton.list_stories": m("GET", "slepton/v1/stories", _BODY_QUERY),
            "tgn.slepton.get_story": m("GET", "slepton/v1/stories/{story_id}", _BODY_NONE),
            # SortedClothing
            "tgn.sortedclothing.search_items": m("GET", "sortedclothing/v1/items/search", _BODY_QUERY),
            # TagMe
            "tgn.tagme.create_tag": m("POST", "tagme/v1/tags", _BODY_JSON),
            "tgn.tagme.list_nearby_tags": m("GET", "tagme/v1/tags/nearby", _BODY_QUERY),
            # Takemehome
            "tgn.takemehome.search_flights": m("GET", "takemehome/v1/flights/search", _BODY_QUERY),
            "tgn.takemehome.search_stays": m("GET", "takemehome/v1/stays/search", _BODY_QUERY),
            # TheHotList
            "tgn.thehotlist.list_entries": m("GET", "thehotlist/v1/entries", _BODY_QUERY),
            # TheJobCenter
            "tgn.thejobcenter.search_jobs": m("GET", "thejobcenter/v1/jobs/search", _BODY_QUERY),
            "tgn.thejobcenter.apply": m("POST", "thejobcenter/v1/jobs/{job_id}/applications", _BODY_JSON),
            # ThirdParty
            "tgn.thirdparty.list_integrations": m("GET", "thirdparty/v1/integrations", _BODY_NONE),
            "tgn.thirdparty.invoke_integration": m("POST", "thirdparty/v1/integrations/{integration_name}/invoke", _BODY_JSON),
            # TrustSeal
            "tgn.trustseal.get_status": m("GET", "trustseal/v1/status", _BODY_NONE),
            "tgn.trustseal.start_verification": m("POST", "trustseal/v1/verifications", _BODY_JSON),
            # Wallet
            "tgn.wallet.get_balance": m("GET", "wallet/v1/balance", _BODY_QUERY),
            "tgn.wallet.get_transactions": m("GET", "wallet/v1/transactions", _BODY_QUERY),
            # WhatWeWant
            "tgn.whatwewant.list_stories": m("GET", "whatwewant/v1/stories", _BODY_QUERY),
            "tgn.whatwewant.get_story": m("GET", "whatwewant/v1/stories/{story_id}", _BODY_NONE),
            # Wolverine
            "tgn.wolverine.list_jobs": m("GET", "wolverine/v1/jobs", _BODY_QUERY),
        }
