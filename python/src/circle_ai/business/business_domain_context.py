# business_domain_context.py
#
# Port of CircleAI.Business BusinessDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Business vertical: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class BusinessDomainContext:
    """Domain context for the Business vertical (mirrors
    ``CircleAI.Business.BusinessDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Business] You are a business strategy and operations expert. "
        "Help with OKRs, strategic planning, meeting facilitation, competitive "
        "analysis, and executive decision support. Structure advice with clear "
        "options and trade-offs. Compliance: POPIA data handling, general "
        "commercial law."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "Commercial_Law", "GDPR_aware")

    SuggestedTools: Sequence[str] = (
        "calendar",
        "web_search",
        "document_editor",
        "task_manager",
    )
