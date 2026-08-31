import XCTest
@testable import CircleAI

/// Addresses, CIDRs and blocklist lines - everything the index is built from.
final class SecurityDefenseParsingTests: XCTestCase {

    // MARK: - Addresses

    func testDottedQuadParsesToItsHostOrderValue() {
        let ip = IPAddressValue("192.168.1.1")
        XCTAssertEqual(ip?.family, .iPv4)
        XCTAssertEqual(ip?.v4, 0xC0A80101)
    }

    func testAHostnameIsNotAnAddress() {
        XCTAssertNil(IPAddressValue("evil.example.com"))
        XCTAssertNil(IPAddressValue("999.1.1.1"))
        XCTAssertNil(IPAddressValue(""))
    }

    func testIPv6IsLowercasedSoMatchingIsStable() {
        XCTAssertEqual(IPAddressValue("2001:DB8::1")?.text, "2001:db8::1")
        XCTAssertEqual(IPAddressValue("2001:DB8::1")?.family, .iPv6)
    }

    func testRoundTripThroughTheHostOrderValue() {
        XCTAssertEqual(IPAddressValue.iPv4(0xC0A80101).text, "192.168.1.1")
        XCTAssertEqual(IPAddressValue.iPv4(0).text, "0.0.0.0")
        XCTAssertEqual(IPAddressValue.iPv4(0xFFFFFFFF).text, "255.255.255.255")
    }

    // MARK: - CIDR

    func testCidrMasksTheHostBitsAway() {
        let c = Ipv4Cidr("10.1.2.3/24")
        XCTAssertEqual(c?.description, "10.1.2.0/24")
        XCTAssertEqual(c?.prefixLength, 24)
    }

    func testContainmentIsInclusiveOfTheWholeRange() throws {
        let c = try XCTUnwrap(Ipv4Cidr("10.0.0.0/8"))
        XCTAssertTrue(c.contains(try XCTUnwrap(IPAddressValue("10.0.0.0"))))
        XCTAssertTrue(c.contains(try XCTUnwrap(IPAddressValue("10.255.255.255"))))
        XCTAssertFalse(c.contains(try XCTUnwrap(IPAddressValue("11.0.0.1"))))
    }

    // A /0 is the whole internet, and the shift that builds its mask is the one
    // that is undefined if written naively.
    func testSlashZeroMatchesEverythingWithoutOverflowing() throws {
        let c = try XCTUnwrap(Ipv4Cidr("0.0.0.0/0"))
        XCTAssertEqual(c.mask, 0)
        XCTAssertTrue(c.contains(try XCTUnwrap(IPAddressValue("8.8.8.8"))))
    }

    func testABareAddressIsASlash32() throws {
        let c = try XCTUnwrap(Ipv4Cidr("1.2.3.4"))
        XCTAssertEqual(c.prefixLength, 32)
        XCTAssertTrue(c.contains(try XCTUnwrap(IPAddressValue("1.2.3.4"))))
        XCTAssertFalse(c.contains(try XCTUnwrap(IPAddressValue("1.2.3.5"))))
    }

    func testAnOutOfRangePrefixIsRefused() {
        XCTAssertNil(Ipv4Cidr("10.0.0.0/33"))
        XCTAssertNil(Ipv4Cidr("10.0.0.0/-1"))
        XCTAssertNil(Ipv4Cidr("not-an-ip/24"))
    }

    func testAnIPv6CidrIsNotAnIpv4Cidr() {
        XCTAssertNil(Ipv4Cidr("2001:db8::/32"))
    }

    // MARK: - Blocklist lines

    func testAHostsFileLineDropsTheSinkholeAddress() {
        let i = BlocklistParser.parseLine("0.0.0.0 ads.example.com")
        XCTAssertEqual(i?.kind, .domain)
        XCTAssertEqual(i?.value, "ads.example.com")
    }

    func testTheOtherSinkholeFormIsHandledToo() {
        XCTAssertEqual(BlocklistParser.parseLine("127.0.0.1  tracker.example.net")?.value,
                       "tracker.example.net")
    }

    func testACommentLineIsSkipped() {
        XCTAssertNil(BlocklistParser.parseLine("# this is a comment"))
        XCTAssertNil(BlocklistParser.parseLine("   "))
        XCTAssertNil(BlocklistParser.parseLine(""))
    }

    func testATrailingCommentIsStrippedNotTreatedAsPartOfTheHost() {
        XCTAssertEqual(BlocklistParser.parseLine("evil.example.com # known c2")?.value,
                       "evil.example.com")
    }

    func testAPlainAddressLineIsAnIpv4Indicator() {
        let i = BlocklistParser.parseLine("203.0.113.5")
        XCTAssertEqual(i?.kind, .ipv4)
    }

    func testACidrLineIsRecognisedAsARange() {
        XCTAssertEqual(BlocklistParser.parseLine("203.0.113.0/24")?.kind, .ipv4Cidr)
    }

    // A bare word would otherwise become a domain that matches nothing useful
    // and confuses every later lookup.
    func testABareWordIsNotADomain() {
        XCTAssertNil(BlocklistParser.classify("localhost"))
        XCTAssertNil(BlocklistParser.classify("banana"))
    }

    func testADomainIsLowercasedAndLosesItsRootDot() {
        XCTAssertEqual(BlocklistParser.classify("EVIL.Example.COM.")?.value, "evil.example.com")
    }

    func testParsingAWholeListSkipsTheJunk() {
        let list = """
        # header
        0.0.0.0 a.example.com
        203.0.113.0/24

        not_a_domain
        b.example.com  # trailing
        """
        let parsed = BlocklistParser.parse(list)
        XCTAssertEqual(parsed.count, 3)
        XCTAssertEqual(parsed.map(\.value), ["a.example.com", "203.0.113.0/24", "b.example.com"])
    }
}
