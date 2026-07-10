# safety_domain_context.py
#
# Port of CircleAI.Safety SafetyDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the personal-safety vertical: the system-prompt
# snippet, compliance flags and suggested tools. The C# `static class` with
# get-only static properties maps to a Python class with immutable class
# attributes (tuples stand in for IReadOnlyList<string>).

from __future__ import annotations

from typing import Sequence


class SafetyDomainContext:
    """Domain context for the Safety vertical (mirrors
    ``CircleAI.Safety.SafetyDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Safety] Personal safety and emergency preparedness assistant. "
        "Help with home security assessments, emergency response plans, first aid "
        "guidance (always recommend professional training), situational awareness "
        "tips, and crisis communication. IMPORTANT: For life-threatening "
        "emergencies, direct immediately to 10111 (SAPS) or 10177 (ambulance). "
        "Compliance: POPIA, OHS Act."
    )

    ComplianceFlags: Sequence[str] = ("POPIA", "OHS_Act", "Emergency_Protocol_10111")

    SuggestedTools: Sequence[str] = (
        "emergency_contacts",
        "document_editor",
        "map",
        "web_search",
    )
