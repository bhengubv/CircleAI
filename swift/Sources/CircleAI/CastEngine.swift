// CastEngine.swift
//
// The I/O half of CircleAI.Cast: the control point, the DLNA target and session
// over it, the discovery that mints targets, the LAN media host a renderer
// pulls from, and the engine that wires them together.
//
// The deterministic half — SSDP framing, the SOAP envelope, DIDL-Lite, the
// device description, the address rules — is in Cast.swift and is tested
// without a network. This file is the part that touches a socket.
//
// A DLNA RENDERER PULLS. There is no push: it is handed a URL and fetches it.
// That is why byte and file media need a LAN HTTP host at all, and why a
// session without one has to refuse them rather than pretend.
//
// Ported from DlnaCastEngine.cs, Dlna/*.cs, Http/TcpMediaHost.cs, Hosting.cs
// and NullImplementations.cs.

import Foundation

// MARK: - Contracts the I/O half adds

public protocol ICastEngine: Sendable {
    var backendId: String { get }
    func discover(searchWindow: TimeInterval) -> AsyncStream<any ICastTarget>
    func cast(_ target: any ICastTarget, media: CastMedia) async throws -> any ICastSession
}

public struct CastDocument: Sendable, Equatable {
    public let title: String
    public let source: CastMediaSource
    public let mimeType: String

    public init(title: String, source: CastMediaSource, mimeType: String) {
        self.title = title
        self.source = source
        self.mimeType = mimeType
    }
}

/// Turns a document or a deck into castable page images.
///
/// AN HONEST SEAM. Rasterising a PDF needs a page renderer that is not pure
/// managed code, so the contract is defined and the null implementation
/// REFUSES rather than returning an empty deck — which would look like a
/// document with no pages, be indistinguishable from success, and is how
/// somebody ends up casting a blank screen to a room.
public protocol IDocumentCastAdapter: Sendable {
    var backendId: String { get }
    func toCastable(_ document: CastDocument) async throws -> [CastMedia]
}

public struct NullDocumentCastAdapter: IDocumentCastAdapter {
    public static let instance = NullDocumentCastAdapter()
    public init() {}
    public var backendId: String { "null" }
    public func toCastable(_ document: CastDocument) async throws -> [CastMedia] {
        throw CastError.general(
            "Casting a document needs a page renderer wired through IDocumentCastAdapter. "
            + "Rasterising PDFs and decks is not pure managed code.")
    }
}

// MARK: - The control point

/// Posts one SOAP action to a renderer's control URL.
///
/// A closure rather than a concrete HTTP client, because this package has no
/// networking dependency and the host already owns one.
public typealias SoapTransport =
    @Sendable (_ controlUrl: URL, _ soapAction: String, _ body: String) async throws -> String

/// Drives one renderer's AVTransport service.
///
/// The envelope, the SOAPACTION quoting and the clock formats live in
/// UpnpAvTransport and are tested without a network; this is the thin layer
/// that puts them on the wire.
public struct UpnpControlPoint: Sendable {

    private let controlUrl: URL
    private let transport: SoapTransport

    public init(controlUrl: URL, transport: @escaping SoapTransport) {
        self.controlUrl = controlUrl
        self.transport = transport
    }

    @discardableResult
    func invoke(_ action: String, _ body: String) async throws -> String {
        try await transport(controlUrl,
                            UpnpAvTransport.soapActionHeader(action),
                            UpnpAvTransport.envelope(action: action, innerXml: body))
    }

    public func setAvTransportUri(_ mediaUrl: URL, didl: String) async throws {
        try await invoke("SetAVTransportURI",
                         UpnpAvTransport.setAvTransportUriBody(mediaUrl: mediaUrl,
                                                               didlMetadata: didl))
    }

    public func play() async throws { try await invoke("Play", UpnpAvTransport.playBody) }
    public func pause() async throws { try await invoke("Pause", UpnpAvTransport.pauseBody) }
    public func stop() async throws { try await invoke("Stop", UpnpAvTransport.stopBody) }

    public func seek(to seconds: TimeInterval) async throws {
        try await invoke("Seek", UpnpAvTransport.seekBody(position: seconds))
    }

    public func transportState() async throws -> String {
        UpnpAvTransport.transportState(
            from: try await invoke("GetTransportInfo", "<InstanceID>0</InstanceID>"))
    }

    /// Position and duration, in seconds.
    public func position() async throws -> (position: TimeInterval, duration: TimeInterval) {
        UpnpAvTransport.positionInfo(
            from: try await invoke("GetPositionInfo", "<InstanceID>0</InstanceID>"))
    }
}

// MARK: - Target and session

/// ICastTarget over a resolved UPnP MediaRenderer.
///
/// The session is minted by a factory rather than constructed here, so the
/// target itself stays free of HTTP and media-host wiring and can be compared,
/// listed and shown on a screen without any of it.
public struct DlnaCastTarget: ICastTarget {

    public let description: RendererDescription
    private let sessionFactory: @Sendable (DlnaCastTarget) -> any ICastSession

    public init(_ description: RendererDescription,
                sessionFactory: @escaping @Sendable (DlnaCastTarget) -> any ICastSession) {
        self.description = description
        self.sessionFactory = sessionFactory
    }

    public var id: CastTargetId { CastTargetId(description.udn) }
    public var friendlyName: String { description.friendlyName }
    public var manufacturer: String { description.manufacturer }
    public var model: String { description.modelName }
    public var castProtocol: CastProtocolKind { .dlna }
    public var location: URL { description.location }
    public var iconUri: URL? { description.iconUrl }

    public func connect() async throws -> any ICastSession { sessionFactory(self) }
}

/// ICastSession over UPnP AVTransport.
public final class DlnaCastSession: ICastSession, @unchecked Sendable {

    public let target: any ICastTarget
    private let control: UpnpControlPoint
    private let host: (any ILocalMediaHost)?

    private let lock = NSLock()
    private var published: [URL] = []
    private var currentUrl: URL?

    public init(target: any ICastTarget, control: UpnpControlPoint,
                host: (any ILocalMediaHost)?) {
        self.target = target
        self.control = control
        self.host = host
    }

    /// SYNCHRONOUS, deliberately: each of these takes and releases the lock
    /// without ever crossing an await. A lock held across a suspension point
    /// can be released on a different thread than took it, which is why Swift 6
    /// makes it an error.
    private func rememberCurrent(_ url: URL) {
        lock.lock(); defer { lock.unlock() }
        currentUrl = url
    }

    private func readCurrent() -> URL? {
        lock.lock(); defer { lock.unlock() }
        return currentUrl
    }

    private func notePublished(_ url: URL) {
        lock.lock(); defer { lock.unlock() }
        published.append(url)
    }

    private func takePublished() -> [URL] {
        lock.lock(); defer { lock.unlock() }
        let urls = published
        published = []
        return urls
    }

    public func load(_ media: CastMedia) async throws {
        let url = try await resolveUrl(media)
        let protocolInfo = DidlLite.protocolInfo(media.mimeType)
        let didl = DidlLite.forMedia(media, url: url, protocolInfo: protocolInfo)
        try await control.setAvTransportUri(url, didl: didl)
        rememberCurrent(url)
    }

    public func play() async throws { try await control.play() }
    public func pause() async throws { try await control.pause() }
    public func stop() async throws { try await control.stop() }
    public func seek(to position: TimeInterval) async throws { try await control.seek(to: position) }

    public func status() async throws -> CastStatus {
        let state = try await control.transportState()
        let (pos, dur) = try await control.position()
        let current = readCurrent()
        return CastStatus(state: UpnpAvTransport.mapState(state),
                          position: pos,
                          duration: dur,
                          currentUri: current?.absoluteString)
    }

    /// A slideshow is SetAVTransportURI in a LOOP. There is no DLNA slideshow
    /// action; a deck is cast by handing the renderer one image after another.
    public func showSlideShow(_ images: [CastMedia], perImage: TimeInterval) async throws {
        // A non-positive interval would advance instantly and show nothing.
        let interval = CastDefaults.perImage(perImage)
        for image in images {
            if Task.isCancelled { break }
            try await load(image)
            try await play()
            do {
                try await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
            } catch {
                // Cancelling a slideshow stops it where it is; not an error.
                break
            }
        }
    }

    /// Un-publishes what this session published, and LEAVES THE HOST ALONE: it
    /// is shared per bind address and owned by the engine, so closing it here
    /// would take down every other session pointed at the same interface.
    public func close() async {
        let urls = takePublished()
        guard let host else { return }
        for url in urls { await host.unpublish(url) }
    }

    private func resolveUrl(_ media: CastMedia) async throws -> URL {
        if case .url(let address) = media.source { return address }
        guard let host else { throw CastError.noMediaHost }
        let url = try await host.publish(media.source, mimeType: media.mimeType)
        notePublished(url)
        return url
    }
}

// MARK: - Discovery

/// Discovers renderers by SSDP and resolves each one's description.
public struct DlnaCastDiscovery: ICastDiscovery {

    public typealias Search = @Sendable (TimeInterval) async throws -> [SsdpResponse]
    public typealias FetchDescription = @Sendable (URL) async throws -> String

    private let search: Search
    private let fetchDescription: FetchDescription
    private let hostForTarget: @Sendable (any ICastTarget) -> (any ILocalMediaHost)?
    private let transport: SoapTransport

    public init(search: @escaping Search,
                fetchDescription: @escaping FetchDescription,
                hostForTarget: @escaping @Sendable (any ICastTarget) -> (any ILocalMediaHost)?,
                transport: @escaping SoapTransport) {
        self.search = search
        self.fetchDescription = fetchDescription
        self.hostForTarget = hostForTarget
        self.transport = transport
    }

    public var backendId: String { "dlna" }

    public func discover(searchWindow: TimeInterval) -> AsyncStream<any ICastTarget> {
        AsyncStream { continuation in
            Task {
                guard let responses = try? await search(searchWindow) else {
                    continuation.finish()
                    return
                }

                var seen = Set<String>()
                for response in responses {
                    // The SAME renderer answers an M-SEARCH several times — that
                    // is the protocol, not a fault. Emitting each answer would
                    // put one television in the list four times.
                    guard seen.insert(response.location.absoluteString).inserted else { continue }

                    // One unreachable or malformed device must not end the scan.
                    guard let xml = try? await fetchDescription(response.location),
                          let described = DeviceDescription.parse(xml, location: response.location)
                    else { continue }

                    let transport = self.transport
                    let hostFor = self.hostForTarget
                    continuation.yield(DlnaCastTarget(described) { target in
                        DlnaCastSession(
                            target: target,
                            control: UpnpControlPoint(controlUrl: described.avTransportControlUrl,
                                                      transport: transport),
                            host: hostFor(target))
                    })
                }
                continuation.finish()
            }
        }
    }
}

// MARK: - The engine

/// The one type most callers touch: find televisions, then fling something at
/// one. One media host per LAN bind address, created on first use and reused.
public final class DlnaCastEngine: ICastEngine, @unchecked Sendable {

    private let discovery: DlnaCastDiscovery
    private let makeHost: @Sendable (String) -> any ILocalMediaHost
    private let localAddresses: @Sendable () -> [String]

    private let lock = NSLock()
    private var hostsByBind: [String: any ILocalMediaHost] = [:]

    public init(search: @escaping DlnaCastDiscovery.Search,
                fetchDescription: @escaping DlnaCastDiscovery.FetchDescription,
                transport: @escaping SoapTransport,
                makeHost: @escaping @Sendable (String) -> any ILocalMediaHost,
                localAddresses: @escaping @Sendable () -> [String] = { [] }) {
        self.makeHost = makeHost
        self.localAddresses = localAddresses

        // Captured before self is fully formed, so the closure holds the boxes
        // rather than the engine.
        let box = HostBox()
        self.hostBox = box
        self.discovery = DlnaCastDiscovery(
            search: search,
            fetchDescription: fetchDescription,
            hostForTarget: { target in box.host(for: target) },
            transport: transport)
        box.resolve = { [makeHost, localAddresses] target in
            DlnaCastEngine.bindAddress(for: target, candidates: localAddresses())
        }
        box.make = makeHost
    }

    /// Holds the per-bind hosts so the discovery closure does not capture the
    /// engine and keep it alive after the caller has let go of it.
    final class HostBox: @unchecked Sendable {
        private let lock = NSLock()
        private var hosts: [String: any ILocalMediaHost] = [:]
        var resolve: (@Sendable (any ICastTarget) -> String)?
        var make: (@Sendable (String) -> any ILocalMediaHost)?

        func host(for target: any ICastTarget) -> (any ILocalMediaHost)? {
            guard let resolve, let make else { return nil }
            let bind = resolve(target)
            lock.lock(); defer { lock.unlock() }
            if let existing = hosts[bind] { return existing }
            let created = make(bind)
            hosts[bind] = created
            return created
        }

        func all() -> [any ILocalMediaHost] {
            lock.lock(); defer { lock.unlock() }
            let out = Array(hosts.values)
            hosts.removeAll()
            return out
        }
    }

    private let hostBox: HostBox

    public var backendId: String { "dlna" }

    public func discover(searchWindow: TimeInterval) -> AsyncStream<any ICastTarget> {
        discovery.discover(searchWindow: searchWindow)
    }

    public func cast(_ target: any ICastTarget, media: CastMedia) async throws -> any ICastSession {
        let session = try await target.connect()
        do {
            try await session.load(media)
            try await session.play()
            return session
        } catch {
            // A session that failed to START is closed here rather than left
            // holding published bytes nobody will ever come back for.
            await session.close()
            throw error
        }
    }

    public func hostFor(_ target: any ICastTarget) -> (any ILocalMediaHost)? {
        hostBox.host(for: target)
    }

    public func close() async {
        for host in hostBox.all() {
            if let closable = host as? TcpMediaHost { await closable.close() }
        }
    }

    /// The address to bind is the one on the SAME network as the television.
    ///
    /// Binding to the wrong interface produces a URL the renderer cannot reach,
    /// and the symptom is a television that accepts the command and then shows
    /// nothing at all.
    static func bindAddress(for target: any ICastTarget, candidates: [String]) -> String {
        guard let host = target.location.host else { return "127.0.0.1" }
        let castable = candidates.filter { LocalAddress.isCastable($0) }
        let prefix = host.split(separator: ".").dropLast().joined(separator: ".")
        if let sameSubnet = castable.first(where: {
            $0.split(separator: ".").dropLast().joined(separator: ".") == prefix
        }) {
            return sameSubnet
        }
        return castable.first ?? "127.0.0.1"
    }
}
