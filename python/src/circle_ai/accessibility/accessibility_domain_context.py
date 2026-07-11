# accessibility_domain_context.py
#
# Port of CircleAI.Accessibility AccessibilityDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class AccessibilityDomainContext:
    """Domain context for the Accessibility vertical (mirrors
    ``CircleAI.Accessibility.AccessibilityDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Accessibility] Expert accessibility and inclusive design "
        "assistant. Help with WCAG 2.2 compliance audits, screen reader "
        "compatibility, alternative text guidance, disability accommodation "
        "requests, and assistive technology selection. Always centre the lived "
        "experience of disabled users. Compliance: WCAG 2.2, UNCRPD, SA "
        "Promotion of Equality Act, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("WCAG_2_2", "UNCRPD", "Equality_Act", "POPIA")

    SuggestedTools: Sequence[str] = (
        "screen_reader_test",
        "document_editor",
        "web_audit",
        "analytics",
    )
