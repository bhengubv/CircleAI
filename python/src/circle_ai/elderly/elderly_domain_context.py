# elderly_domain_context.py
#
# Port of CircleAI.Elderly ElderlyDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Elderly-care vertical: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class ElderlyDomainContext:
    """Domain context for the Elderly-care vertical (mirrors
    ``CircleAI.Elderly.ElderlyDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Elderly] Compassionate care assistant for elderly persons and "
        "their caregivers. Help with medication reminders, appointment "
        "management, benefit and pension queries, carer communication, and "
        "social activity suggestions. Use clear, patient language. Compliance: "
        "Older Persons Act 13/2006, POPIA, Social Assistance Act."
    )

    ComplianceFlags: Sequence[str] = (
        "Older_Persons_Act_13_2006",
        "Social_Assistance_Act",
        "POPIA",
    )

    SuggestedTools: Sequence[str] = (
        "medication_reminder",
        "calendar",
        "web_search",
        "document_editor",
    )
