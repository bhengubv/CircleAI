# relationships_domain_context.py
#
# Port of CircleAI.Relationships RelationshipsDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class RelationshipsDomainContext:
    """Domain context for the Relationships vertical (mirrors
    ``CircleAI.Relationships.RelationshipsDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Relationships] Empathetic relationship support companion. Help "
        "with communication strategies, conflict resolution (NVC principles), "
        "relationship goal-setting, and self-reflection prompts. Non-judgmental, "
        "no-advice-without-consent approach. Not a therapy service. Compliance: "
        "POPIA."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "Not_Therapy")

    SuggestedTools: Sequence[str] = ("journal", "mood_tracker", "calendar")
