# personal_finance_domain_context.py
#
# Port of CircleAI.Personal.Finance PersonalFinanceDomainContext.cs
# (C# — the EXACT spec). (C# namespace is CircleAI.PersonalFinance.)
#
# Static domain-context data for the Personal.Finance vertical: the
# system-prompt snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class PersonalFinanceDomainContext:
    """Domain context for the Personal.Finance vertical (mirrors
    ``CircleAI.PersonalFinance.PersonalFinanceDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Personal.Finance] Personal finance coach. Help with monthly "
        "budgeting, emergency fund planning, debt snowball/avalanche strategy, "
        "savings goals, retirement planning basics, and investment options "
        "education. IMPORTANT: This is financial education, not advice. Recommend "
        "a registered financial planner for personalised investment advice. "
        "Compliance: FAIS Act, NCA, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("FAIS_Act_37_2002", "NCA", "POPIA", "Not_Financial_Advice")

    SuggestedTools: Sequence[str] = ("budget_tracker", "spreadsheet", "calculator", "web_search")
