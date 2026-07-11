# tourism_domain_context.py
#
# Port of CircleAI.Tourism TourismDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class TourismDomainContext:
    """Domain context for the Tourism vertical (mirrors
    ``CircleAI.Tourism.TourismDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Tourism] Expert tourism and travel operations assistant. Help "
        "with itinerary design, tour package costing, guide briefing notes, "
        "destination marketing, and safety management plans. Apply experiential "
        "travel principles. Compliance: Tourism Act 3/2014, SABS tour operator "
        "standards, SATSA, POPIA."
    )

    ComplianceFlags: Sequence[str] = (
        "Tourism_Act_3_2014",
        "SABS_Tour_Ops",
        "SATSA",
        "POPIA",
    )

    SuggestedTools: Sequence[str] = (
        "mapping",
        "booking_system",
        "document_editor",
        "weather_api",
    )
