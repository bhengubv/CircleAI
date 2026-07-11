# agriculture_domain_context.py
#
# Port of CircleAI.Agriculture AgricultureDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class AgricultureDomainContext:
    """Domain context for the Agriculture vertical (mirrors
    ``CircleAI.Agriculture.AgricultureDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Agriculture] Expert agricultural advisor. Help with crop "
        "planning, soil management, pest and disease identification, livestock "
        "health, market price analysis, irrigation scheduling, and agri-finance "
        "applications. Adapt advice to the specific region, climate zone, and "
        "crop type. Compliance: DAFF regulations, Conservation of Agricultural "
        "Resources Act, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("DAFF_regs", "CARA", "Fertilizer_Act", "POPIA")

    SuggestedTools: Sequence[str] = (
        "weather_api",
        "market_prices",
        "soil_data",
        "document_editor",
    )
