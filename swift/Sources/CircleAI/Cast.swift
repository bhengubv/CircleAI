// Cast.swift
//
// Throwing what is on the phone onto the television: DLNA / UPnP discovery,
// control and metadata.
//
// Ported from src/CircleAI.Cast.
//
// SCOPE: the deterministic half is ported in full - SSDP request building and
// response parsing, device-description XML, the SOAP envelope, DIDL-Lite
// metadata, clock formats and the transport-state map. The socket, HTTP and
// TCP-media-host layers stay behind protocols with honest null implementations,
// the same shape the C# uses, so a host wires its own transport.

import Foundation

// MARK: - Primitives

public struct CastTargetId: Sendable, Equatable, Hashable, CustomStringConvertible {
    public let value: String
    public init(_ value: String) { self.value = value }
    public var description: String { value }
}

public enum CastProtocolKind: Int, Sendable, Equatable {
    case dlna = 0
}

public enum CastContentKind: Int, Sendable, Equatable {
    case image = 0
    case audio = 1
    case video = 2
    case slideShow = 3
}

public enum CastPlaybackState: Int, Sendable, Equatable {
    case unknown = 0
    case idle
    case buffering
    case playing
    case paused
    case stopped
    case error
}

/// Where the media is. A URL the renderer can already reach, or something local
/// that has to be published over the LAN before a television can pull it.
public enum CastMediaSource: Sendable, Equatable {
    case url(URL)
    case file(String)
    case bytes(Data)
}

public struct CastMedia: Sendable, Equatable {
    public let source: CastMediaSource
    public let mimeType: String
    public let kind: CastContentKind
    public let title: String
    public let duration: TimeInterval?

    public init(source: CastMediaSource, mimeType: String, kind: CastContentKind,
                title: String = "", duration: TimeInterval? = nil) {
        self.source = source
        self.mimeType = mimeType
        self.kind = kind
        self.title = title
        self.duration = duration
    }

    public static func video(_ src: CastMediaSource, mime: String = "video/mp4",
                             title: String = "", duration: TimeInterval? = nil) -> CastMedia {
        CastMedia(source: src, mimeType: mime, kind: .video, title: title, duration: duration)
    }
    public static func image(_ src: CastMediaSource, mime: String = "image/jpeg",
                             title: String = "") -> CastMedia {
        CastMedia(source: src, mimeType: mime, kind: .image, title: title)
    }
    public static func audio(_ src: CastMediaSource, mime: String = "audio/mpeg",
                             title: String = "", duration: TimeInterval? = nil) -> CastMedia {
        CastMedia(source: src, mimeType: mime, kind: .audio, title: title, duration: duration)
    }
}

public struct CastStatus: Sendable, Equatable {
    public let state: CastPlaybackState
    public let position: TimeInterval
    public let duration: TimeInterval
    public let currentUri: String?

    public init(state: CastPlaybackState, position: TimeInterval,
                duration: TimeInterval, currentUri: String?) {
        self.state = state
        self.position = position
        self.duration = duration
        self.currentUri = currentUri
    }
}

public enum CastError: Error, CustomStringConvertible, Equatable {
    case control(String)
    case general(String)
    case noMediaHost

    public var description: String {
        switch self {
        case .control(let m): return m
        case .general(let m): return m
        case .noMediaHost:
            return "Byte/file media requires a local media host so the renderer can pull it over the LAN. " +
                   "Construct the session with a host."
        }
    }
}

/// XML escaping for the five predefined entities, matching what the C# side
/// puts on the wire. A television name with an ampersand in it breaks the SOAP
/// envelope otherwise.
enum XmlText {
    static func escape(_ s: String) -> String {
        var out = ""
        out.reserveCapacity(s.count)
        for c in s {
            switch c {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "\u{22}": out += "&quot;"
            case "\u{27}": out += "&apos;"
            default: out.append(c)
            }
        }
        return out
    }
}

// MARK: - A very small XML tree
//
// The C# reaches for XDocument.Descendants and matches on LOCAL name, ignoring
// namespaces entirely - which is the right call, because DLNA renderers in the
// wild declare namespaces inconsistently. XMLDocument is macOS-only, so this
// builds the same shape out of XMLParser, which exists everywhere.

/// One element: its local name, its own text, its attributes and its children.
final class XmlNode {
    let name: String
    var text: String = ""
    var attributes: [String: String] = [:]
    var children: [XmlNode] = []
    weak var parent: XmlNode?

    init(name: String) { self.name = name }

    /// Depth-first, self first - the same order XDocument.Descendants yields.
    func descendants() -> [XmlNode] {
        var out: [XmlNode] = []
        for c in children {
            out.append(c)
            out.append(contentsOf: c.descendants())
        }
        return out
    }

    /// First descendant with this local name, or nil.
    func first(_ localName: String) -> XmlNode? {
        descendants().first { $0.name == localName }
    }

    /// Trimmed text of the first descendant with this local name, or "".
    func value(_ localName: String) -> String {
        first(localName)?.text.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

    /// Direct child by local name - NOT a descendant. Needed where a service
    /// element must not pick up a nested one.
    func child(_ localName: String) -> XmlNode? {
        children.first { $0.name == localName }
    }
}

enum XmlLite {
    /// Parses into a tree, or nil when the document is malformed. Never throws:
    /// a renderer that returns broken XML is a device to skip, not a crash.
    static func parse(_ xml: String) -> XmlNode? {
        guard let data = xml.data(using: .utf8) else { return nil }
        let parser = XMLParser(data: data)
        let builder = TreeBuilder()
        parser.delegate = builder
        parser.shouldProcessNamespaces = true   // so elementName is the LOCAL name
        guard parser.parse() else { return nil }
        return builder.root
    }

    private final class TreeBuilder: NSObject, XMLParserDelegate {
        let root = XmlNode(name: "#document")
        private var stack: [XmlNode] = []

        override init() {
            super.init()
            stack = [root]
        }

        func parser(_ parser: XMLParser, didStartElement elementName: String,
                    namespaceURI: String?, qualifiedName qName: String?,
                    attributes attributeDict: [String: String] = [:]) {
            let node = XmlNode(name: elementName)
            node.attributes = attributeDict
            node.parent = stack.last
            stack.last?.children.append(node)
            stack.append(node)
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            stack.last?.text += string
        }

        func parser(_ parser: XMLParser, didEndElement elementName: String,
                    namespaceURI: String?, qualifiedName qName: String?) {
            if stack.count > 1 { stack.removeLast() }
        }
    }
}

// MARK: - SSDP

public struct SsdpResponse: Sendable, Equatable {
    public let location: URL
    public let searchTarget: String
    public let uniqueServiceName: String

    public init(location: URL, searchTarget: String, uniqueServiceName: String) {
        self.location = location
        self.searchTarget = searchTarget
        self.uniqueServiceName = uniqueServiceName
    }
}

public enum SsdpClient {
    public static let multicastAddress = "239.255.255.250"
    public static let port = 1900
    public static let mediaRendererTarget = "urn:schemas-upnp-org:device:MediaRenderer:1"

    /// The M-SEARCH datagram. CRLF line endings and the quoted MAN header are
    /// not style - renderers that see anything else simply do not answer.
    public static func searchRequest(target: String, window: TimeInterval) -> String {
        let mx = max(1, min(5, Int(window)))
        return "M-SEARCH * HTTP/1.1\r\n"
            + "HOST: \(multicastAddress):\(port)\r\n"
            + "MAN: \u{22}ssdp:discover\u{22}\r\n"
            + "MX: \(mx)\r\n"
            + "ST: \(target)\r\n"
            + "\r\n"
    }

    /// Parses one datagram. Header names are case-insensitive on the wire and
    /// devices disagree about capitalisation, so matching is folded.
    public static func parseResponse(_ text: String) -> SsdpResponse? {
        guard text.lowercased().hasPrefix("http/1.1") else { return nil }

        var location: String?
        var st: String?
        var usn: String?

        for raw in text.components(separatedBy: "\r\n") {
            guard let colon = raw.firstIndex(of: ":") else { continue }
            let key = String(raw[raw.startIndex..<colon]).trimmingCharacters(in: .whitespaces).uppercased()
            if key.isEmpty { continue }
            let val = String(raw[raw.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
            switch key {
            case "LOCATION": location = val
            case "ST": st = val
            case "USN": usn = val
            default: break
            }
        }

        guard let location, !location.isEmpty, let url = URL(string: location), url.scheme != nil else {
            return nil
        }
        return SsdpResponse(location: url, searchTarget: st ?? "", uniqueServiceName: usn ?? "")
    }

    public static func parseResponse(_ buffer: Data) -> SsdpResponse? {
        guard let text = String(data: buffer, encoding: .ascii) else { return nil }
        return parseResponse(text)
    }
}

// MARK: - Device description

public struct RendererDescription: Sendable, Equatable {
    public let udn: String
    public let friendlyName: String
    public let manufacturer: String
    public let modelName: String
    public let location: URL
    public let avTransportControlUrl: URL
    public let iconUrl: URL?

    public init(udn: String, friendlyName: String, manufacturer: String, modelName: String,
                location: URL, avTransportControlUrl: URL, iconUrl: URL?) {
        self.udn = udn
        self.friendlyName = friendlyName
        self.manufacturer = manufacturer
        self.modelName = modelName
        self.location = location
        self.avTransportControlUrl = avTransportControlUrl
        self.iconUrl = iconUrl
    }
}

public enum DeviceDescription {

    /// A renderer WITHOUT an AVTransport service cannot be controlled, so it is
    /// not a cast target and this returns nil rather than a half-usable one.
    public static func parse(_ xml: String, location: URL) -> RendererDescription? {
        guard let doc = XmlLite.parse(xml) else { return nil }

        // URLBase, when present, wins over the description URL for resolving
        // relative control paths.
        var baseUrl = location
        let urlBase = doc.value("URLBase")
        if !urlBase.isEmpty, let ub = URL(string: urlBase), ub.scheme != nil { baseUrl = ub }

        let avService = doc.descendants().first { node in
            node.name == "service"
                && (node.child("serviceType")?.text ?? "")
                    .range(of: "AVTransport", options: .caseInsensitive) != nil
        }
        guard let avService else { return nil }

        let controlPath = (avService.child("controlURL")?.text ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !controlPath.isEmpty, let controlUrl = resolve(controlPath, against: baseUrl) else {
            return nil
        }

        let udn = doc.value("UDN")
        let friendly = doc.value("friendlyName")

        var iconUrl: URL?
        if let iconPath = doc.first("icon")?.child("url")?.text
            .trimmingCharacters(in: .whitespacesAndNewlines), !iconPath.isEmpty {
            iconUrl = resolve(iconPath, against: baseUrl)
        }

        return RendererDescription(
            udn: udn.isEmpty ? location.absoluteString : udn,
            friendlyName: friendly.isEmpty ? "DLNA Renderer" : friendly,
            manufacturer: doc.value("manufacturer"),
            modelName: doc.value("modelName"),
            location: location,
            avTransportControlUrl: controlUrl,
            iconUrl: iconUrl)
    }

    /// Absolute paths resolve against the ORIGIN, relative ones against the
    /// directory - which is what a browser does and what renderers expect.
    static func resolve(_ path: String, against base: URL) -> URL? {
        if let absolute = URL(string: path), absolute.scheme != nil { return absolute }
        if path.hasPrefix("/") {
            guard var parts = URLComponents(url: base, resolvingAgainstBaseURL: false) else { return nil }
            parts.path = path
            parts.query = nil
            parts.fragment = nil
            return parts.url
        }
        return URL(string: path, relativeTo: base)?.absoluteURL
    }
}

// MARK: - AVTransport (SOAP)

public enum UpnpAvTransport {
    public static let serviceType = "urn:schemas-upnp-org:service:AVTransport:1"

    /// The full SOAP envelope for one action.
    public static func envelope(action: String, innerXml: String) -> String {
        "<?xml version=\u{22}1.0\u{22} encoding=\u{22}utf-8\u{22}?>"
            + "<s:Envelope xmlns:s=\u{22}http://schemas.xmlsoap.org/soap/envelope/\u{22} "
            + "s:encodingStyle=\u{22}http://schemas.xmlsoap.org/soap/encoding/\u{22}>"
            + "<s:Body>"
            + "<u:\(action) xmlns:u=\u{22}\(serviceType)\u{22}>\(innerXml)</u:\(action)>"
            + "</s:Body></s:Envelope>"
    }

    /// The SOAPACTION header value, quotes included - renderers reject it
    /// unquoted.
    public static func soapActionHeader(_ action: String) -> String {
        "\u{22}\(serviceType)#\(action)\u{22}"
    }

    public static func setAvTransportUriBody(mediaUrl: URL, didlMetadata: String) -> String {
        "<InstanceID>0</InstanceID>"
            + "<CurrentURI>\(XmlText.escape(mediaUrl.absoluteString))</CurrentURI>"
            + "<CurrentURIMetaData>\(XmlText.escape(didlMetadata))</CurrentURIMetaData>"
    }

    public static let playBody = "<InstanceID>0</InstanceID><Speed>1</Speed>"
    public static let pauseBody = "<InstanceID>0</InstanceID>"
    public static let stopBody = "<InstanceID>0</InstanceID>"

    public static func seekBody(position: TimeInterval) -> String {
        "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>\(formatClock(position))</Target>"
    }

    /// hh:mm:ss, zero padded, no fraction - the only form renderers accept.
    public static func formatClock(_ seconds: TimeInterval) -> String {
        let total = max(0, Int(seconds))
        return String(format: "%02d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }

    /// Renderers send h:mm:ss, hh:mm:ss, and sometimes hh:mm:ss.mmm or the
    /// literal NOT_IMPLEMENTED. Anything unreadable is zero, not a crash.
    public static func parseClock(_ text: String?) -> TimeInterval {
        guard let text else { return 0 }
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty { return 0 }

        let parts = t.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return 0 }
        guard let h = Int(parts[0]), let m = Int(parts[1]) else { return 0 }

        // Seconds may carry a fraction; take the whole part, as TimeSpan does.
        let secondsField = parts[2].split(separator: ".").first.map(String.init) ?? String(parts[2])
        guard let s = Int(secondsField) else { return 0 }
        guard h >= 0, m >= 0, m < 60, s >= 0, s < 60 else { return 0 }

        return TimeInterval(h * 3600 + m * 60 + s)
    }

    public static func transportState(from soapXml: String) -> String {
        guard let doc = XmlLite.parse(soapXml) else { return "UNKNOWN" }
        let v = doc.value("CurrentTransportState")
        return v.isEmpty ? "UNKNOWN" : v
    }

    public static func positionInfo(from soapXml: String) -> (position: TimeInterval, duration: TimeInterval) {
        guard let doc = XmlLite.parse(soapXml) else { return (0, 0) }
        return (parseClock(doc.first("RelTime")?.text), parseClock(doc.first("TrackDuration")?.text))
    }

    /// The names renderers actually report, mapped onto our states.
    public static func mapState(_ s: String) -> CastPlaybackState {
        switch s.uppercased() {
        case "PLAYING": return .playing
        case "PAUSED_PLAYBACK", "PAUSED": return .paused
        case "STOPPED": return .stopped
        case "TRANSITIONING": return .buffering
        case "NO_MEDIA_PRESENT": return .idle
        default: return .unknown
        }
    }
}

// MARK: - DIDL-Lite

public enum DidlLite {
    public static func protocolInfo(_ mime: String) -> String { "http-get:*:\(mime):*" }

    /// The metadata blob that rides alongside the URL. Televisions that ignore
    /// it still play; the ones that do not, will not play without it.
    public static func forMedia(_ media: CastMedia, url: URL, protocolInfo: String) -> String {
        let upnpClass: String
        switch media.kind {
        case .image, .slideShow: upnpClass = "object.item.imageItem.photo"
        case .audio: upnpClass = "object.item.audioItem.musicTrack"
        case .video: upnpClass = "object.item.videoItem"
        }

        let title = XmlText.escape(media.title.isEmpty ? "CircleAI" : media.title)
        let res = XmlText.escape(url.absoluteString)
        let pInfo = XmlText.escape(protocolInfo)

        return "<DIDL-Lite xmlns=\u{22}urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\u{22} "
            + "xmlns:dc=\u{22}http://purl.org/dc/elements/1.1/\u{22} "
            + "xmlns:upnp=\u{22}urn:schemas-upnp-org:metadata-1-0/upnp/\u{22}>"
            + "<item id=\u{22}0\u{22} parentID=\u{22}-1\u{22} restricted=\u{22}1\u{22}>"
            + "<dc:title>\(title)</dc:title>"
            + "<upnp:class>\(upnpClass)</upnp:class>"
            + "<res protocolInfo=\u{22}\(pInfo)\u{22}>\(res)</res>"
            + "</item></DIDL-Lite>"
    }
}

// MARK: - Local addresses
//
// A television pulls the media from the phone, so the URL handed to it must
// carry an address the television can route to. Loopback and link-local are the
// two that look fine on the phone and are unreachable from the television.

public enum LocalAddress {

    /// RFC 1918 only: 10/8, 172.16/12, 192.168/16.
    public static func isPrivateV4(_ b: [UInt8]) -> Bool {
        guard b.count == 4 else { return false }
        if b[0] == 10 { return true }
        if b[0] == 172 && b[1] >= 16 && b[1] <= 31 { return true }
        if b[0] == 192 && b[1] == 168 { return true }
        return false
    }

    /// APIPA. An address in here means DHCP never answered, and nothing on the
    /// LAN can reach it.
    public static func isLinkLocalV4(_ b: [UInt8]) -> Bool {
        b.count == 4 && b[0] == 169 && b[1] == 254
    }

    public static func isLoopbackV4(_ b: [UInt8]) -> Bool {
        b.count == 4 && b[0] == 127
    }

    /// Whether an address is usable as the host part of a URL a television on
    /// the same LAN will fetch.
    public static func isCastable(_ text: String) -> Bool {
        guard let ip = IPAddressValue(text), ip.family == .iPv4 else { return false }
        let b: [UInt8] = [
            UInt8((ip.v4 >> 24) & 0xFF), UInt8((ip.v4 >> 16) & 0xFF),
            UInt8((ip.v4 >> 8) & 0xFF), UInt8(ip.v4 & 0xFF),
        ]
        return !isLoopbackV4(b) && !isLinkLocalV4(b) && isPrivateV4(b)
    }
}

// MARK: - The seams

/// Somewhere a renderer can pull local bytes from over the LAN.
public protocol ILocalMediaHost: Sendable {
    var backendId: String { get }
    func publish(_ source: CastMediaSource, mimeType: String) async throws -> URL
    func unpublish(_ url: URL) async
}

/// No host is wired, so nothing local can be cast - only URLs the renderer can
/// already reach.
public struct NullLocalMediaHost: ILocalMediaHost {
    public static let instance = NullLocalMediaHost()
    public init() {}
    public var backendId: String { "null" }
    public func publish(_ source: CastMediaSource, mimeType: String) async throws -> URL {
        throw CastError.noMediaHost
    }
    public func unpublish(_ url: URL) async {}
}

public protocol ICastSession: Sendable {
    var target: any ICastTarget { get }
    func load(_ media: CastMedia) async throws
    func play() async throws
    func pause() async throws
    func stop() async throws
    func seek(to position: TimeInterval) async throws
    func status() async throws -> CastStatus
    func showSlideShow(_ images: [CastMedia], perImage: TimeInterval) async throws
    func close() async
}

public protocol ICastTarget: Sendable {
    var id: CastTargetId { get }
    var friendlyName: String { get }
    var manufacturer: String { get }
    var model: String { get }
    var castProtocol: CastProtocolKind { get }
    var location: URL { get }
    var iconUri: URL? { get }
    func connect() async throws -> any ICastSession
}

public protocol ICastDiscovery: Sendable {
    var backendId: String { get }
    func discover(searchWindow: TimeInterval) -> AsyncStream<any ICastTarget>
}

/// Finds nothing, and says which backend found nothing. This is what a build
/// with no LAN transport wired looks like.
public struct NullCastDiscovery: ICastDiscovery {
    public static let instance = NullCastDiscovery()
    public init() {}
    public var backendId: String { "null" }
    public func discover(searchWindow: TimeInterval) -> AsyncStream<any ICastTarget> {
        AsyncStream { $0.finish() }
    }
}

/// A target built straight from a parsed device description, with no transport
/// behind it. Useful for tests and for showing what was discovered before a
/// connection is attempted.
public struct DescribedCastTarget: ICastTarget {
    public let description: RendererDescription

    public init(_ description: RendererDescription) { self.description = description }

    public var id: CastTargetId { CastTargetId(description.udn) }
    public var friendlyName: String { description.friendlyName }
    public var manufacturer: String { description.manufacturer }
    public var model: String { description.modelName }
    public var castProtocol: CastProtocolKind { .dlna }
    public var location: URL { description.location }
    public var iconUri: URL? { description.iconUrl }

    public func connect() async throws -> any ICastSession {
        throw CastError.general(
            "No cast transport is wired on this build. A host must supply an HTTP client and a "
            + "local media host before a session can be opened to \(friendlyName).")
    }
}

/// The default slide-show interval, used when a caller passes a non-positive
/// one rather than refusing the whole request.
public enum CastDefaults {
    public static let slideShowPerImage: TimeInterval = 8

    public static func perImage(_ requested: TimeInterval) -> TimeInterval {
        requested <= 0 ? slideShowPerImage : requested
    }
}
