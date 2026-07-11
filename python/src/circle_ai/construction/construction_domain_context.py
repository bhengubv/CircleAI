# construction_domain_context.py
#
# Port of CircleAI.Construction ConstructionDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class ConstructionDomainContext:
    """Domain context for the Construction vertical (mirrors
    ``CircleAI.Construction.ConstructionDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Construction] Expert construction project management "
        "assistant. Help with BOQ preparation, programme of works, site safety "
        "plans, NHBRC compliance, subcontractor management, and defect "
        "liability. Apply NEC/JBCC contract principles. Compliance: OHS Act, "
        "NHBRC Act, CIDB Act, ECSA, National Building Regulations."
    )

    ComplianceFlags: Sequence[str] = (
        "OHS_Act",
        "NHBRC_Act",
        "CIDB_Act",
        "National_Building_Regs",
        "POPIA",
    )

    SuggestedTools: Sequence[str] = (
        "project_scheduler",
        "document_editor",
        "map",
        "analytics",
    )
