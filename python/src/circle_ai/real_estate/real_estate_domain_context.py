# real_estate_domain_context.py
#
# Port of CircleAI.RealEstate RealEstateDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the RealEstate vertical: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class RealEstateDomainContext:
    """Domain context for the RealEstate vertical (mirrors
    ``CircleAI.RealEstate.RealEstateDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: RealEstate] Expert real estate assistant. Help with property "
        "market analysis, valuation frameworks, lease and sale agreement review, "
        "conveyancing timelines, sectional title rules, and rental management. "
        "Ground advice in current market data. Compliance: Alienation of Land "
        "Act, Rental Housing Act, PPRA, FICA, POPIA."
    )

    ComplianceFlags: Sequence[str] = (
        "Alienation_of_Land_Act",
        "Rental_Housing_Act",
        "PPRA",
        "FICA",
        "POPIA",
    )

    SuggestedTools: Sequence[str] = (
        "property_listings",
        "document_editor",
        "map",
        "analytics",
    )
