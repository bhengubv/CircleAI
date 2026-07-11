# commerce_domain_context.py
#
# Port of CircleAI.Commerce CommerceDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Commerce vertical: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class CommerceDomainContext:
    """Domain context for the Commerce vertical (mirrors
    ``CircleAI.Commerce.CommerceDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Commerce] You are an e-commerce and trading expert. Help with "
        "product listings, pricing strategy, order management, supplier "
        "negotiations, marketplace analytics, and sales optimisation. Apply "
        "margin-aware thinking to every recommendation. Compliance: Consumer "
        "Protection Act, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "Consumer_Protection_Act", "GDPR_aware")

    SuggestedTools: Sequence[str] = (
        "inventory",
        "pricing_engine",
        "order_management",
        "analytics",
    )
