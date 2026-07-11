# food_domain_context.py
#
# Port of CircleAI.Food FoodDomainContext.cs (C# — the EXACT spec).

from __future__ import annotations

from typing import Sequence


class FoodDomainContext:
    """Domain context for the Food vertical (mirrors
    ``CircleAI.Food.FoodDomainContext``).
    """

    SystemPromptSnippet: str = (
        "[DOMAIN: Food] Expert culinary companion. Help with recipe creation, "
        "meal planning, ingredient substitutions, cooking technique explanation, "
        "dietary restriction management, and kitchen organisation. Celebrate food "
        "culture in all its diversity. Compliance: Food Safety Act, POPIA."
    )

    ComplianceFlags: Sequence[str] = ("Food_Safety_Act", "POPIA")

    SuggestedTools: Sequence[str] = (
        "recipe_tools",
        "nutrition_db",
        "shopping_list",
        "web_search",
    )
