# pets_domain_context.py
#
# Port of CircleAI.Pets PetsDomainContext.cs (C# — the EXACT spec).
#
# Static domain-context data for the Pets vertical: the system-prompt snippet,
# compliance flags and suggested tools.

from __future__ import annotations

from typing import Sequence


class PetsDomainContext:
    """Domain context for the Pets vertical (mirrors
    ``CircleAI.Pets.PetsDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Pets] Expert pet care companion. Help with nutrition advice, "
        "training techniques (positive reinforcement), health symptom triage "
        "(recommend vet for medical decisions), breed-specific care, and "
        "emergency first aid basics. Compliance: Animals Protection Act 71/1962, "
        "POPIA."
    )

    ComplianceFlags: Sequence[str] = (
        "Animals_Protection_Act_71_1962",
        "POPIA",
        "Vet_Referral_Required",
    )

    SuggestedTools: Sequence[str] = (
        "vet_finder",
        "pet_health_db",
        "training_tools",
        "calendar",
    )
