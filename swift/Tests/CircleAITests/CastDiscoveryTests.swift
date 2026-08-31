import XCTest
@testable import CircleAI

/// SSDP and the device description - everything between "is there a television
/// on this network" and "here is where to send commands".
final class CastDiscoveryTests: XCTestCase {

    // MARK: - The M-SEARCH datagram

    func testTheSearchRequestCarriesTheHeadersRenderersDemand() {
        let r = SsdpClient.searchRequest(target: SsdpClient.mediaRendererTarget, window: 3)
        XCTAssertTrue(r.hasPrefix("M-SEARCH * HTTP/1.1\r\n"))
        XCTAssertTrue(r.contains("HOST: 239.255.255.250:1900\r\n"))
        XCTAssertTrue(r.contains("MX: 3\r\n"))
        XCTAssertTrue(r.contains("ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n"))
        XCTAssertTrue(r.hasSuffix("\r\n\r\n"))
    }

    // MAN must be QUOTED. Renderers that see it unquoted simply do not answer.
    func testTheManHeaderIsQuoted() {
        let r = SsdpClient.searchRequest(target: "x", window: 3)
        XCTAssertTrue(r.contains("MAN: \u{22}ssdp:discover\u{22}\r\n"))
    }

    // MX outside 1...5 is out of spec and gets clamped, not passed through.
    func testMxIsClampedIntoTheLegalRange() {
        XCTAssertTrue(SsdpClient.searchRequest(target: "x", window: 0).contains("MX: 1\r\n"))
        XCTAssertTrue(SsdpClient.searchRequest(target: "x", window: 60).contains("MX: 5\r\n"))
        XCTAssertTrue(SsdpClient.searchRequest(target: "x", window: 2.9).contains("MX: 2\r\n"))
    }

    // MARK: - Parsing a response

    func testAWellFormedResponseIsParsed() {
        let raw = "HTTP/1.1 200 OK\r\n"
            + "CACHE-CONTROL: max-age=1800\r\n"
            + "LOCATION: http://192.168.1.50:8080/description.xml\r\n"
            + "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n"
            + "USN: uuid:abc-123::urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n"
        let r = SsdpClient.parseResponse(raw)
        XCTAssertEqual(r?.location.absoluteString, "http://192.168.1.50:8080/description.xml")
        XCTAssertEqual(r?.searchTarget, "urn:schemas-upnp-org:device:MediaRenderer:1")
        XCTAssertTrue(r!.uniqueServiceName.contains("uuid:abc-123"))
    }

    // Devices disagree about capitalisation, so header matching is folded.
    func testHeaderNamesAreCaseInsensitive() {
        let raw = "HTTP/1.1 200 OK\r\nlocation: http://10.0.0.5/d.xml\r\nst: x\r\nUsn: y\r\n\r\n"
        let r = SsdpClient.parseResponse(raw)
        XCTAssertEqual(r?.location.absoluteString, "http://10.0.0.5/d.xml")
        XCTAssertEqual(r?.searchTarget, "x")
    }

    func testANotifyOrGarbageIsNotAResponse() {
        XCTAssertNil(SsdpClient.parseResponse("NOTIFY * HTTP/1.1\r\nLOCATION: http://x/d.xml\r\n\r\n"))
        XCTAssertNil(SsdpClient.parseResponse("not http at all"))
        XCTAssertNil(SsdpClient.parseResponse(""))
    }

    // No LOCATION means nothing to fetch, so there is no device here.
    func testAResponseWithoutALocationIsDiscarded() {
        XCTAssertNil(SsdpClient.parseResponse("HTTP/1.1 200 OK\r\nST: x\r\nUSN: y\r\n\r\n"))
    }

    func testMissingStAndUsnBecomeEmptyRatherThanFailing() {
        let r = SsdpClient.parseResponse("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.5/d.xml\r\n\r\n")
        XCTAssertNotNil(r)
        XCTAssertEqual(r?.searchTarget, "")
        XCTAssertEqual(r?.uniqueServiceName, "")
    }
}
