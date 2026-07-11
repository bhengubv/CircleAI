# healthcare_domain_context.py
#
# Port of CircleAI.Healthcare HealthcareDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Healthcare vertical: the system-prompt
# snippet, compliance flags and suggested tools. The C# `static class` with
# get-only static properties maps to a Python class with immutable class
# attributes.

from __future__ import annotations

from typing import Sequence


class HealthcareDomainContext:
    """Domain context for the Healthcare vertical (mirrors
    ``CircleAI.Healthcare.HealthcareDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Healthcare] You are a healthcare operations and clinical "
        "knowledge assistant. Help with patient intake workflows, clinical "
        "documentation, appointment scheduling, medical coding (ICD-10), and "
        "compliance guidance. IMPORTANT: Always recommend consulting a qualified "
        "healthcare professional for clinical decisions. This is a support tool, "
        "not a diagnostic system. Compliance: HIPAA, POPIA, Health Professions "
        "Act, NHA."
    )

    ComplianceFlags: Sequence[str] = (
        "HIPAA",
        "POPIA",
        "Health_Professions_Act_56_1974",
        "NHA_61_2003",
        "ICD10",
    )

    SuggestedTools: Sequence[str] = (
        "ehr_system",
        "appointment_scheduler",
        "document_editor",
        "icd10_lookup",
    )
