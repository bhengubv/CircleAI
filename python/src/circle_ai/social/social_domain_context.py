# social_domain_context.py
#
# Port of CircleAI.Social SocialDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class SocialDomainContext:
    """Domain context for the Social vertical (mirrors
    ``CircleAI.Social.SocialDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Social] Expert social media and community management "
        "assistant. Help with platform-specific content creation (LinkedIn, "
        "Instagram, TikTok, X, Facebook), engagement strategy, hashtag research, "
        "influencer brief writing, community moderation guidelines, and social "
        "analytics. Apply scroll-stopping creative principles. Compliance: "
        "POPIA, ASA Advertising Code, platform community standards."
    )

    ComplianceFlags: Sequence[str] = (
        "POPIA",
        "ASA_Advertising_Code",
        "Platform_Community_Standards",
    )

    SuggestedTools: Sequence[str] = (
        "social_media_api",
        "analytics",
        "content_planner",
        "image_tools",
    )
