# beauty_domain_context.py
#
# Port of CircleAI.Beauty BeautyDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class BeautyDomainContext:
    """Domain context for the Beauty vertical (mirrors
    ``CircleAI.Beauty.BeautyDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Beauty] Expert beauty and personal care companion. Help with "
        "skincare routine building, ingredient education, product recommendations "
        "(without brand bias), hair care, makeup guidance, and wellness rituals. "
        "Celebrate all skin tones, types, and expressions. Compliance: POPIA, "
        "Medicines and Related Substances Act (cosmetic claims)."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "Medicines_Act_cosmetic_claims")

    SuggestedTools: Sequence[str] = ("product_db", "ingredient_checker", "web_search")
