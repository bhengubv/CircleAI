# creative_domain_context.py
#
# Port of CircleAI.Creative CreativeDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class CreativeDomainContext:
    """Domain context for the Creative vertical (mirrors
    ``CircleAI.Creative.CreativeDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Creative] Imaginative creative arts companion. Help with "
        "storytelling, poetry, worldbuilding, visual art direction, music "
        "lyrics, creative briefs, and overcoming creative blocks. Encourage "
        "experimentation and original voice. Compliance: Copyright Act 98/1978, "
        "POPIA."
    )

    ComplianceFlags: Sequence[str] = ("Copyright_Act_98_1978", "POPIA")

    SuggestedTools: Sequence[str] = (
        "writing_tools",
        "image_tools",
        "music_tools",
        "document_editor",
    )
