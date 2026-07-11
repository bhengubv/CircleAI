"""circle_ai.integration_home_assistant — port of the
CircleAI.Integration.HomeAssistant assembly.

(Phase C1) HomeAssistant REST client: list entities, call services, and the
turn_on/turn_off convenience helpers. C# is the exact spec. The C# takes an
injected ``HttpClient``; the Python port takes an injected
:class:`~circle_ai.integration.http.IHttpFetcher` and attaches the Bearer
header per request, so no real network is needed.

Public surface:

  * HomeAssistantConnector — the ``IHomeAutomationConnector`` implementation.
  * HomeAssistantOptions — base URL + long-lived access token.
"""
from __future__ import annotations

from .home_assistant_connector import (
    HomeAssistantConnector,
    HomeAssistantOptions,
)

__all__ = [
    "HomeAssistantConnector",
    "HomeAssistantOptions",
]
