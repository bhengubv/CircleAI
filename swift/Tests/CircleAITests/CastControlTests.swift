import XCTest
@testable import CircleAI

/// The SOAP envelope, the clock formats and DIDL-Lite.
final class CastControlTests: XCTestCase {

    private let url = URL(string: "http://192.168.1.9:8200/media/1.mp4")!

    // MARK: - SOAP

    func testTheEnvelopeNamesTheActionAndTheService() {
        let e = UpnpAvTransport.envelope(action: "Play", innerXml: UpnpAvTransport.playBody)
        XCTAssertTrue(e.contains("<u:Play xmlns:u="))
        XCTAssertTrue(e.contains("urn:schemas-upnp-org:service:AVTransport:1"))
        XCTAssertTrue(e.contains("</u:Play>"))
        XCTAssertTrue(e.contains("<Speed>1</Speed>"))
    }

    // The header value is QUOTED - renderers reject it bare.
    func testTheSoapActionHeaderIsQuoted() {
        XCTAssertEqual(UpnpAvTransport.soapActionHeader("Play"),
                       "\u{22}urn:schemas-upnp-org:service:AVTransport:1#Play\u{22}")
    }

    // The metadata is XML inside an XML element, so it is escaped TWICE - once
    // by DIDL for the URL, once here. Miss this and the envelope is malformed.
    func testTheMetadataIsEscapedIntoTheEnvelope() {
        let body = UpnpAvTransport.setAvTransportUriBody(
            mediaUrl: url, didlMetadata: "<DIDL-Lite><item id=\u{22}0\u{22}/></DIDL-Lite>")
        XCTAssertTrue(body.contains("&lt;DIDL-Lite&gt;"))
        XCTAssertTrue(body.contains("&quot;0&quot;"))
        XCTAssertFalse(body.contains("<DIDL-Lite>"))
    }

    func testAUrlWithAQueryStringIsEscaped() {
        let tricky = URL(string: "http://10.0.0.5/v?a=1&b=2")!
        let body = UpnpAvTransport.setAvTransportUriBody(mediaUrl: tricky, didlMetadata: "")
        XCTAssertTrue(body.contains("a=1&amp;b=2"))
    }

    // MARK: - Clocks

    func testSeekTargetsAreZeroPaddedHoursMinutesSeconds() {
        XCTAssertEqual(UpnpAvTransport.formatClock(0), "00:00:00")
        XCTAssertEqual(UpnpAvTransport.formatClock(65), "00:01:05")
        XCTAssertEqual(UpnpAvTransport.formatClock(3661), "01:01:01")
        XCTAssertEqual(UpnpAvTransport.formatClock(36000), "10:00:00")
    }

    func testANegativeSeekClampsToZeroRatherThanPrintingNonsense() {
        XCTAssertEqual(UpnpAvTransport.formatClock(-5), "00:00:00")
    }

    func testTheSeekBodyUsesRelativeTime() {
        let b = UpnpAvTransport.seekBody(position: 90)
        XCTAssertTrue(b.contains("<Unit>REL_TIME</Unit>"))
        XCTAssertTrue(b.contains("<Target>00:01:30</Target>"))
    }

    // Renderers send several shapes, and NOT_IMPLEMENTED is a real answer.
    func testClocksAreParsedInEveryShapeRenderersSend() {
        XCTAssertEqual(UpnpAvTransport.parseClock("00:01:05"), 65)
        XCTAssertEqual(UpnpAvTransport.parseClock("1:01:01"), 3661)
        XCTAssertEqual(UpnpAvTransport.parseClock("00:00:10.500"), 10)
        XCTAssertEqual(UpnpAvTransport.parseClock("NOT_IMPLEMENTED"), 0)
        XCTAssertEqual(UpnpAvTransport.parseClock(nil), 0)
        XCTAssertEqual(UpnpAvTransport.parseClock("  "), 0)
        XCTAssertEqual(UpnpAvTransport.parseClock("99:99:99"), 0)
    }

    // MARK: - Reading responses

    func testTheTransportStateIsReadOutOfTheSoapReply() {
        let xml = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
        <u:GetTransportInfoResponse xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">
        <CurrentTransportState>PLAYING</CurrentTransportState>
        <CurrentTransportStatus>OK</CurrentTransportStatus>
        </u:GetTransportInfoResponse></s:Body></s:Envelope>
        """
        XCTAssertEqual(UpnpAvTransport.transportState(from: xml), "PLAYING")
        XCTAssertEqual(UpnpAvTransport.transportState(from: "<broken"), "UNKNOWN")
    }

    func testPositionAndDurationAreReadTogether() {
        let xml = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
        <u:GetPositionInfoResponse xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">
        <TrackDuration>00:03:20</TrackDuration>
        <RelTime>00:00:45</RelTime>
        </u:GetPositionInfoResponse></s:Body></s:Envelope>
        """
        let p = UpnpAvTransport.positionInfo(from: xml)
        XCTAssertEqual(p.position, 45)
        XCTAssertEqual(p.duration, 200)
    }

    func testAllTheStatesRenderersActuallyReport() {
        XCTAssertEqual(UpnpAvTransport.mapState("PLAYING"), .playing)
        XCTAssertEqual(UpnpAvTransport.mapState("PAUSED_PLAYBACK"), .paused)
        XCTAssertEqual(UpnpAvTransport.mapState("PAUSED"), .paused)
        XCTAssertEqual(UpnpAvTransport.mapState("STOPPED"), .stopped)
        XCTAssertEqual(UpnpAvTransport.mapState("TRANSITIONING"), .buffering)
        XCTAssertEqual(UpnpAvTransport.mapState("NO_MEDIA_PRESENT"), .idle)
        XCTAssertEqual(UpnpAvTransport.mapState("playing"), .playing)
        XCTAssertEqual(UpnpAvTransport.mapState("SOMETHING_NEW"), .unknown)
    }

    // MARK: - DIDL-Lite

    func testEachContentKindGetsItsUpnpClass() {
        func cls(_ k: CastContentKind) -> String {
            DidlLite.forMedia(CastMedia(source: .url(url), mimeType: "x", kind: k),
                              url: url, protocolInfo: "p")
        }
        XCTAssertTrue(cls(.video).contains("object.item.videoItem"))
        XCTAssertTrue(cls(.audio).contains("object.item.audioItem.musicTrack"))
        XCTAssertTrue(cls(.image).contains("object.item.imageItem.photo"))
        XCTAssertTrue(cls(.slideShow).contains("object.item.imageItem.photo"))
    }

    func testProtocolInfoIsTheHttpGetForm() {
        XCTAssertEqual(DidlLite.protocolInfo("video/mp4"), "http-get:*:video/mp4:*")
    }

    // A television name or track title with an ampersand in it must not break
    // the document it is embedded in.
    func testATitleWithMarkupInItIsEscaped() {
        let m = CastMedia.video(.url(url), title: "Tom & Jerry <1955>")
        let didl = DidlLite.forMedia(m, url: url, protocolInfo: DidlLite.protocolInfo("video/mp4"))
        XCTAssertTrue(didl.contains("<dc:title>Tom &amp; Jerry &lt;1955&gt;</dc:title>"))
    }

    func testAnUntitledItemStillGetsAName() {
        let didl = DidlLite.forMedia(CastMedia.video(.url(url)), url: url, protocolInfo: "p")
        XCTAssertTrue(didl.contains("<dc:title>CircleAI</dc:title>"))
    }

    func testTheResourceElementCarriesTheUrlAndProtocolInfo() {
        let didl = DidlLite.forMedia(CastMedia.video(.url(url)), url: url,
                                     protocolInfo: DidlLite.protocolInfo("video/mp4"))
        XCTAssertTrue(didl.contains("protocolInfo=\u{22}http-get:*:video/mp4:*\u{22}"))
        XCTAssertTrue(didl.contains(">http://192.168.1.9:8200/media/1.mp4</res>"))
    }
}
