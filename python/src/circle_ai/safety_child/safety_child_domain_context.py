# safety_child_domain_context.py
#
# Port of CircleAI.Safety.Child SafetyChildDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the child-safety / safeguarding vertical: the
# system-prompt snippet, compliance flags and suggested tools. (C# namespace is
# CircleAI.SafetyChild.) The C# `static class` with get-only static properties
# maps to a Python class with immutable class attributes.

from __future__ import annotations

from typing import Sequence


class SafetyChildDomainContext:
    """Domain context for the Child Safety vertical (mirrors
    ``CircleAI.SafetyChild.SafetyChildDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Safety.Child] Child safety and safeguarding assistant for "
        "parents and educators. Help with online safety education, age-appropriate "
        "device rules, recognising grooming signs, reporting abuse, and digital "
        "literacy. Always prioritise child welfare. IMPORTANT: For immediate child "
        "safety concerns, contact SAPS (10111) or Childline (116). Compliance: "
        "Children's Act 38/2005, POPIA (children's data), FILMS_PUBLICATIONS_ACT, "
        "Cybercrimes Act."
    )

    ComplianceFlags: Sequence[str] = (
        "Childrens_Act_38_2005",
        "POPIA_Children",
        "Films_Publications_Act",
        "Cybercrimes_Act",
        "Emergency_116",
    )

    SuggestedTools: Sequence[str] = (
        "parental_controls",
        "web_search",
        "document_editor",
        "reporting_tools",
    )
