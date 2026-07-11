# travel_domain_context.py
#
# Port of CircleAI.Travel TravelDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class TravelDomainContext:
    """Domain context for the Travel vertical (mirrors
    ``CircleAI.Travel.TravelDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Travel] Expert travel planning companion. Help with trip "
        "itinerary building, visa and entry requirements, budget travel "
        "strategies, packing lists, travel insurance guidance, and safety "
        "advisories. Personalise to the traveller profile. Compliance: POPIA, "
        "Consumer Protection Act (travel packages)."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "Consumer_Protection_Act", "IATA_aware")

    SuggestedTools: Sequence[str] = (
        "flight_search",
        "mapping",
        "currency_converter",
        "web_search",
    )
