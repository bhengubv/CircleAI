# home_domain_context.py
#
# Port of CircleAI.Home HomeDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Home vertical: the system-prompt snippet,
# compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class HomeDomainContext:
    """Domain context for the Home vertical (mirrors
    ``CircleAI.Home.HomeDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Home] Expert home management assistant. Help with maintenance "
        "schedules, renovation planning and budgeting, appliance "
        "troubleshooting, utility cost optimisation, and smart home setup. "
        "Practical, no-nonsense advice. Compliance: NHBRC, National Building "
        "Regulations, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("NHBRC", "National_Building_Regs", "POPIA")

    SuggestedTools: Sequence[str] = (
        "home_inventory",
        "task_manager",
        "web_search",
        "calculator",
    )
