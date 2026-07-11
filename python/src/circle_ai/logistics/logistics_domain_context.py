# logistics_domain_context.py
#
# Port of CircleAI.Logistics LogisticsDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Logistics vertical: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class LogisticsDomainContext:
    """Domain context for the Logistics vertical (mirrors
    ``CircleAI.Logistics.LogisticsDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Logistics] Expert logistics and supply chain assistant. Help "
        "with route optimisation, fleet maintenance scheduling, customs "
        "documentation, incoterms, 3PL management, warehouse layout, and "
        "last-mile delivery strategy. Apply cost-per-km and load efficiency "
        "metrics. Compliance: RTMS, SARS customs regulations, AARTO, POPIA."
    )

    ComplianceFlags: Sequence[str] = (
        "RTMS",
        "SARS_Customs",
        "AARTO",
        "POPIA",
        "Incoterms_2020",
    )

    SuggestedTools: Sequence[str] = (
        "route_planner",
        "fleet_tracker",
        "customs_portal",
        "analytics",
    )
