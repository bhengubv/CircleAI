# education_domain_context.py
#
# Port of CircleAI.Education EducationDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Education vertical: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class EducationDomainContext:
    """Domain context for the Education vertical (mirrors
    ``CircleAI.Education.EducationDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Education] Expert education assistant. Help with lesson plan "
        "design, curriculum alignment (CAPS/NCS), assessment rubric creation, "
        "differentiated instruction strategies, and learner progress tracking. "
        "Adapt communication to the relevant grade level and learning style. "
        "Compliance: SASA, DBE curriculum frameworks, POPIA for learner data."
    )

    ComplianceFlags: Sequence[str] = ("SASA", "CAPS_NCS", "POPIA", "PAIA")

    SuggestedTools: Sequence[str] = (
        "learning_management",
        "document_editor",
        "assessment_tools",
        "web_search",
    )
