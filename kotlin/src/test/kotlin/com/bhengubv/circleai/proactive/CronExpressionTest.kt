// CronExpressionTest.kt
//
// Verifies the 5-field cron parser against the C# reference semantics: field
// ranges, wildcards, lists, ranges, steps, day-of-month AND day-of-week
// matching, next-occurrence search, and the malformed-expression failures.

package com.bhengubv.circleai.proactive

import org.junit.jupiter.api.Test
import java.time.Instant
import java.time.ZoneOffset
import java.time.ZonedDateTime
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class CronExpressionTest {

    private fun utc(y: Int, mo: Int, d: Int, h: Int, mi: Int): Instant =
        ZonedDateTime.of(y, mo, d, h, mi, 0, 0, ZoneOffset.UTC).toInstant()

    @Test
    fun `every minute matches any moment`() {
        val c = CronExpression.parse("* * * * *")
        assertTrue(c.matches(utc(2026, 7, 8, 13, 37)))
    }

    @Test
    fun `specific minute+hour matches only that time of day`() {
        val c = CronExpression.parse("30 9 * * *")
        assertTrue(c.matches(utc(2026, 7, 8, 9, 30)))
        assertFalse(c.matches(utc(2026, 7, 8, 9, 31)))
        assertFalse(c.matches(utc(2026, 7, 8, 10, 30)))
    }

    @Test
    fun `step values expand correctly`() {
        val c = CronExpression.parse("*/15 * * * *")
        assertTrue(c.matches(utc(2026, 7, 8, 0, 0)))
        assertTrue(c.matches(utc(2026, 7, 8, 0, 15)))
        assertTrue(c.matches(utc(2026, 7, 8, 0, 30)))
        assertTrue(c.matches(utc(2026, 7, 8, 0, 45)))
        assertFalse(c.matches(utc(2026, 7, 8, 0, 10)))
    }

    @Test
    fun `lists and ranges expand correctly`() {
        val c = CronExpression.parse("0 9-17 * * 1,3,5") // hourly 9-17 on Mon/Wed/Fri
        // 2026-07-08 is a Wednesday.
        assertTrue(c.matches(utc(2026, 7, 8, 9, 0)))
        assertTrue(c.matches(utc(2026, 7, 8, 17, 0)))
        assertFalse(c.matches(utc(2026, 7, 8, 18, 0)))
        // 2026-07-07 is a Tuesday -> day-of-week excluded.
        assertFalse(c.matches(utc(2026, 7, 7, 9, 0)))
    }

    @Test
    fun `day-of-month and day-of-week both restricted requires both (AND)`() {
        // The 8th AND a Wednesday. 2026-07-08 is the 8th and a Wednesday -> match.
        val c = CronExpression.parse("0 0 8 * 3")
        assertTrue(c.matches(utc(2026, 7, 8, 0, 0)))
        // 2026-04-08 is the 8th but a Wednesday? 2026-04-08 is a Wednesday too; pick
        // a month where the 8th is NOT Wednesday: 2026-01-08 is a Thursday.
        assertFalse(c.matches(utc(2026, 1, 8, 0, 0)))
    }

    @Test
    fun `getNextOccurrence finds the next matching minute strictly after`() {
        val c = CronExpression.parse("30 9 * * *")
        val next = c.getNextOccurrence(utc(2026, 7, 8, 9, 0))
        assertEquals(utc(2026, 7, 8, 9, 30), next)
        // At exactly the match minute, the next is the following day (search starts +1 min).
        val after = c.getNextOccurrence(utc(2026, 7, 8, 9, 30))
        assertEquals(utc(2026, 7, 9, 9, 30), after)
    }

    @Test
    fun `malformed expressions throw`() {
        assertFailsWith<IllegalArgumentException> { CronExpression.parse("* * * *") }        // 4 fields
        assertFailsWith<IllegalArgumentException> { CronExpression.parse("60 * * * *") }     // minute out of range
        assertFailsWith<IllegalArgumentException> { CronExpression.parse("* 24 * * *") }     // hour out of range
        assertFailsWith<IllegalArgumentException> { CronExpression.parse("*/0 * * * *") }    // bad step
        assertFailsWith<IllegalArgumentException> { CronExpression.parse("5-1 * * * *") }    // inverted range
    }
}
