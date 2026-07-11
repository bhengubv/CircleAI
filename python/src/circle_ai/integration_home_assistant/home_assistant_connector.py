# home_assistant_connector.py
#
# Port of CircleAI.Integration.HomeAssistant/HomeAssistantConnector.cs (C# — the
# EXACT spec).
#
# (Phase C1) HomeAssistant REST API client. Connects with a long-lived access
# token (Profile -> Security -> Long-Lived Access Tokens in HA). Lists entities,
# calls services, and reads state.
#
# The C# takes an injected ``HttpClient`` (Bearer auth header set from the
# token); the Python port takes an injected :class:`IHttpFetcher` and attaches
# the same ``Authorization: Bearer`` header per request. Entity-attribute value
# coercion mirrors the C# ``JsonValueKind`` switch: string as-is, number as raw
# text, bool -> "true"/"false", arrays/objects -> raw JSON.

from __future__ import annotations

import json as _json
from dataclasses import dataclass
from typing import Any, Dict, List, Mapping, Optional
from urllib.parse import quote

from circle_ai.integration.contracts import HaEntity, IHomeAutomationConnector
from circle_ai.integration.http import HttpRequest, IHttpFetcher


@dataclass(frozen=True, slots=True)
class HomeAssistantOptions:
    """Mirrors ``CircleAI.Integration.HomeAssistant.HomeAssistantOptions`` —
    ``record(Uri BaseUrl, string AccessToken)``.

    ``base_url``: HA base URL, e.g. ``http://homeassistant.local:8123/`` — must
    include a trailing slash. ``access_token``: long-lived access token.
    """

    base_url: str
    access_token: str


def _coerce_attr(value: Any) -> str:
    """Mirror the C# attribute ``JsonValueKind`` switch."""
    if isinstance(value, str):
        return value
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        return repr(value)
    # arrays / objects / null -> raw JSON text (C# JsonElement.ToString()).
    return _json.dumps(value, separators=(",", ":"))


class HomeAssistantConnector(IHomeAutomationConnector):
    """Port of ``CircleAI.Integration.HomeAssistant.HomeAssistantConnector``."""

    def __init__(self, opts: HomeAssistantOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def provider_id(self) -> str:
        return "home-assistant"

    @property
    def is_configured(self) -> bool:
        return bool(self._opts.base_url) and bool(
            self._opts.access_token and self._opts.access_token.strip()
        )

    def _auth_headers(self) -> Dict[str, str]:
        headers: Dict[str, str] = {}
        if self._opts.access_token and self._opts.access_token.strip():
            headers["Authorization"] = f"Bearer {self._opts.access_token}"
        return headers

    def _url(self, path: str) -> str:
        base = self._opts.base_url
        if not base.endswith("/"):
            base += "/"
        return base + path

    async def list_entities_async(self) -> List[HaEntity]:
        resp = (
            await self._http.send_async(
                HttpRequest("GET", self._url("api/states"), self._auth_headers())
            )
        ).ensure_success()
        root = resp.json()

        result: List[HaEntity] = []
        if not isinstance(root, list):
            return result
        for st in root:
            if not isinstance(st, dict):
                continue
            entity_id = st.get("entity_id") or ""
            if not entity_id:
                continue
            state = st.get("state") or ""
            domain = entity_id.split(".", 1)[0]
            attrs: Dict[str, str] = {}
            friendly = entity_id
            att = st.get("attributes")
            if isinstance(att, dict):
                for name, val in att.items():
                    attrs[name] = _coerce_attr(val)
                    if name == "friendly_name" and isinstance(val, str):
                        friendly = val or entity_id
            result.append(HaEntity(entity_id, friendly, domain, state, attrs))
        return result

    async def call_service_async(
        self,
        domain: str,
        service: str,
        data: Optional[Mapping[str, object]],
    ) -> None:
        if not (domain and domain.strip()):
            raise ValueError("domain required")
        if not (service and service.strip()):
            raise ValueError("service required")

        payload = dict(data) if data is not None else {}
        path = f"api/services/{quote(domain, safe='')}/{quote(service, safe='')}"
        resp = await self._http.send_async(
            HttpRequest("POST", self._url(path), self._auth_headers(), body_json=payload)
        )
        resp.ensure_success()

    async def turn_on_async(self, entity_id: str) -> None:
        """(Phase C1) Convenience: turn an entity on via
        ``homeassistant.turn_on``.
        """
        await self.call_service_async(
            "homeassistant", "turn_on", {"entity_id": entity_id}
        )

    async def turn_off_async(self, entity_id: str) -> None:
        """(Phase C1) Convenience: turn an entity off via
        ``homeassistant.turn_off``.
        """
        await self.call_service_async(
            "homeassistant", "turn_off", {"entity_id": entity_id}
        )
