// Food.swift
//
// Port of the Food vertical from src/CircleAI.Food/FoodPrimitives.cs and the
// static domain-context constants from FoodDomainContext.cs:
//   • Recipe, MealLog, PantryItem — domain records
//   • IFoodBoard                  — recipes, meal logs, pantry
//   • InMemoryFoodBoard           — deterministic in-memory impl
//   • FoodDomainContext           — system-prompt snippet + flags
//
// The Companion-facing wrapper (FoodCompanionAdapter) is an ICompanionSession
// decorator that prefixes the food domain prompt.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`; `DateTime? BestBefore` → `Date?`.
//   • `SearchByIngredient` requires a non-blank ingredient (else `.ingredientRequired`)
//     and matches recipes whose Ingredients contain it (case-insensitive substring).
//   • `LogsSince` filters user + AtUtc >= since, ordered ascending.
//   • `Use` on an unknown pantry item throws `.unknownPantryItem`; quantity is
//     floored at 0. `Pantry()` returns items with quantity > 0.
//   • `Expiring(before)` returns items with a BestBefore <= before, ordered
//     ascending by BestBefore. All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A recipe with ingredients and steps.
public struct Recipe: Sendable, Equatable, Codable {
    public let recipeId: String
    public let title: String
    public let ingredients: [String]
    public let steps: [String]
    public let servings: Int
    public let prepMinutes: Int

    public init(recipeId: String, title: String, ingredients: [String], steps: [String], servings: Int, prepMinutes: Int) {
        self.recipeId = recipeId
        self.title = title
        self.ingredients = ingredients
        self.steps = steps
        self.servings = servings
        self.prepMinutes = prepMinutes
    }
}

/// A logged meal referencing a recipe.
public struct MealLog: Sendable, Equatable, Codable {
    public let logId: String
    public let userId: String
    public let recipeId: String
    public let atUtc: Date
    public let servings: Int

    public init(logId: String, userId: String, recipeId: String, atUtc: Date, servings: Int) {
        self.logId = logId
        self.userId = userId
        self.recipeId = recipeId
        self.atUtc = atUtc
        self.servings = servings
    }
}

/// A pantry stock item.
public struct PantryItem: Sendable, Equatable, Codable {
    public let pantryItemId: String
    public let name: String
    public let quantity: Double
    public let unit: String
    public let bestBefore: Date?

    public init(pantryItemId: String, name: String, quantity: Double, unit: String, bestBefore: Date?) {
        self.pantryItemId = pantryItemId
        self.name = name
        self.quantity = quantity
        self.unit = unit
        self.bestBefore = bestBefore
    }
}

// MARK: - Errors

public enum FoodError: Error, Equatable, CustomStringConvertible {
    case ingredientRequired
    case unknownPantryItem(String)

    public var description: String {
        switch self {
        case .ingredientRequired: return "ingredient required"
        case .unknownPantryItem(let id): return "Unknown pantry item \(id)"
        }
    }
}

// MARK: - Contract

/// Recipes, meal logs, and pantry stock for the food vertical.
public protocol IFoodBoard: AnyObject, Sendable {
    func addRecipe(_ r: Recipe)
    func getRecipe(_ id: String) -> Recipe?
    func searchByIngredient(_ ingredient: String) throws -> [Recipe]
    func log(_ m: MealLog)
    func logsSince(userId: String, since: Date) -> [MealLog]
    func stockPantry(_ p: PantryItem)
    func use(pantryItemId: String, quantity: Double) throws
    func pantry() -> [PantryItem]
    func expiring(before: Date) -> [PantryItem]
}

// MARK: - InMemoryFoodBoard

/// Deterministic in-memory `IFoodBoard`. All state guarded by a single `NSLock`.
public final class InMemoryFoodBoard: IFoodBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var recipes: [String: Recipe] = [:]
    private var logs: [MealLog] = []
    private var pantryItems: [String: PantryItem] = [:]

    public init() {}

    public func addRecipe(_ r: Recipe) {
        lock.lock(); defer { lock.unlock() }
        recipes[r.recipeId] = r
    }

    public func getRecipe(_ id: String) -> Recipe? {
        lock.lock(); defer { lock.unlock() }
        return recipes[id]
    }

    public func searchByIngredient(_ ingredient: String) throws -> [Recipe] {
        if ingredient.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw FoodError.ingredientRequired }
        lock.lock(); defer { lock.unlock() }
        return recipes.values.filter { r in
            r.ingredients.contains { $0.range(of: ingredient, options: .caseInsensitive) != nil }
        }
    }

    public func log(_ m: MealLog) {
        lock.lock(); defer { lock.unlock() }
        logs.append(m)
    }

    public func logsSince(userId: String, since: Date) -> [MealLog] {
        lock.lock(); defer { lock.unlock() }
        return logs.filter { $0.userId == userId && $0.atUtc >= since }.sorted { $0.atUtc < $1.atUtc }
    }

    public func stockPantry(_ p: PantryItem) {
        lock.lock(); defer { lock.unlock() }
        pantryItems[p.pantryItemId] = p
    }

    public func use(pantryItemId: String, quantity: Double) throws {
        lock.lock(); defer { lock.unlock() }
        guard let p = pantryItems[pantryItemId] else { throw FoodError.unknownPantryItem(pantryItemId) }
        pantryItems[pantryItemId] = PantryItem(pantryItemId: p.pantryItemId, name: p.name, quantity: max(0, p.quantity - quantity), unit: p.unit, bestBefore: p.bestBefore)
    }

    public func pantry() -> [PantryItem] {
        lock.lock(); defer { lock.unlock() }
        return pantryItems.values.filter { $0.quantity > 0 }
    }

    public func expiring(before: Date) -> [PantryItem] {
        lock.lock(); defer { lock.unlock() }
        return pantryItems.values
            .filter { $0.bestBefore != nil && $0.bestBefore! <= before }
            .sorted { ($0.bestBefore ?? .distantFuture) < ($1.bestBefore ?? .distantFuture) }
    }
}

// MARK: - FoodDomainContext

/// Static domain-context constants for the food vertical.
public enum FoodDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Food] Expert culinary companion. Help with recipe creation, meal planning, ingredient substitutions, cooking technique explanation, dietary restriction management, and kitchen organisation. Celebrate food culture in all its diversity. Compliance: Food Safety Act, POPIA."
    public static let complianceFlags: [String] = ["Food_Safety_Act", "POPIA"]
    public static let suggestedTools: [String] = ["recipe_tools", "nutrition_db", "shopping_list", "web_search"]
}

// MARK: - FoodCompanionAdapter

/// An `ICompanionSession` decorator that prepends the food domain system prompt
/// to every conversational call and adds culinary helper methods.
/// Port of `CircleAI.Food.FoodCompanionAdapter`. Identity/context/feedback are
/// forwarded to the inner session; proactive events forward through the inner
/// session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class FoodCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String { try await inner.agent(enrich(instruction)) }

    private func enrich(_ m: String) -> String { "\(FoodDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Food helpers ──────────────────────────────────────────────────────────

    /// Create a recipe (C# `CreateRecipeAsync`).
    public func createRecipe(ingredients: String, dietary: String, difficulty: String) async throws -> String {
        try await inner.agent(
            "Create a recipe using: \(ingredients). Dietary requirements: \(dietary). Difficulty: \(difficulty). Include prep time, cook time, step-by-step method, and nutritional estimate.")
    }

    /// Plan meals (C# `PlanMealsAsync`).
    public func planMeals(days: String, people: String, dietary: String, budget: String) async throws -> String {
        try await inner.agent(
            "Plan \(days) days of meals for \(people) people. Dietary: \(dietary). Budget: \(budget). Include breakfast, lunch, dinner, and a shopping list.")
    }

    /// Suggest recipes from pantry (C# `SuggestRecipeFromPantryAsync`).
    public func suggestRecipeFromPantry(availableIngredients: String, dietNotes: String) async throws -> String {
        try await inner.agent(
            "Suggest 3 recipes using mostly: \(availableIngredients). Dietary: \(dietNotes). Pick varied techniques + cuisines.")
    }

    /// Estimate nutrition per serving (C# `EstimateNutritionAsync`).
    public func estimateNutrition(recipeIngredients: String, servings: Int) async throws -> String {
        try await inner.agent(
            "Estimate nutrition per serving for \(servings)-serving recipe: \(recipeIngredients). Output kcal, P/F/C, sodium, fibre.")
    }

    /// Suggest ingredient substitutes (C# `SubstituteIngredientAsync`).
    public func substituteIngredient(ingredient: String, reason: String) async throws -> String {
        try await inner.agent(
            "Suggest 3 substitutes for \(ingredient) (reason: \(reason)). For each: ratio, flavour impact, technique tweak.")
    }

    /// Convert a meal plan to a shopping list (C# `PlanShoppingListAsync`).
    public func planShoppingList(weeklyMealPlan: String) async throws -> String {
        try await inner.agent(
            "Convert this meal plan to a shopping list grouped by store aisle: \(weeklyMealPlan). Aggregate quantities.")
    }
}
