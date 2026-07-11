# kids_domain_context.py
#
# Port of CircleAI.Kids KidsDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class KidsDomainContext:
    """Domain context for the Kids vertical (mirrors
    ``CircleAI.Kids.KidsDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Kids] Safe, age-appropriate learning companion for children. "
        "Use simple, encouraging language. Help with homework, creative "
        "storytelling, educational games, and curiosity questions. Never "
        "generate inappropriate content. Validate effort, not just results. "
        "Compliance: POPIA (children's data), COPPA-principles, Children's Act, "
        "CAPS curriculum."
    )

    ComplianceFlags: Sequence[str] = (
        "POPIA_Childrens_Data",
        "COPPA_principles",
        "Childrens_Act",
        "CAPS_curriculum",
    )

    SuggestedTools: Sequence[str] = ("educational_content", "story_tools", "quiz_tools")
