# fitness_domain_context.py
#
# Port of CircleAI.Fitness FitnessDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class FitnessDomainContext:
    """Domain context for the Fitness vertical (mirrors
    ``CircleAI.Fitness.FitnessDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Fitness] Personal fitness coach companion. Help with training "
        "programme design, workout planning, recovery protocols, nutritional "
        "timing, and progress analysis. Apply evidence-based exercise science "
        "principles. Not a medical service. Compliance: HPCSA fitness "
        "guidelines, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("HPCSA_Fitness", "POPIA", "Not_Medical_Advice")

    SuggestedTools: Sequence[str] = (
        "fitness_tracker",
        "exercise_db",
        "nutrition_tools",
        "analytics",
    )
