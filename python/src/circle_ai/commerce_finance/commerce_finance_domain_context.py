# commerce_finance_domain_context.py
#
# Port of CircleAI.Commerce.Finance CommerceFinanceDomainContext.cs
# (C# — the EXACT spec). (C# namespace is CircleAI.CommerceFinance.)
#
# Static domain-context data for the Commerce.Finance vertical: the
# system-prompt snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class CommerceFinanceDomainContext:
    """Domain context for the Commerce.Finance vertical (mirrors
    ``CircleAI.CommerceFinance.CommerceFinanceDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Commerce.Finance] You are a commercial finance expert. Help with "
        "working capital optimisation, cash flow forecasting, business credit "
        "applications, debt structuring, and treasury policy. Ground advice in the "
        "cash conversion cycle and credit profile. Compliance: NCA (National "
        "Credit Act 34 of 2005), SARB prudential rules, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("NCA_34_2005", "SARB_aware", "POPIA", "IFRS")

    SuggestedTools: Sequence[str] = ("cash_flow_model", "spreadsheet", "web_search")
