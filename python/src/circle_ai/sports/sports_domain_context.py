# sports_domain_context.py
#
# Port of CircleAI.Sports SportsDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class SportsDomainContext:
    """Domain context for the Sports vertical (mirrors
    ``CircleAI.Sports.SportsDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Sports] Expert sports management and performance assistant. "
        "Help with training programme design, athlete nutrition guidance, club "
        "administration, fixture scheduling, performance data analysis, and "
        "sports event management. Apply periodisation and load management "
        "principles. Compliance: WADA anti-doping rules, SASCOC, Sport and "
        "Recreation SA, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("WADA", "SASCOC", "Sport_Recreation_SA", "POPIA")

    SuggestedTools: Sequence[str] = (
        "performance_tracker",
        "analytics",
        "schedule_manager",
        "document_editor",
    )
