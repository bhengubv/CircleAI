package com.bhengubv.circleai.security.defense

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/** The indicator index. */
class DefenseIndexTest {

    private fun source(list: String) = BlocklistIndicatorSource().apply { refresh(list, replace = true) }
    private fun ip(s: String) = IpAddressValue.parse(s)!!

    @Test fun `an exact address matches`() {
        val m = source("203.0.113.5").match(ip("203.0.113.5"), null)
        assertEquals(IndicatorKind.IPV4, m!!.kind)
        assertEquals("known-bad-ip", m.reason)
    }

    @Test fun `an address inside a blocked range matches the range`() {
        val m = source("203.0.113.0/24").match(ip("203.0.113.77"), null)
        assertEquals(IndicatorKind.IPV4_CIDR, m!!.kind)
        assertEquals("203.0.113.0/24", m.indicator)
    }

    @Test fun `an address outside everything does not match`() {
        assertNull(source("203.0.113.0/24").match(ip("8.8.8.8"), null))
    }

    // Blocking evil.com has to block every subdomain, or blocklists are
    // trivially defeated by prefixing a random label.
    @Test fun `a subdomain matches its blocked parent`() {
        val m = source("evil.example.com").match(null, "cdn.assets.evil.example.com")
        assertEquals("evil.example.com", m!!.indicator)
        assertEquals("known-bad-parent-domain", m.reason)
    }

    // And the reverse must NOT hold.
    @Test fun `a parent is not blocked by its child`() {
        assertNull(source("bad.example.com").match(null, "example.com"))
    }

    @Test fun `a similarly named sibling is not a match`() {
        assertNull(source("evil.com").match(null, "notevil.com"))
    }

    @Test fun `host matching ignores case and the root dot`() {
        assertNotNull(source("evil.example.com").match(null, "EVIL.Example.COM."))
    }

    @Test fun `ipv6 matches on its canonical form`() {
        assertEquals(IndicatorKind.IPV6, source("2001:DB8::99").match(ip("2001:db8::99"), null)!!.kind)
    }

    @Test fun `refresh replaces by default and appends when asked`() {
        val s = BlocklistIndicatorSource()
        s.refresh("a.example.com", replace = true)
        s.refresh("b.example.com", replace = true)
        assertNull(s.match(null, "a.example.com"))

        s.refresh("c.example.com", replace = false)
        assertNotNull(s.match(null, "b.example.com"))
        assertNotNull(s.match(null, "c.example.com"))
    }

    @Test fun `the count reflects what was indexed`() {
        val lines = listOf("a.example.com", "b.example.com", "203.0.113.1", "203.0.113.0/24")
        assertEquals(4, source(lines.joinToString("\n")).indicatorCount)
    }
}
