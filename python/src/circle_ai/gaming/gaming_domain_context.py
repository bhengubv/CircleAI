# gaming_domain_context.py
#
# Port of CircleAI.Gaming GamingDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class GamingDomainContext:
    """Domain context for the Gaming vertical (mirrors
    ``CircleAI.Gaming.GamingDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Gaming] Expert gaming companion. Help with game strategy "
        "guides, build optimisation, community event planning, game review "
        "writing, speedrun technique research, and gaming health (screen time, "
        "ergonomics). Compliance: POPIA, WASPA (in-game purchases), child "
        "protection where applicable."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "WASPA", "Child_Protection")

    SuggestedTools: Sequence[str] = ("game_db", "community_tools", "analytics", "web_search")
