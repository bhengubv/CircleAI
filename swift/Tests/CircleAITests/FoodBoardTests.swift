// FoodBoardTests.swift
//
// Exercises the Food records' Codable round-trips and the deterministic
// behaviour of InMemoryFoodBoard — recipes + ingredient search, meal logs
// (since, asc), and pantry (stock/use/expiring). Also checks the
// FoodDomainContext constants. Mirrors CircleAI.Food/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class FoodBoardTests: XCTestCase {

    func testPantryItemCodableRoundTrip() throws {
        let p = PantryItem(pantryItemId: "p1", name: "Flour", quantity: 2.5, unit: "kg", bestBefore: Date(timeIntervalSince1970: 99))
        XCTAssertEqual(try JSONDecoder().decode(PantryItem.self, from: try JSONEncoder().encode(p)), p)
        let noExpiry = PantryItem(pantryItemId: "p2", name: "Salt", quantity: 1, unit: "kg", bestBefore: nil)
        XCTAssertEqual(try JSONDecoder().decode(PantryItem.self, from: try JSONEncoder().encode(noExpiry)), noExpiry)
    }

    func testRecipeSearchByIngredientCaseInsensitiveAndValidation() throws {
        let b = InMemoryFoodBoard()
        b.addRecipe(Recipe(recipeId: "r1", title: "Pancakes", ingredients: ["Flour", "Milk", "Egg"], steps: ["mix"], servings: 4, prepMinutes: 15))
        b.addRecipe(Recipe(recipeId: "r2", title: "Omelette", ingredients: ["Egg", "Butter"], steps: ["whisk"], servings: 1, prepMinutes: 10))
        XCTAssertEqual(b.getRecipe("r1")?.title, "Pancakes")
        XCTAssertEqual(Set(try b.searchByIngredient("egg").map { $0.recipeId }), ["r1", "r2"])
        XCTAssertEqual(try b.searchByIngredient("milk").map { $0.recipeId }, ["r1"])
        XCTAssertThrowsError(try b.searchByIngredient("   ")) { XCTAssertEqual($0 as? FoodError, .ingredientRequired) }
    }

    func testMealLogsSinceAscending() {
        let b = InMemoryFoodBoard()
        let base = Date(timeIntervalSince1970: 1000)
        b.log(MealLog(logId: "l1", userId: "u1", recipeId: "r1", atUtc: base.addingTimeInterval(30), servings: 1))
        b.log(MealLog(logId: "l2", userId: "u1", recipeId: "r1", atUtc: base.addingTimeInterval(10), servings: 1))
        b.log(MealLog(logId: "l3", userId: "u1", recipeId: "r1", atUtc: base.addingTimeInterval(-5), servings: 1)) // before
        XCTAssertEqual(b.logsSince(userId: "u1", since: base).map { $0.logId }, ["l2", "l1"])
    }

    func testPantryStockUseAndExpiring() throws {
        let b = InMemoryFoodBoard()
        let d10 = Date(timeIntervalSince1970: 10), d20 = Date(timeIntervalSince1970: 20)
        b.stockPantry(PantryItem(pantryItemId: "p1", name: "Milk", quantity: 2, unit: "L", bestBefore: d10))
        b.stockPantry(PantryItem(pantryItemId: "p2", name: "Eggs", quantity: 12, unit: "ea", bestBefore: d20))
        b.stockPantry(PantryItem(pantryItemId: "p3", name: "Salt", quantity: 1, unit: "kg", bestBefore: nil))
        try b.use(pantryItemId: "p1", quantity: 3) // floors at 0 -> drops out of pantry()
        XCTAssertEqual(Set(b.pantry().map { $0.pantryItemId }), ["p2", "p3"])
        XCTAssertEqual(b.expiring(before: Date(timeIntervalSince1970: 15)).map { $0.pantryItemId }, ["p1"])
        XCTAssertEqual(b.expiring(before: Date(timeIntervalSince1970: 25)).map { $0.pantryItemId }, ["p1", "p2"]) // asc, nil excluded
        XCTAssertThrowsError(try b.use(pantryItemId: "ghost", quantity: 1)) { XCTAssertEqual($0 as? FoodError, .unknownPantryItem("ghost")) }
    }

    func testDomainContext() {
        XCTAssertTrue(FoodDomainContext.systemPromptSnippet.contains("[DOMAIN: Food]"))
        XCTAssertEqual(FoodDomainContext.complianceFlags, ["Food_Safety_Act", "POPIA"])
        XCTAssertEqual(FoodDomainContext.suggestedTools, ["recipe_tools", "nutrition_db", "shopping_list", "web_search"])
    }
}
