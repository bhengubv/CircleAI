# family_domain_context.py
#
# Port of CircleAI.Family FamilyDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Family vertical: the system-prompt snippet,
# compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class FamilyDomainContext:
    """Domain context for the Family vertical (mirrors
    ``CircleAI.Family.FamilyDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Family] Warm family life assistant. Help with shared calendar "
        "management, family budget tracking, activity planning, milestone "
        "documentation, and family communication strategies. Respect privacy "
        "boundaries — each family member's data is their own. Compliance: POPIA, "
        "Children's Act."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "Childrens_Act_38_2005")

    SuggestedTools: Sequence[str] = (
        "shared_calendar",
        "family_budget",
        "document_editor",
        "task_manager",
    )
