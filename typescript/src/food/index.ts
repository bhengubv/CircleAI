// food/index.ts
// Full-parity port of CircleAI.Food (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Food vertical: recipes, meal logs, and
// a pantry with usage + expiry. Plus the static FoodDomainContext.
//
// NOTE: The C# FoodCompanionAdapter (an ICompanionSession LLM-prompt wrapper) is
// intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   IReadOnlyList<string>            → readonly string[]
//   int Servings / PrepMinutes       → number
//   double Quantity                  → number
//   DateTime? BestBefore             → Date | null
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   SearchByIngredient — throws on blank; recipes whose Ingredients contain the
//                        term (ordinal case-insensitive substring).
//   LogsSince          — user's logs with AtUtc >= since, AtUtc ascending.
//   Use                — throws on unknown; Quantity = max(0, Quantity − quantity).
//   Pantry             — items with Quantity > 0 (map insertion order).
//   Expiring           — items with BestBefore <= before, BestBefore ascending.

/** A recipe. Mirrors C# `Recipe` record. */
export interface Recipe {
  readonly recipeId: string;
  readonly title: string;
  readonly ingredients: readonly string[];
  readonly steps: readonly string[];
  readonly servings: number;
  readonly prepMinutes: number;
}

/** Constructs a {@link Recipe}. */
export function recipe(
  recipeId: string,
  title: string,
  ingredients: readonly string[],
  steps: readonly string[],
  servings: number,
  prepMinutes: number,
): Recipe {
  return { recipeId, title, ingredients, steps, servings, prepMinutes };
}

/** A meal log entry. Mirrors C# `MealLog` record. */
export interface MealLog {
  readonly logId: string;
  readonly userId: string;
  readonly recipeId: string;
  /** UTC instant of the meal (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly servings: number;
}

/** Constructs a {@link MealLog}. */
export function mealLog(
  logId: string,
  userId: string,
  recipeId: string,
  atUtc: Date,
  servings: number,
): MealLog {
  return { logId, userId, recipeId, atUtc, servings };
}

/** A pantry item. Mirrors C# `PantryItem` record. */
export interface PantryItem {
  readonly pantryItemId: string;
  readonly name: string;
  readonly quantity: number;
  readonly unit: string;
  /** Best-before date, or null (C# `DateTime? BestBefore`). */
  readonly bestBefore: Date | null;
}

/** Constructs a {@link PantryItem}. */
export function pantryItem(
  pantryItemId: string,
  name: string,
  quantity: number,
  unit: string,
  bestBefore: Date | null,
): PantryItem {
  return { pantryItemId, name, quantity, unit, bestBefore };
}

/** The food board contract. Mirrors C# `IFoodBoard`. */
export interface IFoodBoard {
  addRecipe(r: Recipe): void;
  getRecipe(id: string): Recipe | undefined;
  searchByIngredient(ingredient: string): readonly Recipe[];
  log(m: MealLog): void;
  logsSince(userId: string, since: Date): readonly MealLog[];
  stockPantry(p: PantryItem): void;
  use(pantryItemId: string, quantity: number): void;
  pantry(): readonly PantryItem[];
  expiring(before: Date): readonly PantryItem[];
}

/** Deterministic in-memory {@link IFoodBoard}. */
export class InMemoryFoodBoard implements IFoodBoard {
  private readonly recipes = new Map<string, Recipe>();
  private readonly logs: MealLog[] = [];
  private readonly pantryItems = new Map<string, PantryItem>();

  addRecipe(r: Recipe): void {
    if (r == null) throw new Error("r required");
    this.recipes.set(r.recipeId, r);
  }

  getRecipe(id: string): Recipe | undefined {
    return this.recipes.get(id);
  }

  searchByIngredient(ingredient: string): readonly Recipe[] {
    if (ingredient == null || ingredient.trim() === "") throw new Error("ingredient required");
    const needle = ingredient.toLowerCase();
    return [...this.recipes.values()].filter((r) =>
      r.ingredients.some((i) => i.toLowerCase().includes(needle)),
    );
  }

  log(m: MealLog): void {
    if (m == null) throw new Error("m required");
    this.logs.push(m);
  }

  logsSince(userId: string, since: Date): readonly MealLog[] {
    const sinceMs = since.getTime();
    return this.logs
      .filter((l) => l.userId === userId && l.atUtc.getTime() >= sinceMs)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  stockPantry(p: PantryItem): void {
    if (p == null) throw new Error("p required");
    this.pantryItems.set(p.pantryItemId, p);
  }

  use(pantryItemId: string, quantity: number): void {
    const p = this.pantryItems.get(pantryItemId);
    if (p === undefined) throw new Error(`Unknown pantry item ${pantryItemId}`);
    this.pantryItems.set(pantryItemId, { ...p, quantity: Math.max(0, p.quantity - quantity) });
  }

  pantry(): readonly PantryItem[] {
    return [...this.pantryItems.values()].filter((p) => p.quantity > 0);
  }

  expiring(before: Date): readonly PantryItem[] {
    const beforeMs = before.getTime();
    return [...this.pantryItems.values()]
      .filter((p) => p.bestBefore !== null && p.bestBefore.getTime() <= beforeMs)
      .sort((a, b) => (a.bestBefore as Date).getTime() - (b.bestBefore as Date).getTime());
  }
}

/**
 * Static domain context for the Food vertical. Mirrors C# `FoodDomainContext`.
 */
export const FoodDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Food] Expert culinary companion. Help with recipe creation, meal planning, ingredient substitutions, cooking technique explanation, dietary restriction management, and kitchen organisation. Celebrate food culture in all its diversity. Compliance: Food Safety Act, POPIA.",
  complianceFlags: ["Food_Safety_Act", "POPIA"] as readonly string[],
  suggestedTools: ["recipe_tools", "nutrition_db", "shopping_list", "web_search"] as readonly string[],
} as const;
