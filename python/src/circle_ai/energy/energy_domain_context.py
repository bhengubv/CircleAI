# energy_domain_context.py
#
# Port of CircleAI.Energy EnergyDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class EnergyDomainContext:
    """Domain context for the Energy vertical (mirrors
    ``CircleAI.Energy.EnergyDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Energy] Expert energy management and renewable energy "
        "assistant. Help with solar/wind feasibility, load flow analysis, tariff "
        "optimisation, battery storage sizing, grid connection requirements, and "
        "energy efficiency audits. Apply NERSA and SABS standards. Compliance: "
        "Electricity Act, NERSA regulations, Municipal By-laws, Renewable Energy "
        "IPP."
    )

    ComplianceFlags: Sequence[str] = (
        "Electricity_Act",
        "NERSA",
        "SABS",
        "Municipal_Energy_By_laws",
        "POPIA",
    )

    SuggestedTools: Sequence[str] = (
        "energy_model",
        "analytics",
        "document_editor",
        "web_search",
    )
