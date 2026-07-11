"""circle_ai.integration_calendar — port of the CircleAI.Integration.Calendar
assembly.

(Phase B1) Calendar connectors: a dependency-free CalDAV client (iCloud,
Fastmail, Nextcloud…), Google Calendar v3, and Microsoft Graph. Each is an
:class:`~circle_ai.integration.contracts.ICalendarConnector`. C# is the exact
spec. The C# connectors take an injected ``HttpClient``; the Python ports take
an injected :class:`~circle_ai.integration.http.IHttpFetcher` and parse the
identical XML/JSON so no real network is needed.

Public surface:

  * CalDavCalendarConnector / CalDavCalendarOptions
  * GoogleCalendarConnector / GoogleCalendarOptions
  * MsGraphCalendarConnector / MsGraphCalendarOptions
"""
from __future__ import annotations

from .caldav_calendar_connector import (
    CalDavCalendarConnector,
    CalDavCalendarOptions,
)
from .google_calendar_connector import (
    GoogleCalendarConnector,
    GoogleCalendarOptions,
)
from .ms_graph_calendar_connector import (
    MsGraphCalendarConnector,
    MsGraphCalendarOptions,
)

__all__ = [
    "CalDavCalendarConnector",
    "CalDavCalendarOptions",
    "GoogleCalendarConnector",
    "GoogleCalendarOptions",
    "MsGraphCalendarConnector",
    "MsGraphCalendarOptions",
]
