// InferencePreflight.swift
//
// Checks the network BEFORE a 433 MB download, and routes AROUND a dead system
// resolver rather than surrendering to it. And takes a model somebody copied
// onto the phone by hand and makes it a first-class installed model — or
// refuses it, loudly, with a reason.
//
// WHY NOT "RESTART DNS": an app cannot. There is no public API to flush or
// restart the platform resolver on Android, and toggling Wi-Fi has been a no-op
// for non-system apps since API 29. Toggling it over adb fixes the phone; the
// app has no such power. So the recovery is not to REPAIR the resolver but to
// BYPASS it:
//
//   1. Ask the system resolver.                    fast path, nearly always works
//   2. If that fails, resolve over DNS-over-HTTPS  needs NO system DNS, because
//      addressed by IP LITERAL.                    there is no name to look up
//   3. Connect to the resulting address.
//
// Step 2 is the whole trick: https://1.1.1.1/dns-query is reachable with a
// broken resolver precisely because 1.1.1.1 is already an address.
//
// RESOLVER CHOICE — de-Googled by policy. Cloudflare and Quad9 only; 8.8.8.8 is
// Google and is deliberately absent. Quad9 is second because it is run by a
// Swiss non-profit, which is a different failure domain from Cloudflare rather
// than a second helping of the same one.
//
// Ported from src/CircleAI.Inference/{NetworkPreflight, SideloadedBundleImporter}.cs.

import Foundation
#if canImport(CryptoKit)
import CryptoKit
#endif

// MARK: - Preflight

public protocol INetworkPreflight: Sendable {
    func check(target: URL) async throws -> NetworkDiagnosis
    func resolve(host: String) async -> [String]
}

/// One HTTP exchange, as a closure.
///
/// The library owns no HTTP client: a host already has one configured with its
/// own timeouts, proxy and pinning, and a second one hidden in here would
/// quietly bypass all of it.
public typealias PreflightTransport =
    @Sendable (URLRequest) async throws -> (data: Data, status: Int, location: String?)

public struct NetworkPreflight: INetworkPreflight, Sendable {

    /// Cloudflare first, then Quad9. NOT 8.8.8.8 — that is Google, and its
    /// absence here is a policy decision, not an oversight.
    public static let dohEndpoints = [
        "https://1.1.1.1/dns-query",            // Cloudflare
        "https://9.9.9.9:5053/dns-query",       // Quad9, a Swiss non-profit
    ]

    private let transport: PreflightTransport
    private let systemResolve: @Sendable (String) async -> [String]
    private let linkIsUp: @Sendable () -> Bool

    public init(transport: @escaping PreflightTransport,
                systemResolve: @escaping @Sendable (String) async -> [String] = { _ in [] },
                linkIsUp: @escaping @Sendable () -> Bool = { true }) {
        self.transport = transport
        self.systemResolve = systemResolve
        self.linkIsUp = linkIsUp
    }

    public func check(target: URL) async throws -> NetworkDiagnosis {
        // LINK LAYER FIRST — cheapest, and it distinguishes "no network at all"
        // from "network but broken", which have different remedies.
        guard linkIsUp() else {
            return NetworkDiagnosis(
                fault: .noLink,
                detail: "no network interface is up",
                remedy: "Connect to Wi-Fi or mobile data.",
                isTransient: true)
        }

        var request = URLRequest(url: target)
        // HEAD, not GET: this wants reachability, not 433 MB of payload.
        request.httpMethod = "HEAD"

        do {
            let (_, status, location) = try await transport(request)

            // A REDIRECT TO AN UNRELATED HOST ON A PLAIN HEAD is the classic
            // captive-portal signature: the network answered for somebody else.
            if Self.isRedirect(status),
               let location,
               let loc = URL(string: location),
               let host = loc.host,
               host.caseInsensitiveCompare(target.host ?? "") != .orderedSame {
                return NetworkDiagnosis(
                    fault: .captivePortal,
                    detail: "redirected to \(host)",
                    remedy: "This Wi-Fi needs you to sign in first. Open a browser and "
                          + "complete sign-in.",
                    // NOT transient: retrying will redirect again until somebody
                    // signs in, and spinning on it drains a battery for nothing.
                    isTransient: false)
            }

            if !(200..<400).contains(status) {
                return NetworkDiagnosis.classify(httpStatus: status)
            }
            return .healthy

        } catch is CancellationError {
            throw CancellationError()       // the caller cancelled; not a fault
        } catch {
            let diagnosis = NetworkDiagnosis.classify(error)

            // A DNS FAILURE IS NOT NECESSARILY FATAL — the bypass may still
            // resolve it. Only report it if that ALSO fails, otherwise this
            // blocks a download that would have worked.
            if diagnosis.fault == .dnsFailure {
                let viaDoh = await resolveViaDoh(host: target.host ?? "")
                if let first = viaDoh.first {
                    return NetworkDiagnosis(
                        fault: .dnsFailure,
                        detail: "system resolver failed for '\(target.host ?? "")'; "
                              + "resolved \(first) over DoH instead",
                        remedy: "",          // nothing to do — we routed around it
                        isTransient: true)
                }
            }
            return diagnosis
        }
    }

    public func resolve(host: String) async -> [String] {
        let trimmed = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return [] }

        // Already an address — nothing to resolve, and asking a resolver about
        // an IP literal is how a working connection gets blocked by a broken one.
        if Self.isIpLiteral(trimmed) { return [trimmed] }

        let system = await systemResolve(trimmed)
        if !system.isEmpty { return system }

        return await resolveViaDoh(host: trimmed)
    }

    func resolveViaDoh(host: String) async -> [String] {
        guard !host.isEmpty else { return [] }

        for endpoint in Self.dohEndpoints {
            guard var components = URLComponents(string: endpoint) else { continue }
            components.queryItems = [
                URLQueryItem(name: "name", value: host),
                URLQueryItem(name: "type", value: "A"),
            ]
            guard let url = components.url else { continue }

            var request = URLRequest(url: url)
            // RFC 8484 JSON profile — both endpoints serve it.
            request.setValue("application/dns-json", forHTTPHeaderField: "Accept")

            do {
                let (data, status, _) = try await transport(request)
                guard (200..<300).contains(status) else { continue }
                let addresses = Self.parseDohAnswer(data)
                if !addresses.isEmpty { return addresses }
            } catch {
                // Try the next resolver. Both being unreachable means the LINK
                // is dead, which `check` reports separately and differently.
                continue
            }
        }
        return []
    }

    /// Reads the RFC 8484 JSON answer, keeping only A records.
    ///
    /// Type 1 is A. A CNAME (type 5) in the same answer is a NAME, not an
    /// address, and connecting to it would need the resolver that just failed.
    static func parseDohAnswer(_ data: Data) -> [String] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let answers = root["Answer"] as? [[String: Any]]
        else { return [] }

        return answers.compactMap { a in
            guard (a["type"] as? Int) == 1,
                  let value = a["data"] as? String,
                  isIpLiteral(value)
            else { return nil }
            return value
        }
    }

    static func isRedirect(_ status: Int) -> Bool {
        [301, 302, 303, 307, 308].contains(status)
    }

    /// Dotted-quad only, matching the A records this asks for. Deliberately not
    /// a permissive check: a hostname smuggled in where an address belongs would
    /// be handed straight back to the resolver that is broken.
    static func isIpLiteral(_ s: String) -> Bool {
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        return parts.allSatisfy { part in
            guard !part.isEmpty, part.count <= 3,
                  part.allSatisfy(\.isNumber), let v = Int(part) else { return false }
            return v >= 0 && v <= 255
        }
    }
}

// MARK: - Side-loaded bundles

public enum SideloadOutcome: Int, Sendable, Equatable, Codable, CaseIterable {
    case imported = 0
    case alreadyInstalled
    case notFound
    case corrupt
    case unknown
    case copyFailed
}

public struct SideloadResult: Sendable, Equatable {
    public let outcome: SideloadOutcome
    /// Written for a PERSON, not a log.
    public let detail: String
    public let files: Int

    public init(outcome: SideloadOutcome, detail: String, files: Int = 0) {
        self.outcome = outcome
        self.detail = detail
        self.files = files
    }

    /// Both of these mean the model is there and checked. A caller that treats
    /// only `imported` as success re-imports on every launch.
    public var usable: Bool { outcome == .imported || outcome == .alreadyInstalled }
}

/// Makes a hand-copied model a first-class installed one, or refuses it.
///
/// WHY THIS IS A FEATURE AND NOT A DEVELOPER HOOK. A 7 MB wake word or a 900 MB
/// generalist is real money on a prepaid bundle, and the people this is built
/// for are exactly the ones who will be handed a model over Bluetooth, on a
/// memory card, or from a friend's laptop. Reading a side-loaded folder was
/// already possible; what was missing is everything that makes it TRUSTWORTHY —
/// nothing checked the bytes were the bytes we published, and nothing moved them
/// into the store, so the app kept treating an installed model as absent and
/// offering to download it again.
///
/// VERIFY, THEN IMPORT, IN THAT ORDER. The registry pins a SHA-256 for every
/// file in every bundle, so a side-loaded copy is held to exactly the standard a
/// downloaded one is. That is the whole security story for this path: a model
/// arriving by an untrusted route is checked against a hash we shipped, and one
/// that does not match never reaches the store. Without it, "copy this folder
/// onto your phone" is an invitation to run somebody else's weights.
public struct SideloadedBundleImporter: Sendable {

    private let lookup: @Sendable (String) -> [BundleFile]?
    private let storageRoot: String

    public init(storageRoot: String, lookup: @escaping @Sendable (String) -> [BundleFile]?) {
        self.storageRoot = storageRoot
        self.lookup = lookup
    }

    public func `import`(modelName: String, from folder: String) -> SideloadResult {
        guard let wanted = lookup(modelName), !wanted.isEmpty else {
            return SideloadResult(
                outcome: .unknown,
                detail: "\u{201C}\(modelName)\u{201D} is not in the catalogue, so there is "
                      + "nothing to check this against.")
        }

        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: folder, isDirectory: &isDir),
              isDir.boolValue else {
            return SideloadResult(outcome: .notFound, detail: "That folder is not there.")
        }

        // The published names are repo-relative ("kws-hey-b/encoder.int8.onnx"),
        // but somebody copying a folder across keeps the LEAF names and rarely
        // the path. Both are accepted, keyed on the leaf.
        var present: [String: String] = [:]
        if let walker = FileManager.default.enumerator(atPath: folder) {
            for case let p as String in walker {
                let full = (folder as NSString).appendingPathComponent(p)
                var sub: ObjCBool = false
                guard FileManager.default.fileExists(atPath: full, isDirectory: &sub),
                      !sub.boolValue else { continue }
                present[((p as NSString).lastPathComponent).lowercased()] = full
            }
        }

        var verified: [(relative: String, source: String)] = []
        for want in wanted {
            let leaf = (want.name as NSString).lastPathComponent
            guard let source = present[leaf.lowercased()] else {
                return SideloadResult(outcome: .notFound,
                                      detail: "This copy is missing \(leaf).")
            }

            // SIZE FIRST, because it is free and it catches the overwhelmingly
            // common failure — a copy that stopped part-way — without reading
            // 400 MB to find out.
            let attrs = try? FileManager.default.attributesOfItem(atPath: source)
            let actualSize = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
            if want.sizeBytes > 0 && actualSize != want.sizeBytes {
                return SideloadResult(
                    outcome: .corrupt,
                    detail: "\(leaf) is the wrong size — \(actualSize) bytes instead of "
                          + "\(want.sizeBytes). The copy is probably incomplete.")
            }

            if !want.sha256.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                guard let actual = Self.sha256Hex(ofFileAt: source) else {
                    return SideloadResult(outcome: .corrupt,
                                          detail: "\(leaf) could not be read.")
                }
                guard actual.caseInsensitiveCompare(want.sha256) == .orderedSame else {
                    return SideloadResult(
                        outcome: .corrupt,
                        detail: "\(leaf) does not match the published version. It may have "
                              + "been damaged in transit, or it may not be ours.")
                }
            }

            verified.append((want.name, source))
        }

        let target = (storageRoot as NSString).appendingPathComponent(modelName)
        let allPresent = verified.allSatisfy {
            FileManager.default.fileExists(
                atPath: (target as NSString).appendingPathComponent($0.relative))
        }
        if FileManager.default.fileExists(atPath: target) && allPresent {
            return SideloadResult(outcome: .alreadyInstalled,
                                  detail: "This is already installed.", files: verified.count)
        }

        for (relative, source) in verified {
            let dest = (target as NSString).appendingPathComponent(relative)
            let dir = (dest as NSString).deletingLastPathComponent
            do {
                try FileManager.default.createDirectory(atPath: dir,
                                                        withIntermediateDirectories: true)
                // COPY, NEVER MOVE. The folder may be shared storage somebody
                // wants to pass on to the next phone, and consuming it would
                // make installing on one device destroy the copy for everyone
                // else.
                if FileManager.default.fileExists(atPath: dest) {
                    try FileManager.default.removeItem(atPath: dest)
                }
                try FileManager.default.copyItem(atPath: source, toPath: dest)
            } catch {
                return SideloadResult(outcome: .copyFailed,
                                      detail: "Could not save it: \(error.localizedDescription)",
                                      files: verified.count)
            }
        }

        return SideloadResult(outcome: .imported, detail: "Installed and checked.",
                              files: verified.count)
    }

    /// Streamed in chunks rather than read whole: a 900 MB model read into
    /// memory to hash it is exactly the allocation a low-end phone cannot make.
    static func sha256Hex(ofFileAt path: String) -> String? {
        #if canImport(CryptoKit)
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }

        var hasher = SHA256()
        while true {
            guard let chunk = try? handle.read(upToCount: 1 << 20), !chunk.isEmpty else { break }
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
        #else
        return nil
        #endif
    }
}
