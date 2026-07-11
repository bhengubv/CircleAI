// FoodTest.kt — verifies the CircleAI.Food port against the C# reference.

package com.bhengubv.circleai.food

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class FoodTest {

    @Test
    fun `recipes search meal logs and pantry`() {
        val b = InMemoryFoodBoard()
        b.addRecipe(Recipe("r1", "Omelette", listOf("Egg", "Cheese"), listOf("whisk", "cook"), 1, 10))
        b.addRecipe(Recipe("r2", "Toast", listOf("Bread"), listOf("toast"), 1, 3))
        assertEquals("Omelette", b.getRecipe("r1")!!.title)
        assertEquals(listOf("r1"), b.searchByIngredient("egg").map { it.recipeId }) // case-insensitive
        assertFailsWith<IllegalArgumentException> { b.searchByIngredient("  ") }

        val since = Instant.parse("2026-01-01T00:00:00Z")
        b.log(MealLog("l2", "u1", "r1", since.plusSeconds(200), 1))
        b.log(MealLog("l1", "u1", "r2", since.plusSeconds(100), 1))
        b.log(MealLog("lOld", "u1", "r2", since.minusSeconds(100), 1)) // excluded
        assertEquals(listOf("l1", "l2"), b.logsSince("u1", since).map { it.logId }) // ASC

        val bb = Instant.parse("2026-02-01T00:00:00Z")
        b.stockPantry(PantryItem("p1", "Milk", 2.0, "L", bb))
        b.stockPantry(PantryItem("p2", "Flour", 1.0, "kg", null))
        b.use("p1", 0.5)
        assertEquals(1.5, b.pantry().first { it.pantryItemId == "p1" }.quantity, 1e-9)
        b.use("p1", 5.0) // floored at 0 -> drops out of pantry() (quantity filter)
        assertTrue(b.pantry().none { it.pantryItemId == "p1" })
        assertEquals(listOf("p2"), b.pantry().map { it.pantryItemId }) // only p2 has quantity > 0
        // expiring() filters on best-before only (not quantity): p1 (dated) still surfaces.
        assertEquals(listOf("p1"), b.expiring(bb.plusSeconds(1)).map { it.pantryItemId })
        assertFailsWith<IllegalStateException> { b.use("nope", 1.0) }
    }

    @Test
    fun `expiring orders by best-before`() {
        val b = InMemoryFoodBoard()
        b.stockPantry(PantryItem("a", "A", 1.0, "u", Instant.parse("2026-03-10T00:00:00Z")))
        b.stockPantry(PantryItem("b", "B", 1.0, "u", Instant.parse("2026-03-05T00:00:00Z")))
        b.stockPantry(PantryItem("c", "C", 1.0, "u", null))
        val exp = b.expiring(Instant.parse("2026-03-31T00:00:00Z"))
        assertEquals(listOf("b", "a"), exp.map { it.pantryItemId }) // ASC by best-before, null excluded
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(FoodDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Food]"))
        assertTrue("Food_Safety_Act" in FoodDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = FoodCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Food]"))
        a.substituteIngredientAsync("butter", "vegan")
        assertTrue(fake.lastMessage!!.contains("Suggest 3 substitutes for butter"))
    }
}
