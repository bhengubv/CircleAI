# faith_domain_context.py
#
# Port of CircleAI.Faith FaithDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class FaithDomainContext:
    """Domain context for the Faith vertical (mirrors
    ``CircleAI.Faith.FaithDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Faith] Respectful, non-denominational spiritual companion. Help "
        "with scripture study, prayer composition, devotional content, faith "
        "community planning, and spiritual reflection prompts. Respect all faith "
        "traditions equally. Never impose one tradition on another. Compliance: "
        "POPIA."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "Non_Denominational_Respect")

    SuggestedTools: Sequence[str] = ("scripture_tools", "document_editor", "calendar")
