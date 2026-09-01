import XCTest
@testable import CircleAI

// MARK: - The parts that need no socket

final class MediaHostHttpTests: XCTestCase {

    private func range(_ h: String, _ len: Int64) -> (start: Int64, end: Int64)? {
        MediaHostHttp.parseRange(h, length: len)
    }

    func testTheFirstAndLastByteFormIsInclusiveAtBothEnds() {
        // bytes=0-499 is 500 bytes, not 499. Off by one here and the last byte
        // of every chunk is missing, which a decoder reports as a corrupt file.
        XCTAssertEqual(0, range("bytes=0-499", 1000)?.start)
        XCTAssertEqual(499, range("bytes=0-499", 1000)?.end)
        XCTAssertEqual(500, range("bytes=500-999", 1000)?.start)
    }

    func testAnOpenEndedRangeRunsToTheEndOfTheFile() {
        XCTAssertEqual(500, range("bytes=500-", 1000)?.start)
        XCTAssertEqual(999, range("bytes=500-", 1000)?.end)
    }

    func testTheSuffixFormIsTheLastNBytesNotTheFirst() {
        // Read as a start offset it serves the wrong part of the file, and the
        // picture is silently wrong rather than absent.
        XCTAssertEqual(500, range("bytes=-500", 1000)?.start)
        XCTAssertEqual(999, range("bytes=-500", 1000)?.end)
        // Asking for more than exists is the whole file, not a refusal.
        XCTAssertEqual(0, range("bytes=-5000", 1000)?.start)
    }

    func testAnEndPastTheFileIsClampedRatherThanRefused() {
        // Renderers over-ask routinely.
        XCTAssertEqual(900, range("bytes=900-99999", 1000)?.start)
        XCTAssertEqual(999, range("bytes=900-99999", 1000)?.end)
    }

    func testOnlyTheFirstRangeIsHonoured() {
        XCTAssertEqual(0, range("bytes=0-99,200-299", 1000)?.start)
        XCTAssertEqual(99, range("bytes=0-99,200-299", 1000)?.end)
    }

    func testTheHeaderNameIsCaseInsensitiveAndSpacesAreTolerated() {
        XCTAssertEqual(0, range("BYTES=0-99", 1000)?.start)
        XCTAssertEqual(99, range("bytes= 0 - 99 ", 1000)?.end)
    }

    func testAHeaderThisCannotHonourIsNilSoTheWholeFileIsSentInstead() {
        // Nil means "no partial content", which is always a valid answer.
        XCTAssertNil(range("items=0-99", 1000))
        XCTAssertNil(range("bytes=abc", 1000))
        XCTAssertNil(range("bytes=500", 1000))
        XCTAssertNil(range("bytes=900-100", 1000))
        XCTAssertNil(range("bytes=-0", 1000))
        XCTAssertNil(range("", 1000))
        XCTAssertNil(range("bytes=5000-6000", 1000))
    }

    func testTheExtensionFollowsTheMimeTypeBecauseSomeRenderersReadTheUrl() {
        // Not cosmetic: several televisions decide how to handle a URL by its
        // extension and ignore Content-Type entirely.
        XCTAssertEqual(".mp4", MediaHostHttp.extensionFor(mimeType: "video/mp4"))
        XCTAssertEqual(".jpg", MediaHostHttp.extensionFor(mimeType: "image/jpeg"))
        XCTAssertEqual(".png", MediaHostHttp.extensionFor(mimeType: "IMAGE/PNG"))
        XCTAssertEqual(".png", MediaHostHttp.extensionFor(mimeType: "image/apng"))
        XCTAssertEqual(".bin", MediaHostHttp.extensionFor(mimeType: "application/x-made-up"))
    }

    func testTheResponseHeadCarriesTheDlnaHeadersThatDecideBehaviour() {
        // Without transferMode some sets download a whole video before showing
        // anything, and some refuse an image outright.
        let image = MediaHostHttp.responseHead(mimeType: "image/jpeg", totalLength: 10, range: nil)
        XCTAssertTrue(image.contains("transferMode.dlna.org: Interactive"))
        XCTAssertTrue(image.contains("Accept-Ranges: bytes"))
        XCTAssertTrue(image.hasPrefix("HTTP/1.1 200 OK"))

        let video = MediaHostHttp.responseHead(mimeType: "video/mp4", totalLength: 10, range: nil)
        XCTAssertTrue(video.contains("transferMode.dlna.org: Streaming"))
        XCTAssertTrue(video.contains("DLNA.ORG_OP=01"))
    }

    func testAPartialResponseIs206WithAContentRange() {
        let head = MediaHostHttp.responseHead(
            mimeType: "video/mp4", totalLength: 1000, range: (100, 199))
        XCTAssertTrue(head.hasPrefix("HTTP/1.1 206 Partial Content"))
        XCTAssertTrue(head.contains("Content-Range: bytes 100-199/1000"))
        XCTAssertTrue(head.contains("Content-Length: 100"))
    }

    func testTheRequestLineAndRangeHeaderAreParsed() {
        let raw = "GET /abc.mp4?x=1 HTTP/1.1\r\nHost: h\r\nRange: bytes=0-99\r\n\r\n"
        let r = MediaHostHttp.parseRequest(raw)
        XCTAssertEqual("GET", r?.method)
        // The query string is dropped: it is not part of what was published.
        XCTAssertEqual("/abc.mp4", r?.path)
        XCTAssertEqual("bytes=0-99", r?.range)

        XCTAssertNil(MediaHostHttp.parseRequest("garbage")?.range)
        XCTAssertNil(MediaHostHttp.parseRequest(""))
    }
}

// MARK: - The session, over a fake transport

/// Records every SOAP action and hands back canned XML.
private final class FakeSoap: @unchecked Sendable {
    private let lock = NSLock()
    private(set) var actions: [String] = []
    private(set) var bodies: [String] = []
    private(set) var urls: [URL] = []
    var transportState = "PLAYING"
    var relTime = "00:01:30"
    var duration = "00:05:00"
    var failOn: String?

    var transport: SoapTransport {
        { [self] url, action, body in
            lock.lock()
            urls.append(url); actions.append(action); bodies.append(body)
            let fail = failOn
            let state = transportState, rel = relTime, dur = duration
            lock.unlock()

            if let fail, action.contains(fail) { throw CastError.general("renderer said no") }
            if action.contains("GetTransportInfo") {
                return "<r><CurrentTransportState>\(state)</CurrentTransportState></r>"
            }
            if action.contains("GetPositionInfo") {
                return "<r><RelTime>\(rel)</RelTime><TrackDuration>\(dur)</TrackDuration></r>"
            }
            return "<r/>"
        }
    }
}

private final class FakeHost: ILocalMediaHost, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var published: [URL] = []
    private(set) var unpublished: [URL] = []

    var backendId: String { "fake" }

    func publish(_ source: CastMediaSource, mimeType: String) async throws -> URL {
        lock.lock(); defer { lock.unlock() }
        let url = URL(string: "http://192.168.1.5:9000/\(published.count).bin")!
        published.append(url)
        return url
    }

    func unpublish(_ url: URL) async {
        lock.lock(); unpublished.append(url); lock.unlock()
    }
}

final class DlnaCastSessionTests: XCTestCase {

    private func description(
        udn: String = "uuid:tv-1",
        control: String = "http://192.168.1.10:8080/AVTransport/control"
    ) -> RendererDescription {
        RendererDescription(
            udn: udn,
            friendlyName: "Living room TV",
            manufacturer: "Acme",
            modelName: "A1",
            location: URL(string: "http://192.168.1.10:8080/desc.xml")!,
            avTransportControlUrl: URL(string: control)!,
            iconUrl: URL(string: "http://192.168.1.10:8080/icon.png"))
    }

    private func session(_ soap: FakeSoap, host: FakeHost? = nil) -> DlnaCastSession {
        let d = description()
        let target = DescribedCastTarget(d)
        return DlnaCastSession(
            target: target,
            control: UpnpControlPoint(controlUrl: d.avTransportControlUrl, transport: soap.transport),
            host: host)
    }

    func testEveryActionCarriesAQuotedSoapActionHeader() async throws {
        // Unquoted, a renderer answers 401 or simply ignores the request, and
        // nothing about the symptom points at a pair of quotation marks.
        let soap = FakeSoap()
        let s = session(soap)
        try await s.play()
        try await s.pause()
        try await s.stop()
        XCTAssertTrue(soap.actions.allSatisfy { $0.hasPrefix("\"") && $0.hasSuffix("\"") })
        XCTAssertTrue(soap.actions[0].contains("AVTransport:1#Play"))
    }

    func testSeekIsSentAsAClockNotANumberOfSeconds() async throws {
        let soap = FakeSoap()
        try await session(soap).seek(to: 90)
        XCTAssertTrue(soap.bodies[0].contains("00:01:30"))
    }

    func testAUrlSourceNeedsNoMediaHostAtAll() async throws {
        let soap = FakeSoap()
        try await session(soap).load(CastMedia(
            source: .url(URL(string: "http://cdn/x.mp4")!),
            mimeType: "video/mp4", kind: .video))
        XCTAssertTrue(soap.bodies[0].contains("http://cdn/x.mp4"))
    }

    func testByteMediaIsPublishedFirstBecauseARendererPulls() async throws {
        // There is no push in DLNA. The renderer is handed a URL and fetches it,
        // so bytes have to become a URL before anything is sent.
        let soap = FakeSoap()
        let host = FakeHost()
        try await session(soap, host: host).load(CastMedia(
            source: .bytes(Data(count: 10)), mimeType: "image/png", kind: .image))
        XCTAssertEqual(1, host.published.count)
        XCTAssertTrue(soap.bodies[0].contains(host.published[0].absoluteString))
    }

    func testByteMediaWithNoHostIsRefusedRatherThanSentAsNothing() async throws {
        let s = session(FakeSoap(), host: nil)
        do {
            try await s.load(CastMedia(source: .bytes(Data(count: 4)),
                                       mimeType: "image/png", kind: .image))
            XCTFail("byte media was accepted with no media host")
        } catch {
            XCTAssertEqual(CastError.noMediaHost, error as? CastError)
        }
    }

    func testStatusMapsTheRendererStateOntoOurs() async throws {
        let soap = FakeSoap()
        soap.transportState = "TRANSITIONING"
        var status = try await session(soap).status()
        XCTAssertEqual(.buffering, status.state)

        soap.transportState = "NO_MEDIA_PRESENT"
        status = try await session(soap).status()
        XCTAssertEqual(.idle, status.state)

        soap.transportState = "SOMETHING_NEW"
        status = try await session(soap).status()
        XCTAssertEqual(.unknown, status.state)
    }

    func testStatusReportsWhatIsCurrentlyLoaded() async throws {
        let soap = FakeSoap()
        let s = session(soap)
        var status = try await s.status()
        XCTAssertNil(status.currentUri)

        try await s.load(CastMedia(source: .url(URL(string: "http://cdn/x.mp4")!),
                                   mimeType: "video/mp4", kind: .video))
        status = try await s.status()
        XCTAssertEqual("http://cdn/x.mp4", status.currentUri)
    }

    func testASlideshowIsSetAvTransportUriInALoop() async throws {
        // There is no DLNA slideshow action; a deck is cast one image at a time.
        let soap = FakeSoap()
        let images = (1...3).map {
            CastMedia(source: .url(URL(string: "http://cdn/\($0).png")!),
                      mimeType: "image/png", kind: .image)
        }
        try await session(soap).showSlideShow(images, perImage: 0.001)
        XCTAssertEqual(3, soap.actions.filter { $0.contains("SetAVTransportURI") }.count)
        XCTAssertEqual(3, soap.actions.filter { $0.contains("#Play") }.count)
    }

    func testANonPositiveIntervalFallsBackToTheDefault() {
        // Otherwise it advances instantly and shows nothing.
        XCTAssertEqual(CastDefaults.slideShowPerImage, CastDefaults.perImage(0))
        XCTAssertEqual(CastDefaults.slideShowPerImage, CastDefaults.perImage(-4))
        XCTAssertEqual(2, CastDefaults.perImage(2))
    }

    func testCloseUnpublishesWhatItPublishedAndLeavesTheHostAlone() async throws {
        // The host is shared per bind address and owned by the engine. Closing
        // it here would take down every other session on the same interface.
        let host = FakeHost()
        let s = session(FakeSoap(), host: host)
        let media = CastMedia(source: .bytes(Data(count: 4)), mimeType: "image/png", kind: .image)
        try await s.load(media)
        try await s.load(media)
        await s.close()
        XCTAssertEqual(2, host.unpublished.count)
    }

    func testCloseDoesNotUnpublishAUrlItNeverPublished() async throws {
        let host = FakeHost()
        let s = session(FakeSoap(), host: host)
        try await s.load(CastMedia(source: .url(URL(string: "http://cdn/x.mp4")!),
                                   mimeType: "video/mp4", kind: .video))
        await s.close()
        XCTAssertTrue(host.unpublished.isEmpty)
    }
}

// MARK: - Discovery and the engine

final class DlnaCastDiscoveryTests: XCTestCase {

    private let descXml = """
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <UDN>uuid:tv-1</UDN>
            <friendlyName>Living room TV</friendlyName>
            <manufacturer>Acme</manufacturer>
            <modelName>A1</modelName>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                <controlURL>/AVTransport/control</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """

    private func response(_ location: String) -> SsdpResponse {
        SsdpResponse(location: URL(string: location)!,
                     searchTarget: "urn:schemas-upnp-org:device:MediaRenderer:1",
                     uniqueServiceName: "uuid:tv-1::x")
    }

    private func collect(_ stream: AsyncStream<any ICastTarget>) async -> [any ICastTarget] {
        var out: [any ICastTarget] = []
        for await t in stream { out.append(t) }
        return out
    }

    func testOneRendererBecomesOneTarget() async {
        let soap = FakeSoap()
        let xml = descXml
        let d = DlnaCastDiscovery(
            search: { _ in [self.response("http://192.168.1.10:8080/desc.xml")] },
            fetchDescription: { _ in xml },
            hostForTarget: { _ in nil },
            transport: soap.transport)
        let found = await collect(d.discover(searchWindow: 2))
        XCTAssertEqual(1, found.count)
        XCTAssertEqual("Living room TV", found.first?.friendlyName)
    }

    func testTheSameRendererAnsweringSeveralTimesIsListedOnce() async {
        // Answering an M-SEARCH repeatedly is the protocol, not a fault.
        // Emitting each answer puts one television in the list four times.
        let soap = FakeSoap()
        let xml = descXml
        let loc = "http://192.168.1.10:8080/desc.xml"
        let d = DlnaCastDiscovery(
            search: { _ in (0..<4).map { _ in self.response(loc) } },
            fetchDescription: { _ in xml },
            hostForTarget: { _ in nil },
            transport: soap.transport)
        let found = await collect(d.discover(searchWindow: 2))
        XCTAssertEqual(1, found.count)
    }

    func testOneUnreachableDeviceDoesNotEndTheScan() async {
        // A television turned off mid-scan must not hide the ones that are on.
        let soap = FakeSoap()
        let xml = descXml
        let bad = "http://192.168.1.99:8080/desc.xml"
        let good = "http://192.168.1.10:8080/desc.xml"
        let d = DlnaCastDiscovery(
            search: { _ in [self.response(bad), self.response(good)] },
            fetchDescription: { url in
                if url.absoluteString == bad { throw CastError.general("unreachable") }
                return xml
            },
            hostForTarget: { _ in nil },
            transport: soap.transport)
        let found = await collect(d.discover(searchWindow: 2))
        XCTAssertEqual(1, found.count)
        XCTAssertEqual("Living room TV", found.first?.friendlyName)
    }

    func testADeviceWithNoAvTransportIsNotACastTarget() async {
        // It cannot be controlled, so listing it would offer somebody a printer
        // that does nothing when they pick it.
        let soap = FakeSoap()
        let d = DlnaCastDiscovery(
            search: { _ in [self.response("http://192.168.1.10/desc.xml")] },
            fetchDescription: { _ in
                "<root><device><UDN>uuid:x</UDN><friendlyName>Printer</friendlyName></device></root>"
            },
            hostForTarget: { _ in nil },
            transport: soap.transport)
        let found = await collect(d.discover(searchWindow: 2))
        XCTAssertTrue(found.isEmpty)
    }

    func testFindingNothingIsAnEmptyStreamNotAnError() async {
        let soap = FakeSoap()
        let d = DlnaCastDiscovery(
            search: { _ in [] },
            fetchDescription: { _ in "" },
            hostForTarget: { _ in nil },
            transport: soap.transport)
        let found = await collect(d.discover(searchWindow: 2))
        XCTAssertTrue(found.isEmpty)
    }

    func testTheBindAddressIsTheOneOnTheSameNetworkAsTheTelevision() {
        // Binding to the wrong interface produces a URL the renderer cannot
        // reach, and the symptom is a television that accepts the command and
        // then shows nothing at all.
        let target = DescribedCastTarget(RendererDescription(
            udn: "uuid:tv-1", friendlyName: "TV", manufacturer: "Acme", modelName: "A1",
            location: URL(string: "http://192.168.1.10:8080/desc.xml")!,
            avTransportControlUrl: URL(string: "http://192.168.1.10:8080/ctl")!,
            iconUrl: nil))

        XCTAssertEqual("192.168.1.5",
                       DlnaCastEngine.bindAddress(for: target,
                                                  candidates: ["10.0.0.7", "192.168.1.5"]))
        // No same-subnet candidate: the first castable one is better than none.
        XCTAssertEqual("10.0.0.7",
                       DlnaCastEngine.bindAddress(for: target, candidates: ["10.0.0.7"]))
        // Nothing castable at all falls back to loopback rather than crashing.
        XCTAssertEqual("127.0.0.1",
                       DlnaCastEngine.bindAddress(for: target, candidates: ["127.0.0.1"]))
        XCTAssertEqual("127.0.0.1", DlnaCastEngine.bindAddress(for: target, candidates: []))
    }
}

final class NullDocumentCastAdapterTests: XCTestCase {

    func testItRefusesRatherThanReturningAnEmptyDeck() async {
        // An empty list looks like a document with no pages, which is
        // indistinguishable from success and is how somebody ends up casting a
        // blank screen to a room full of people.
        do {
            _ = try await NullDocumentCastAdapter.instance.toCastable(
                CastDocument(title: "Deck", source: .file("/tmp/x.pdf"),
                             mimeType: "application/pdf"))
            XCTFail("the null adapter returned a deck")
        } catch {
            XCTAssertTrue("\(error)".contains("page renderer"))
        }
        XCTAssertEqual("null", NullDocumentCastAdapter.instance.backendId)
    }
}
