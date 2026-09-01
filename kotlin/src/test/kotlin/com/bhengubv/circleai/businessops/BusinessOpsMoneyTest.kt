package com.bhengubv.circleai.businessops

import java.math.BigDecimal
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Money and currency formatting. */
class BusinessOpsMoneyTest {

    private fun zar(v: String) = Money.of(BigDecimal(v), "ZAR")!!

    @Test fun `a currency is required`() {
        assertNull(Money.of(BigDecimal.TEN, ""))
        assertNull(Money.of(BigDecimal.TEN, "   "))
    }

    @Test fun `the code is normalised so zar and ZAR are the same currency`() {
        assertEquals("ZAR", Money.of(BigDecimal.TEN, " zar ")!!.currency)
    }

    // The whole reason Money exists: rand and naira must never silently add.
    @Test fun `adding two currencies throws`() {
        assertFailsWith<MixedCurrencyException> { zar("100") + Money.of(100L, "NGN")!! }
    }

    @Test fun `same currency adds and subtracts`() {
        assertEquals(BigDecimal("150"), (zar("100") + zar("50")).amount)
        assertEquals(BigDecimal("50"), (zar("100") - zar("50")).amount)
    }

    @Test fun `multiplication scales the amount and keeps the currency`() {
        val m = zar("100") * BigDecimal("3")
        assertEquals(0, m.amount.compareTo(BigDecimal("300")))
        assertEquals("ZAR", m.currency)
    }

    // BigDecimal, not Double. On a Double this assertion fails.
    @Test fun `tenths add up exactly`() {
        var sum = zar("0")
        repeat(10) { sum += zar("0.1") }
        assertEquals(0, sum.amount.compareTo(BigDecimal.ONE))
    }

    // HALF UP, not bankers rounding: 2.5 cents is 3, not 2.
    @Test fun `rounding is half away from zero not bankers`() {
        assertEquals(BigDecimal("1.01"), zar("1.005").rounded().amount)
        assertEquals(BigDecimal("2.68"), zar("2.675").rounded().amount)
        assertEquals(BigDecimal("-1.01"), zar("-1.005").rounded().amount)
    }

    @Test fun `zero is zero`() {
        assertTrue(Money.zero("ZAR").isZero)
        assertEquals("ZAR", Money.zero("ZAR").currency)
    }

    @Test fun `known currencies get their symbol`() {
        assertEquals("R", Currencies.symbol("ZAR"))
        assertEquals("₦", Currencies.symbol("ngn"))
        assertEquals("$", Currencies.symbol("USD"))
    }

    // Never a blank - an amount with no currency marker is unreadable.
    @Test fun `an unknown currency prints its code rather than nothing`() {
        assertEquals("XYZ", Currencies.symbol("XYZ"))
        assertEquals("", Currencies.symbol(""))
    }

    // A locale-driven format would print R 1.234,56 on a German phone and turn
    // a thousand rand into one.
    @Test fun `formatting is invariant with space-separated thousands`() {
        assertEquals("R 1 234 567.50", Currencies.format(zar("1234567.5")))
        assertEquals("R 0.00", Currencies.format(zar("0")))
        assertEquals("R 9.40", Currencies.format(zar("9.4")))
        assertEquals("R 999.00", Currencies.format(zar("999")))
        assertEquals("R 1 000.00", Currencies.format(zar("1000")))
    }

    @Test fun `a negative amount keeps its sign in front of the digits`() {
        assertEquals("R -1 500.00", Currencies.format(zar("-1500")))
    }
}
