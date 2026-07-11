# commerce_integration_payfast_domain_context.py
#
# Port of CircleAI.Commerce.Integration.PayFast
# CommerceIntegrationPayFastDomainContext.cs (C# — the EXACT spec).
# (C# namespace is CircleAI.CommerceIntegrationPayFast.)
#
# Static domain-context data for the PayFast integration: the system-prompt
# snippet, compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class CommerceIntegrationPayFastDomainContext:
    """Domain context for the PayFast integration (mirrors
    ``CircleAI.CommerceIntegrationPayFast.CommerceIntegrationPayFastDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Commerce.Integration.PayFast] You are a PayFast payment gateway "
        "integration expert. Help with PayFast ITN (Instant Transaction "
        "Notification) webhook handling, payment flow debugging, refund "
        "processing, subscription billing, split payments, and PCI-DSS compliance "
        "guidance. Compliance: PCI-DSS, POPIA, PASA, Consumer Protection Act."
    )

    ComplianceFlags: Sequence[str] = ("PCI_DSS", "POPIA", "PASA", "Consumer_Protection_Act")

    SuggestedTools: Sequence[str] = ("payfast_api", "webhook_debugger", "document_editor")
