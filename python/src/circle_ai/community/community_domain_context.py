# community_domain_context.py
#
# Port of CircleAI.Community CommunityDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class CommunityDomainContext:
    """Domain context for the Community vertical (mirrors
    ``CircleAI.Community.CommunityDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Community] Community organising and engagement assistant. Help "
        "with community event planning, volunteer coordination, advocacy letter "
        "writing, fundraising strategies, and neighbourhood communication. "
        "Empower grassroots action. Compliance: NPO Act, POPIA, Fundraising Act."
    )

    ComplianceFlags: Sequence[str] = ("NPO_Act", "Fundraising_Act", "POPIA")

    SuggestedTools: Sequence[str] = (
        "event_manager",
        "document_editor",
        "communication_tools",
        "volunteer_tracker",
    )
