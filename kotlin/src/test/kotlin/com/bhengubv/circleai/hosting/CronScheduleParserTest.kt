// CronScheduleParserTest.kt
//
// Verifies the Hosting 5-field CronScheduleParser against the C# reference:
// wildcards, fixed values, lists, steps, ranges, day-of-week (0=Sunday) AND
// day-of-month semantics, month/hour advancement, strictly-after search, and
// malformed-expression failures.

package com.bhengubv.circleai.hosting

import org.junit.jupiter.api.Test
import java.time.Instant
import java.time.ZoneOffset
import java.time.ZonedDateTime
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class CronScheduleParserTest {

    private fun utc(y: Int, mo: Int, d: Int, h: Int, mi: Int): Instant =
        ZonedDateTime.of(y, mo, d, h, mi, 0, 0, ZoneOffset.UTC).toInstant()

    @Test
    fun `every-minute yields the next minute`() {
        val next = CronScheduleParser.getNextOccurrence("* * * * *", utc(2026, 7, 8, 13, 37))
        assertEquals(utc(2026, 7, 8, 13, 38), next)
    }

    @Test
    fun `fixed minute+hour yields that time`() {
        val next = CronScheduleParser.getNextOccurrence("30 9 * * *", utc(2026, 7, 8, 9, 0))
        assertEquals(utc(2026, 7, 8, 9, 30), next)
        // At the match minute, search starts +1 minute -> next day.
        val after = CronScheduleParser.getNextOccurrence("30 9 * * *", utc(2026, 7, 8, 9, 30))
        assertEquals(utc(2026, 7, 9, 9, 30), after)
    }

    @Test
    fun `step values`() {
        // */15 at 12:07 -> next is 12:15.
        val next = CronScheduleParser.getNextOccurrence("*/15 * * * *", utc(2026, 7, 8, 12, 7))
        assertEquals(utc(2026, 7, 8, 12, 15), next)
    }

    @Test
    fun `ranges and lists on day-of-week`() {
        // hourly 9-17 on Mon/Wed/Fri. 2026-07-08 is a Wednesday.
        val expr = "0 9-17 * * 1,3,5"
        val next = CronScheduleParser.getNextOccurrence(expr, utc(2026, 7, 8, 8, 30))
        assertEquals(utc(2026, 7, 8, 9, 0), next)
        // From 17:30 Wed, next valid is 09:00 Fri (2026-07-10).
        val nextDay = CronScheduleParser.getNextOccurrence(expr, utc(2026, 7, 8, 17, 30))
        assertEquals(utc(2026, 7, 10, 9, 0), nextDay)
    }

    @Test
    fun `day-of-month advancement across a month`() {
        // 09:00 on the 1st of every month.
        val next = CronScheduleParser.getNextOccurrence("0 9 1 * *", utc(2026, 7, 8, 10, 0))
        assertEquals(utc(2026, 8, 1, 9, 0), next)
    }

    @Test
    fun `sunday is day-of-week 0`() {
        // 2026-07-12 is a Sunday. "0 0 * * 0" -> midnight on Sundays.
        val next = CronScheduleParser.getNextOccurrence("0 0 * * 0", utc(2026, 7, 8, 0, 0))
        assertEquals(utc(2026, 7, 12, 0, 0), next)
    }

    @Test
    fun `month field restricts to matching months`() {
        // 00:00 on Jan 1 only.
        val next = CronScheduleParser.getNextOccurrence("0 0 1 1 *", utc(2026, 7, 8, 0, 0))
        assertEquals(utc(2027, 1, 1, 0, 0), next)
    }

    @Test
    fun `malformed expressions throw`() {
        assertFailsWith<IllegalArgumentException> { CronScheduleParser.getNextOccurrence("* * * *", Instant.now()) }
        assertFailsWith<IllegalArgumentException> { CronScheduleParser.getNextOccurrence("60 * * * *", Instant.now()) }
        assertFailsWith<IllegalArgumentException> { CronScheduleParser.getNextOccurrence("*/0 * * * *", Instant.now()) }
        assertFailsWith<IllegalArgumentException> { CronScheduleParser.getNextOccurrence("5-1 * * * *", Instant.now()) }
        assertFailsWith<IllegalArgumentException> { CronScheduleParser.getNextOccurrence("   ", Instant.now()) }
    }
}
