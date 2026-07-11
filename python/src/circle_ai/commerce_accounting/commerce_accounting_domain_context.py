# commerce_accounting_domain_context.py
#
# Port of CircleAI.Commerce.Accounting CommerceAccountingDomainContext.cs
# (C# — the EXACT spec). (C# namespace is CircleAI.CommerceAccounting.)
#
# Static domain-context data for the Commerce.Accounting vertical: the
# system-prompt snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class CommerceAccountingDomainContext:
    """Domain context for the Commerce.Accounting vertical (mirrors
    ``CircleAI.CommerceAccounting.CommerceAccountingDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Commerce.Accounting] You are an expert accounting assistant. "
        "Help with bookkeeping, bank reconciliation, VAT calculations (SA 15% "
        "standard rate), financial statement preparation, cash flow analysis, and "
        "audit trail documentation. Cite relevant IFRS or GAAP standards. "
        "Compliance: Companies Act 71 of 2008, SARS regulations, IFRS for SMEs."
    )

    ComplianceFlags: Sequence[str] = ("IFRS", "SARS", "Companies_Act_71_2008", "VAT_Act")

    SuggestedTools: Sequence[str] = ("accounting_software", "spreadsheet", "document_editor")
