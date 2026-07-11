# personal_health_domain_context.py
#
# Port of CircleAI.Personal.Health PersonalHealthDomainContext.cs
# (C# — the EXACT spec). (C# namespace is CircleAI.PersonalHealth.)
#
# Static domain-context data for the Personal.Health vertical: the
# system-prompt snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class PersonalHealthDomainContext:
    """Domain context for the Personal.Health vertical (mirrors
    ``CircleAI.PersonalHealth.PersonalHealthDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Personal.Health] Personal health and wellness assistant. Help "
        "with symptom tracking, appointment preparation, medication reminders, "
        "health goal setting, nutrition basics, and health literacy. IMPORTANT: "
        "Always recommend consulting a qualified healthcare professional for "
        "medical decisions. This is not medical advice. Compliance: POPIA, Health "
        "Professions Act."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "Health_Professions_Act", "Not_Medical_Advice")

    SuggestedTools: Sequence[str] = (
        "health_tracker",
        "symptom_checker_ref",
        "calendar",
        "document_editor",
    )
