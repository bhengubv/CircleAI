import XCTest
@testable import CircleAI

/// Addresses and the null media host.
final class CastAddressTests: XCTestCase {

    private func bytes(_ s: String) -> [UInt8] {
        s.split(separator: ".").compactMap { UInt8($0) }
    }

    func testTheThreePrivateRangesAreRecognised() {
        XCTAssertTrue(LocalAddress.isPrivateV4(bytes("10.0.0.1")))
        XCTAssertTrue(LocalAddress.isPrivateV4(bytes("172.16.0.1")))
        XCTAssertTrue(LocalAddress.isPrivateV4(bytes("172.31.255.254")))
        XCTAssertTrue(LocalAddress.isPrivateV4(bytes("192.168.1.50")))
    }

    // 172.15 and 172.32 are OUTSIDE the /12 - the classic off-by-one here.
    func testTheEdgesOfTheOneSeventyTwoRangeAreRight() {
        XCTAssertFalse(LocalAddress.isPrivateV4(bytes("172.15.0.1")))
        XCTAssertFalse(LocalAddress.isPrivateV4(bytes("172.32.0.1")))
    }

    func testPublicAddressesAreNotPrivate() {
        XCTAssertFalse(LocalAddress.isPrivateV4(bytes("8.8.8.8")))
        XCTAssertFalse(LocalAddress.isPrivateV4(bytes("203.0.113.5")))
    }

    // APIPA means DHCP never answered; nothing on the LAN can reach it.
    func testLinkLocalAndLoopbackAreIdentified() {
        XCTAssertTrue(LocalAddress.isLinkLocalV4(bytes("169.254.1.1")))
        XCTAssertFalse(LocalAddress.isLinkLocalV4(bytes("169.253.1.1")))
        XCTAssertTrue(LocalAddress.isLoopbackV4(bytes("127.0.0.1")))
        XCTAssertFalse(LocalAddress.isLoopbackV4(bytes("10.0.0.1")))
    }

    // The two that look fine on the phone and are unreachable from the TV.
    func testOnlyARoutableLanAddressIsCastable() {
        XCTAssertTrue(LocalAddress.isCastable("192.168.1.50"))
        XCTAssertTrue(LocalAddress.isCastable("10.1.2.3"))
        XCTAssertFalse(LocalAddress.isCastable("127.0.0.1"))
        XCTAssertFalse(LocalAddress.isCastable("169.254.7.7"))
        XCTAssertFalse(LocalAddress.isCastable("8.8.8.8"))
        XCTAssertFalse(LocalAddress.isCastable("::1"))
        XCTAssertFalse(LocalAddress.isCastable("not-an-address"))
    }

    // A URL a television can already reach needs no host; local bytes do.
    func testTheNullMediaHostRefusesLocalMediaAndSaysWhy() async {
        do {
            _ = try await NullLocalMediaHost.instance.publish(.bytes(Data([1, 2, 3])),
                                                              mimeType: "image/jpeg")
            XCTFail("expected a refusal")
        } catch let e as CastError {
            XCTAssertEqual(e, .noMediaHost)
            XCTAssertTrue(e.description.contains("over the LAN"))
        } catch { XCTFail("wrong error") }
    }

    func testUnpublishingSomethingUnknownIsHarmless() async {
        await NullLocalMediaHost.instance.unpublish(URL(string: "http://x/y")!)
    }

    func testANonPositiveSlideIntervalFallsBackRatherThanRefusing() {
        XCTAssertEqual(CastDefaults.perImage(0), 8)
        XCTAssertEqual(CastDefaults.perImage(-3), 8)
        XCTAssertEqual(CastDefaults.perImage(2), 2)
    }

    func testTheHelpersBuildTheKindTheyName() {
        let url = URL(string: "http://x/y.mp4")!
        XCTAssertEqual(CastMedia.video(.url(url)).kind, .video)
        XCTAssertEqual(CastMedia.video(.url(url)).mimeType, "video/mp4")
        XCTAssertEqual(CastMedia.audio(.url(url)).mimeType, "audio/mpeg")
        XCTAssertEqual(CastMedia.image(.url(url)).mimeType, "image/jpeg")
        XCTAssertEqual(CastMedia.image(.url(url)).kind, .image)
    }
}
