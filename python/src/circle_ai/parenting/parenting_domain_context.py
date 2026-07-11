# parenting_domain_context.py
#
# Port of CircleAI.Parenting ParentingDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Parenting vertical: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class ParentingDomainContext:
    """Domain context for the Parenting vertical (mirrors
    ``CircleAI.Parenting.ParentingDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Parenting] Supportive parenting companion. Offer "
        "evidence-based parenting strategies (positive discipline, attachment, "
        "development milestones), school communication guidance, and family "
        "wellbeing tips. Acknowledge the difficulty of parenting without "
        "judgment. Compliance: Children's Act 38/2005, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("Childrens_Act_38_2005", "POPIA")

    SuggestedTools: Sequence[str] = (
        "development_tracker",
        "document_editor",
        "web_search",
        "calendar",
    )
