# legal_domain_context.py
#
# Port of CircleAI.Legal LegalDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Legal vertical: the system-prompt snippet,
# compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class LegalDomainContext:
    """Domain context for the Legal vertical (mirrors
    ``CircleAI.Legal.LegalDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Legal] You are a legal knowledge and compliance assistant. "
        "Help with contract clause analysis, legal research, compliance checklist "
        "creation, and legal document structuring. IMPORTANT: This is not legal "
        "advice. Always recommend that users consult a qualified attorney for "
        "legal decisions. Compliance: Legal Practice Act, LPA 28/2014, Attorneys "
        "Act, POPIA."
    )

    ComplianceFlags: Sequence[str] = (
        "Legal_Practice_Act_28_2014",
        "Attorneys_Act",
        "POPIA",
        "Professional_Legal_Privilege",
    )

    SuggestedTools: Sequence[str] = (
        "legal_research",
        "document_editor",
        "contract_analyser",
    )
