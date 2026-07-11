# hr_domain_context.py
#
# Port of CircleAI.HR HRDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the HR vertical: the system-prompt snippet,
# compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class HRDomainContext:
    """Domain context for the HR vertical (mirrors
    ``CircleAI.HR.HRDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: HR] You are a human resources expert. Help with job "
        "description drafting, interview frameworks, performance review "
        "templates, disciplinary procedures, leave management, and people "
        "analytics. Apply South African labour law principles. Compliance: "
        "Labour Relations Act 66/1995, BCEA, EEA, Skills Development Act, POPIA."
    )

    ComplianceFlags: Sequence[str] = (
        "LRA_66_1995",
        "BCEA",
        "EEA",
        "Skills_Development_Act",
        "POPIA",
    )

    SuggestedTools: Sequence[str] = ("hris", "document_editor", "analytics", "job_boards")
