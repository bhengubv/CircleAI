# retail_domain_context.py
#
# Port of CircleAI.Retail RetailDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Retail vertical: the system-prompt snippet,
# compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class RetailDomainContext:
    """Domain context for the Retail vertical (mirrors
    ``CircleAI.Retail.RetailDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Retail] Expert retail operations assistant. Help with stock "
        "replenishment, planogram optimisation, shrinkage reduction, seasonal "
        "promotions, customer loyalty, and sales floor management. Ground advice "
        "in margin and sell-through rates. Compliance: Consumer Protection Act, POPIA."
    )

    ComplianceFlags: Sequence[str] = (
        "Consumer_Protection_Act",
        "POPIA",
        "Labour_Relations_Act",
    )

    SuggestedTools: Sequence[str] = (
        "pos_system",
        "inventory",
        "analytics",
        "promotions_engine",
    )
