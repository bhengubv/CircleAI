// Food.kt
//
// Kotlin port of CircleAI.Food (FoodPrimitives.cs + FoodDomainContext.cs +
// FoodCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory food board: recipes, meal logs, and a pantry.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTime?`/`DateTimeOffset` -> `Instant?`/`Instant`.
//   * `SearchByIngredient` = case-insensitive substring match on any ingredient
//     (blank query throws).
//   * `Use` decrements quantity, floored at 0 (unknown item throws).
//   * `Pantry` returns items with quantity > 0.
//   * `Expiring` = items with a BestBefore at/before the cutoff, ASC by BestBefore.

package com.bhengubv.circleai.food

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.max

// =====================================================================
// Primitives (FoodPrimitives.cs)
// =====================================================================

/** A recipe. Mirrors C# `Recipe`. */
data class Recipe(
    val recipeId: String,
    val title: String,
    val ingredients: List<String>,
    val steps: List<String>,
    val servings: Int,
    val prepMinutes: Int,
)

/** A meal-log entry. Mirrors C# `MealLog`. */
data class MealLog(val logId: String, val userId: String, val recipeId: String, val atUtc: Instant, val servings: Int)

/** A pantry item. Mirrors C# `PantryItem`. */
data class PantryItem(val pantryItemId: String, val name: String, val quantity: Double, val unit: String, val bestBefore: Instant?)

/** Deterministic food board. Mirrors C# `IFoodBoard`. */
interface IFoodBoard {
    fun addRecipe(r: Recipe)
    fun getRecipe(id: String): Recipe?
    fun searchByIngredient(ingredient: String): List<Recipe>
    fun log(m: MealLog)
    fun logsSince(userId: String, since: Instant): List<MealLog>
    fun stockPantry(p: PantryItem)
    fun use(pantryItemId: String, quantity: Double)
    fun pantry(): List<PantryItem>
    fun expiring(before: Instant): List<PantryItem>
}

/** In-memory [IFoodBoard]. Mirrors C# `InMemoryFoodBoard`. */
class InMemoryFoodBoard : IFoodBoard {
    private val recipes = ConcurrentHashMap<String, Recipe>()
    private val logs = mutableListOf<MealLog>()
    private val pantry = ConcurrentHashMap<String, PantryItem>()
    private val lock = Any()

    override fun addRecipe(r: Recipe) { recipes[r.recipeId] = r }
    override fun getRecipe(id: String): Recipe? = recipes[id]

    override fun searchByIngredient(ingredient: String): List<Recipe> {
        if (ingredient.isBlank()) throw IllegalArgumentException("ingredient required")
        return recipes.values.filter { r -> r.ingredients.any { it.contains(ingredient, ignoreCase = true) } }
    }

    override fun log(m: MealLog) { synchronized(lock) { logs.add(m) } }
    override fun logsSince(userId: String, since: Instant): List<MealLog> = synchronized(lock) {
        logs.filter { it.userId == userId && !it.atUtc.isBefore(since) }.sortedBy { it.atUtc }
    }

    override fun stockPantry(p: PantryItem) { pantry[p.pantryItemId] = p }

    override fun use(pantryItemId: String, quantity: Double) {
        val p = pantry[pantryItemId] ?: throw IllegalStateException("Unknown pantry item $pantryItemId")
        pantry[pantryItemId] = p.copy(quantity = max(0.0, p.quantity - quantity))
    }

    override fun pantry(): List<PantryItem> = pantry.values.filter { it.quantity > 0 }

    override fun expiring(before: Instant): List<PantryItem> =
        pantry.values.filter { it.bestBefore != null && !it.bestBefore.isAfter(before) }
            .sortedBy { it.bestBefore }
}

// =====================================================================
// DomainContext (FoodDomainContext.cs)
// =====================================================================

/** Static domain context for Food. Mirrors C# `FoodDomainContext`. */
object FoodDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Food] Expert culinary companion. Help with recipe creation, meal planning, ingredient " +
            "substitutions, cooking technique explanation, dietary restriction management, and kitchen " +
            "organisation. Celebrate food culture in all its diversity. Compliance: Food Safety Act, POPIA."

    val complianceFlags: List<String> = listOf("Food_Safety_Act", "POPIA")

    val suggestedTools: List<String> = listOf("recipe_tools", "nutrition_db", "shopping_list", "web_search")
}

// =====================================================================
// CompanionAdapter (FoodCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Food snippet + helpers. Mirrors C# `FoodCompanionAdapter`. */
class FoodCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
    override val sessionId: String get() = inner.sessionId
    override val identityId: String get() = inner.identityId
    override val interfaceKind: InterfaceKind get() = inner.interfaceKind
    override val history: List<CompanionTurn> get() = inner.history
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = inner.proactiveEvents

    override fun getContext(): CompanionContext = inner.getContext()
    override suspend fun refreshContextAsync() = inner.refreshContextAsync()
    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) =
        inner.signalFeedbackAsync(positive, note)
    override fun close() = inner.close()

    override suspend fun sendAsync(message: String): String = inner.sendAsync(enrich(message))
    override fun streamAsync(message: String): Flow<String> = inner.streamAsync(enrich(message))
    override suspend fun agentAsync(instruction: String): String = inner.agentAsync(enrich(instruction))

    private fun enrich(m: String): String = "${FoodDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun createRecipeAsync(ingredients: String, dietary: String, difficulty: String): String =
        inner.agentAsync("Create a recipe using: $ingredients. Dietary requirements: $dietary. Difficulty: $difficulty. Include prep time, cook time, step-by-step method, and nutritional estimate.")

    suspend fun planMealsAsync(days: String, people: String, dietary: String, budget: String): String =
        inner.agentAsync("Plan $days days of meals for $people people. Dietary: $dietary. Budget: $budget. Include breakfast, lunch, dinner, and a shopping list.")

    suspend fun suggestRecipeFromPantryAsync(availableIngredients: String, dietNotes: String): String =
        inner.agentAsync("Suggest 3 recipes using mostly: $availableIngredients. Dietary: $dietNotes. Pick varied techniques + cuisines.")

    suspend fun estimateNutritionAsync(recipeIngredients: String, servings: Int): String =
        inner.agentAsync("Estimate nutrition per serving for $servings-serving recipe: $recipeIngredients. Output kcal, P/F/C, sodium, fibre.")

    suspend fun substituteIngredientAsync(ingredient: String, reason: String): String =
        inner.agentAsync("Suggest 3 substitutes for $ingredient (reason: $reason). For each: ratio, flavour impact, technique tweak.")

    suspend fun planShoppingListAsync(weeklyMealPlan: String): String =
        inner.agentAsync("Convert this meal plan to a shopping list grouped by store aisle: $weeklyMealPlan. Aggregate quantities.")
}
