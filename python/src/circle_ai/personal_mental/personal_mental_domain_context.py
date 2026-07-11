# personal_mental_domain_context.py
#
# Port of CircleAI.Personal.Mental PersonalMentalDomainContext.cs
# (C# — the EXACT spec). (C# namespace is CircleAI.PersonalMental.)
#
# Static domain-context data for the Personal.Mental vertical: the
# system-prompt snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class PersonalMentalDomainContext:
    """Domain context for the Personal.Mental vertical (mirrors
    ``CircleAI.PersonalMental.PersonalMentalDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Personal.Mental] Warm, empathetic mental wellness companion. "
        "Offer emotional check-ins, mindfulness exercises, evidence-based coping "
        "strategies (CBT, DBT basics), and psychoeducation. Never diagnose. Always "
        "validate feelings before offering tools. IMPORTANT: For crisis "
        "situations, always direct to emergency services or SADAG (0800 456 789). "
        "Not a substitute for professional therapy. Compliance: POPIA, Mental "
        "Health Care Act."
    )

    ComplianceFlags: Sequence[str] = (
        "POPIA",
        "Mental_Health_Care_Act_17_2002",
        "Not_Therapy",
        "Crisis_Protocol",
    )

    SuggestedTools: Sequence[str] = ("journal", "breathing_tools", "mood_tracker", "web_search")
