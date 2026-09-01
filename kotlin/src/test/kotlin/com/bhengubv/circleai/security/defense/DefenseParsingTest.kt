package com.bhengubv.circleai.security.defense

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Addresses, CIDRs and blocklist lines - everything the index is built from. */
class DefenseParsingTest {

    @Test fun `a dotted quad parses to its host-order value`() {
        val ip = IpAddressValue.parse("192.168.1.1")
        assertTrue(ip!!.isIpv4)
        assertEquals(0xC0A80101L, ip.v4)
    }

    @Test fun `a hostname is not an address`() {
        assertNull(IpAddressValue.parse("evil.example.com"))
        assertNull(IpAddressValue.parse("999.1.1.1"))
        assertNull(IpAddressValue.parse(""))
    }

    @Test fun `ipv6 is lowercased so matching is stable`() {
        assertEquals("2001:db8::1", IpAddressValue.parse("2001:DB8::1")!!.text)
        assertFalse(IpAddressValue.parse("2001:DB8::1")!!.isIpv4)
    }

    @Test fun `round trip through the host-order value`() {
        assertEquals("192.168.1.1", IpAddressValue.ofIpv4(0xC0A80101L).text)
        assertEquals("0.0.0.0", IpAddressValue.ofIpv4(0L).text)
        assertEquals("255.255.255.255", IpAddressValue.ofIpv4(0xFFFFFFFFL).text)
    }

    @Test fun `a cidr masks the host bits away`() {
        val c = Ipv4Cidr.parse("10.1.2.3/24")
        assertEquals("10.1.2.0/24", c.toString())
        assertEquals(24, c!!.prefixLength)
    }

    @Test fun `containment is inclusive of the whole range`() {
        val c = Ipv4Cidr.parse("10.0.0.0/8")!!
        assertTrue(c.contains(IpAddressValue.parse("10.0.0.0")!!))
        assertTrue(c.contains(IpAddressValue.parse("10.255.255.255")!!))
        assertFalse(c.contains(IpAddressValue.parse("11.0.0.1")!!))
    }

    // A /0 is the whole internet, and the shift that builds its mask is the one
    // that is undefined if written naively.
    @Test fun `slash zero matches everything without overflowing`() {
        val c = Ipv4Cidr.parse("0.0.0.0/0")!!
        assertEquals(0L, c.mask)
        assertTrue(c.contains(IpAddressValue.parse("8.8.8.8")!!))
    }

    @Test fun `a bare address is a slash thirty-two`() {
        val c = Ipv4Cidr.parse("1.2.3.4")!!
        assertEquals(32, c.prefixLength)
        assertTrue(c.contains(IpAddressValue.parse("1.2.3.4")!!))
        assertFalse(c.contains(IpAddressValue.parse("1.2.3.5")!!))
    }

    @Test fun `an out of range prefix is refused`() {
        assertNull(Ipv4Cidr.parse("10.0.0.0/33"))
        assertNull(Ipv4Cidr.parse("not-an-ip/24"))
        assertNull(Ipv4Cidr.parse("2001:db8::/32"))
    }

    @Test fun `a hosts-file line drops the sinkhole address`() {
        val i = BlocklistParser.parseLine("0.0.0.0 ads.example.com")
        assertEquals(IndicatorKind.DOMAIN, i!!.kind)
        assertEquals("ads.example.com", i.value)
    }

    @Test fun `the other sinkhole form is handled too`() {
        assertEquals("tracker.example.net",
            BlocklistParser.parseLine("127.0.0.1  tracker.example.net")!!.value)
    }

    @Test fun `a comment line is skipped`() {
        assertNull(BlocklistParser.parseLine("# this is a comment"))
        assertNull(BlocklistParser.parseLine("   "))
        assertNull(BlocklistParser.parseLine(""))
    }

    @Test fun `a trailing comment is stripped not treated as part of the host`() {
        assertEquals("evil.example.com",
            BlocklistParser.parseLine("evil.example.com # known c2")!!.value)
    }

    @Test fun `a plain address line is an ipv4 indicator`() {
        assertEquals(IndicatorKind.IPV4, BlocklistParser.parseLine("203.0.113.5")!!.kind)
    }

    @Test fun `a cidr line is recognised as a range`() {
        assertEquals(IndicatorKind.IPV4_CIDR, BlocklistParser.parseLine("203.0.113.0/24")!!.kind)
    }

    // A bare word would otherwise become a domain that matches nothing useful
    // and confuses every later lookup.
    @Test fun `a bare word is not a domain`() {
        assertNull(BlocklistParser.classify("localhost"))
        assertNull(BlocklistParser.classify("banana"))
    }

    @Test fun `a domain is lowercased and loses its root dot`() {
        assertEquals("evil.example.com", BlocklistParser.classify("EVIL.Example.COM.")!!.value)
    }

    @Test fun `parsing a whole list skips the junk`() {
        val list = listOf(
            "# header",
            "0.0.0.0 a.example.com",
            "203.0.113.0/24",
            "",
            "not_a_domain",
            "b.example.com  # trailing",
        ).joinToString("\n")
        val parsed = BlocklistParser.parse(list)
        assertEquals(3, parsed.size)
        assertEquals(listOf("a.example.com", "203.0.113.0/24", "b.example.com"), parsed.map { it.value })
    }
}
