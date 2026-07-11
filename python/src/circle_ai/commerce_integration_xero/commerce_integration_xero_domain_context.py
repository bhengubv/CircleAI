# commerce_integration_xero_domain_context.py
#
# Port of CircleAI.Commerce.Integration.Xero
# CommerceIntegrationXeroDomainContext.cs (C# — the EXACT spec).
# (C# namespace is CircleAI.CommerceIntegrationXero.)
#
# Static domain-context data for the Xero integration: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class CommerceIntegrationXeroDomainContext:
    """Domain context for the Xero integration (mirrors
    ``CircleAI.CommerceIntegrationXero.CommerceIntegrationXeroDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Commerce.Integration.Xero] You are a Xero accounting platform "
        "expert. Help with Xero chart of accounts, invoice creation, bank feeds, "
        "reconciliation workflows, Xero reporting, and API integration "
        "troubleshooting. Reference Xero HQ documentation for accuracy. "
        "Compliance: SARS, IFRS for SMEs, Xero data handling standards."
    )

    ComplianceFlags: Sequence[str] = ("SARS", "IFRS", "Xero_Data_Standards", "POPIA")

    SuggestedTools: Sequence[str] = ("xero_api", "spreadsheet", "document_editor")
