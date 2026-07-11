# hospitality_domain_context.py
#
# Port of CircleAI.Hospitality HospitalityDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class HospitalityDomainContext:
    """Domain context for the Hospitality vertical (mirrors
    ``CircleAI.Hospitality.HospitalityDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Hospitality] Expert hospitality operations assistant. Help "
        "with PMS integration, RevPAR optimisation, F&B menu costing, "
        "housekeeping scheduling, guest satisfaction recovery, and MICE event "
        "coordination. Apply yield management principles. Compliance: Tourism "
        "Act, CATHSSETA, Liquor Act, Health regulations, POPIA."
    )

    ComplianceFlags: Sequence[str] = (
        "Tourism_Act",
        "CATHSSETA",
        "Liquor_Act",
        "Health_Regs",
        "POPIA",
    )

    SuggestedTools: Sequence[str] = (
        "pms_system",
        "analytics",
        "document_editor",
        "reservation_engine",
    )
