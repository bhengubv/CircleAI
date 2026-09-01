// CastMediaHost.swift
//
// The LAN HTTP host a renderer pulls from.
//
// A DLNA RENDERER PULLS. It is handed a URL and fetches it; there is no push.
// So casting bytes or a file means serving them over the LAN for as long as the
// television is playing, and that is all this is.
//
// RANGE IS NOT OPTIONAL IN PRACTICE. A television that cannot ask for a byte
// range restarts the file from the beginning every time somebody scrubs, and
// some refuse to play at all without an Accept-Ranges header.
//
// Ported from Http/TcpMediaHost.cs.

import Foundation

/// The parts of the host that are pure arithmetic and string handling, split
/// out so they can be tested without opening a socket. Every one of these has
/// been a bug in somebody's DLNA implementation.
public enum MediaHostHttp {

    /// The first byte and the last, INCLUSIVE, or nil when the header is one
    /// this cannot honour.
    ///
    /// Three forms all turn up in the wild:
    ///   bytes=0-499   the first 500
    ///   bytes=500-    from 500 to the end
    ///   bytes=-500    the LAST 500, not the first
    ///
    /// Reading the suffix form as a start offset serves the wrong part of the
    /// file, and the picture is silently corrupt rather than absent.
    public static func parseRange(_ header: String, length: Int64) -> (start: Int64, end: Int64)? {
        let prefix = "bytes="
        guard header.lowercased().hasPrefix(prefix) else { return nil }

        var spec = String(header.dropFirst(prefix.count))
        // Only the FIRST range is honoured; a multipart response is not
        // something any renderer here asks for.
        if let comma = spec.firstIndex(of: ",") { spec = String(spec[spec.startIndex..<comma]) }
        guard let dash = spec.firstIndex(of: "-") else { return nil }

        let startPart = spec[spec.startIndex..<dash].trimmingCharacters(in: .whitespaces)
        let endPart = spec[spec.index(after: dash)...].trimmingCharacters(in: .whitespaces)

        var start: Int64
        var end: Int64

        if startPart.isEmpty {
            guard let suffix = Int64(endPart), suffix > 0 else { return nil }
            start = max(0, length - suffix)
            end = length - 1
        } else {
            guard let s = Int64(startPart) else { return nil }
            start = s
            if endPart.isEmpty {
                end = length - 1
            } else {
                guard let e = Int64(endPart) else { return nil }
                end = e
            }
        }

        guard start >= 0, end >= start else { return nil }
        // An end past the file is CLAMPED, not refused; renderers over-ask
        // routinely.
        if end > length - 1 { end = length - 1 }
        return start <= end ? (start, end) : nil
    }

    /// The extension is NOT cosmetic: several renderers dispatch on it and
    /// ignore Content-Type entirely.
    public static func extensionFor(mimeType: String) -> String {
        switch mimeType.lowercased() {
        case "video/mp4": return ".mp4"
        case "video/x-matroska": return ".mkv"
        case "video/webm": return ".webm"
        case "audio/mpeg": return ".mp3"
        case "audio/mp4": return ".m4a"
        case "audio/wav", "audio/x-wav": return ".wav"
        case "image/jpeg": return ".jpg"
        case "image/png", "image/apng": return ".png"
        case "image/gif": return ".gif"
        default: return ".bin"
        }
    }

    /// The response head a renderer needs.
    ///
    /// The two DLNA headers decide behaviour: without transferMode some sets
    /// download a whole video before showing anything, and some refuse an image
    /// outright.
    public static func responseHead(mimeType: String,
                                    totalLength: Int64,
                                    range: (start: Int64, end: Int64)?) -> String {
        let contentLength = totalLength == 0 ? 0 : (range.map { $0.end - $0.start + 1 } ?? totalLength)
        var head = range == nil ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 206 Partial Content\r\n"
        head += "Content-Type: \(mimeType)\r\n"
        head += "Content-Length: \(contentLength)\r\n"
        head += "Accept-Ranges: bytes\r\n"
        if let r = range {
            head += "Content-Range: bytes \(r.start)-\(r.end)/\(totalLength)\r\n"
        }
        head += "transferMode.dlna.org: "
        head += mimeType.lowercased().hasPrefix("image/") ? "Interactive" : "Streaming"
        head += "\r\n"
        head += "contentFeatures.dlna.org: DLNA.ORG_OP=01;DLNA.ORG_CI=0;"
        head += "DLNA.ORG_FLAGS=01700000000000000000000000000000\r\n"
        head += "Server: CircleAI.Cast/3.5\r\n"
        head += "Connection: close\r\n\r\n"
        return head
    }

    public static func statusLine(_ code: Int, _ reason: String) -> String {
        "HTTP/1.1 \(code) \(reason)\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
    }

    /// Method, path and the Range header, or nil if the head never completed.
    public static func parseRequest(_ raw: String) -> (method: String, path: String, range: String?)? {
        let lines = raw.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else { return nil }
        let parts = requestLine.split(separator: " ", omittingEmptySubsequences: true)
        guard parts.count >= 2 else { return nil }

        var range: String?
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = line[line.startIndex..<colon].trimmingCharacters(in: .whitespaces)
            if name.lowercased() == "range" {
                range = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            }
        }

        var path = String(parts[1])
        if let q = path.firstIndex(of: "?") { path = String(path[path.startIndex..<q]) }
        return (String(parts[0]), path, range)
    }
}

#if canImport(Network)
import Network

/// Serves each published asset at its own URL, with Range support so a renderer
/// can seek.
@available(macOS 10.14, iOS 12.0, tvOS 12.0, watchOS 5.0, *)
public final class TcpMediaHost: ILocalMediaHost, @unchecked Sendable {

    private struct Resource {
        let mime: String
        let length: Int64
        let bytes: Data?
        let filePath: String?
    }

    private let bind: String
    private let lock = NSLock()
    private var resources: [String: Resource] = [:]
    private var listener: NWListener?
    private var port: UInt16 = 0

    public init(bindAddress: String = "127.0.0.1") {
        self.bind = bindAddress
    }

    public var backendId: String { "tcp-http" }

    public var isRunning: Bool {
        lock.lock(); defer { lock.unlock() }
        return listener != nil
    }

    public var baseUrl: URL? {
        lock.lock(); defer { lock.unlock() }
        guard listener != nil else { return nil }
        return URL(string: "http://\(bind):\(port)/")
    }

    /// SYNCHRONOUS, deliberately: each of these takes and releases the lock
    /// without ever crossing an await, so it cannot be released on a different
    /// thread than took it. That is what NSLock-in-async warns about, and it is
    /// an error under Swift 6.
    private func alreadyRunning() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return listener != nil
    }

    private func adopt(_ l: NWListener) {
        lock.lock(); defer { lock.unlock() }
        listener = l
        port = l.port?.rawValue ?? 0
    }

    private func takeListener() -> NWListener? {
        lock.lock(); defer { lock.unlock() }
        let l = listener
        listener = nil
        resources.removeAll()
        return l
    }

    private func store(_ resource: Resource, at path: String) -> UInt16 {
        lock.lock(); defer { lock.unlock() }
        resources[path] = resource
        return port
    }

    private func forget(_ path: String) {
        lock.lock(); defer { lock.unlock() }
        resources.removeValue(forKey: path)
    }

    /// Port 0 asks the OS for a free one, and the assigned port is read back
    /// off the listener — hard-coding one collides with whatever else the
    /// device happens to be running.
    public func start() async throws {
        if alreadyRunning() { return }

        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        let l = try NWListener(using: params, on: .any)

        l.newConnectionHandler = { [weak self] connection in
            connection.start(queue: .global(qos: .userInitiated))
            self?.handle(connection)
        }

        let ready = AsyncStreamContinuationBox()
        l.stateUpdateHandler = { state in
            switch state {
            case .ready: ready.resume(nil)
            case .failed(let error): ready.resume(error)
            default: break
            }
        }
        l.start(queue: .global(qos: .userInitiated))
        try await ready.wait()
        adopt(l)
    }

    public func publish(_ source: CastMediaSource, mimeType: String) async throws -> URL {
        guard !mimeType.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw CastError.general("A mimeType is required to publish media.")
        }
        if !isRunning { try await start() }

        let path = "/" + UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            + MediaHostHttp.extensionFor(mimeType: mimeType)

        let resource: Resource
        switch source {
        case .bytes(let data):
            resource = Resource(mime: mimeType, length: Int64(data.count), bytes: data, filePath: nil)
        case .file(let p):
            let size = (try? FileManager.default.attributesOfItem(atPath: p)[.size] as? Int64) ?? 0
            resource = Resource(mime: mimeType, length: size ?? 0, bytes: nil, filePath: p)
        case .url:
            // Already reachable by the renderer; publishing is only for bytes
            // and files.
            throw CastError.general(
                "URL sources are already reachable; publish is only for bytes and file media.")
        }

        let p = store(resource, at: path)
        guard let url = URL(string: "http://\(bind):\(p)\(path)") else {
            throw CastError.general("Could not form a media URL for \(path).")
        }
        return url
    }

    public func unpublish(_ url: URL) async {
        forget(url.path)
    }

    public func close() async {
        takeListener()?.cancel()
    }

    // MARK: - Serving

    private func handle(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) {
            [weak self] data, _, _, _ in
            guard let self, let data, !data.isEmpty,
                  let raw = String(data: data, encoding: .utf8),
                  let request = MediaHostHttp.parseRequest(raw) else {
                connection.cancel()
                return
            }
            self.serve(connection, request)
        }
    }

    private func serve(_ connection: NWConnection,
                       _ request: (method: String, path: String, range: String?)) {
        func finish(_ payload: Data) {
            connection.send(content: payload, completion: .contentProcessed { _ in
                connection.cancel()
            })
        }

        let isGet = request.method.uppercased() == "GET"
        let isHead = request.method.uppercased() == "HEAD"
        guard isGet || isHead else {
            finish(Data(MediaHostHttp.statusLine(405, "Method Not Allowed").utf8))
            return
        }

        lock.lock(); let resource = resources[request.path]; lock.unlock()
        guard let resource else {
            finish(Data(MediaHostHttp.statusLine(404, "Not Found").utf8))
            return
        }

        let range = (resource.length > 0 && request.range != nil)
            ? MediaHostHttp.parseRange(request.range!, length: resource.length)
            : nil

        let head = MediaHostHttp.responseHead(mimeType: resource.mime,
                                              totalLength: resource.length,
                                              range: range)
        var payload = Data(head.utf8)

        if !isHead && resource.length > 0 {
            let start = range?.start ?? 0
            let end = range?.end ?? (resource.length - 1)
            let count = Int(end - start + 1)

            if let bytes = resource.bytes {
                payload.append(bytes.subdata(in: Int(start)..<(Int(start) + count)))
            } else if let path = resource.filePath,
                      let handle = FileHandle(forReadingAtPath: path) {
                defer { try? handle.close() }
                try? handle.seek(toOffset: UInt64(start))
                if let body = try? handle.read(upToCount: count) { payload.append(body) }
            }
        }

        finish(payload)
    }
}

/// A one-shot bridge from NWListener's callback-shaped readiness to an await.
private final class AsyncStreamContinuationBox: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Error>?
    private var settled: Error??

    func resume(_ error: Error?) {
        lock.lock()
        if let c = continuation {
            continuation = nil
            lock.unlock()
            if let error { c.resume(throwing: error) } else { c.resume() }
            return
        }
        // Ready before anybody waited: remember it rather than dropping it.
        settled = .some(error)
        lock.unlock()
    }

    func wait() async throws {
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            lock.lock()
            if let already = settled {
                lock.unlock()
                if let error = already { c.resume(throwing: error) } else { c.resume() }
                return
            }
            continuation = c
            lock.unlock()
        }
    }
}
#endif
