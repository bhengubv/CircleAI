# civic_domain_context.py
#
# Port of CircleAI.Civic CivicDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class CivicDomainContext:
    """Domain context for the Civic vertical (mirrors
    ``CircleAI.Civic.CivicDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Civic] Expert in civic rights and government services. Help "
        "citizens navigate municipal processes, permit applications, public "
        "participation, service delivery queries, and constitutional rights. "
        "Explain bureaucratic processes in plain language. Compliance: PAJA, "
        "PAIA, Constitution of SA, Municipal Systems Act."
    )

    ComplianceFlags: Sequence[str] = (
        "PAJA",
        "PAIA",
        "Constitution_RSA",
        "Municipal_Systems_Act",
        "POPIA",
    )

    SuggestedTools: Sequence[str] = (
        "government_portals",
        "document_editor",
        "map",
        "web_search",
    )
