// InferenceNetwork.swift
//
// Why a download failed, in words a person can act on — and whether it should
// have been attempted at all on this connection.
//
// BEFORE THIS, A FAILED MODEL DOWNLOAD SURFACED AS A BARE TRANSPORT ERROR. On a
// Huawei P30 Lite with a dead system resolver that read as "Unable to resolve
// host modelscope.cn", which is indistinguishable — to the caller and to the
// person holding the phone — from "the mirror is down", "you are offline", "the
// hotel wifi wants you to log in", or "the file 404'd". Those have completely
// different remedies, and only some of them are the user's to fix. Shipping the
// raw error makes every one of them read as "CircleAI is broken".
//
// Ported from src/CircleAI.Inference/{NetworkDiagnosis, ModelDownloadException,
// ModelDownloadGate}.cs. ModelDownloadException lands as a CASE on the existing
// ModelDownloadError rather than as a second type - two error types for one
// operation is how a caller ends up handling one and letting the other escape.

import Foundation

// MARK: - What went wrong

public enum NetworkFault: Int, Sendable, Equatable, Codable, CaseIterable {
    case none = 0
    case noLink
    case dnsFailure
    case captivePortal
    case hostUnreachable
    case tlsFailure
    case timeout
    case httpError
    case unknown
}

/// A fault, what actually happened, and what the person can do about it.
///
/// `remedy` is EMPTY when there is nothing they can do — a dead mirror is not
/// theirs to fix, and inventing advice ("check your connection") for a problem
/// on our side sends somebody to reboot a router that was working.
public struct NetworkDiagnosis: Sendable, Equatable, CustomStringConvertible {
    public let fault: NetworkFault
    public let detail: String
    public let remedy: String
    /// Whether retrying could plausibly work. A 404 is not transient; a timeout
    /// is. Spinning on the first wastes battery and never succeeds.
    public let isTransient: Bool

    public init(fault: NetworkFault, detail: String, remedy: String, isTransient: Bool) {
        self.fault = fault
        self.detail = detail
        self.remedy = remedy
        self.isTransient = isTransient
    }

    public static let healthy = NetworkDiagnosis(fault: .none, detail: "reachable",
                                                 remedy: "", isTransient: false)

    public var shouldBlockDownload: Bool { fault != .none }

    public var description: String {
        if fault == .none { return "network: ok" }
        return remedy.isEmpty
            ? "network: \(fault) — \(detail)"
            : "network: \(fault) — \(detail). \(remedy)"
    }

    /// Classifies a raw error into a verdict.
    ///
    /// MATCHED ON NAME AND MESSAGE AS WELL AS ON CODE, deliberately. On Android
    /// the underlying failure is a Java type that does not exist in a portable
    /// library and cannot be caught by type at all; on Darwin the same failure
    /// arrives as an NSError in a different domain. Text matching is what makes
    /// one classifier serve both.
    public static func classify(_ error: Error) -> NetworkDiagnosis {
        for e in chain(of: error) {
            if let d = classifyOne(e) { return d }
        }
        return NetworkDiagnosis(fault: .unknown, detail: "\(error)",
                                remedy: "", isTransient: true)
    }

    /// Classifies an HTTP status that came back successfully but is a failure.
    public static func classify(httpStatus code: Int) -> NetworkDiagnosis {
        guard !(200..<300).contains(code) else { return .healthy }
        return NetworkDiagnosis(
            fault: .httpError,
            detail: "HTTP \(code)",
            remedy: "",
            // 5xx and 429 may pass on a retry; 4xx will not, so do not spin.
            isTransient: code >= 500 || code == 429)
    }

    private static func chain(of error: Error) -> [Error] {
        var out: [Error] = [error]
        var current = error as NSError
        while let underlying = current.userInfo[NSUnderlyingErrorKey] as? NSError {
            out.append(underlying)
            current = underlying
        }
        return out
    }

    private static func classifyOne(_ e: Error) -> NetworkDiagnosis? {
        let ns = e as NSError
        let message = ns.localizedDescription
        let combined = "\(type(of: e)) \(ns.domain) \(message)"

        // DNS FIRST. A resolution failure is also a connection failure, and
        // checking the generic case first would report "no network" to somebody
        // whose network is fine and whose resolver is not.
        if matchesDnsFailure(ns, combined) {
            return NetworkDiagnosis(
                fault: .dnsFailure, detail: message,
                remedy: "Your device is connected but cannot look up addresses. "
                      + "Turning Wi-Fi off and on again usually fixes it.",
                isTransient: true)
        }

        if ns.domain == NSURLErrorDomain {
            switch ns.code {
            case NSURLErrorNotConnectedToInternet, NSURLErrorNetworkConnectionLost,
                 NSURLErrorDataNotAllowed, NSURLErrorInternationalRoamingOff:
                return NetworkDiagnosis(
                    fault: .noLink, detail: message,
                    remedy: "There is no network connection. Connect to Wi-Fi or mobile data.",
                    isTransient: true)

            case NSURLErrorTimedOut:
                return NetworkDiagnosis(
                    fault: .timeout, detail: message,
                    remedy: "The connection is very slow or stalled. Try again on a better signal.",
                    isTransient: true)

            case NSURLErrorCannotConnectToHost, NSURLErrorCannotFindHost,
                 NSURLErrorDNSLookupFailed:
                // A dead mirror is not the user's to fix, so no remedy.
                return NetworkDiagnosis(fault: .hostUnreachable, detail: message,
                                        remedy: "", isTransient: true)

            case NSURLErrorSecureConnectionFailed, NSURLErrorServerCertificateUntrusted,
                 NSURLErrorServerCertificateHasBadDate,
                 NSURLErrorServerCertificateNotYetValid,
                 NSURLErrorServerCertificateHasUnknownRoot,
                 NSURLErrorClientCertificateRejected:
                return NetworkDiagnosis(
                    fault: .tlsFailure, detail: message,
                    remedy: "The secure connection could not be verified. If you are on public "
                          + "Wi-Fi, sign in to the network first.",
                    isTransient: true)

            case NSURLErrorAppTransportSecurityRequiresSecureConnection:
                return NetworkDiagnosis(fault: .tlsFailure, detail: message,
                                        remedy: "", isTransient: false)

            default:
                return NetworkDiagnosis(fault: .unknown,
                                        detail: "url error \(ns.code): \(message)",
                                        remedy: "", isTransient: true)
            }
        }

        if ns.domain == NSPOSIXErrorDomain {
            switch Int32(ns.code) {
            case ENETDOWN, ENETUNREACH:
                return NetworkDiagnosis(
                    fault: .noLink, detail: message,
                    remedy: "There is no network connection. Connect to Wi-Fi or mobile data.",
                    isTransient: true)
            case ETIMEDOUT:
                return NetworkDiagnosis(
                    fault: .timeout, detail: message,
                    remedy: "The connection is very slow or stalled. Try again on a better signal.",
                    isTransient: true)
            case ECONNREFUSED, EHOSTUNREACH, EHOSTDOWN:
                return NetworkDiagnosis(fault: .hostUnreachable, detail: message,
                                        remedy: "", isTransient: true)
            default:
                return NetworkDiagnosis(fault: .unknown,
                                        detail: "socket error \(ns.code): \(message)",
                                        remedy: "", isTransient: true)
            }
        }

        // Text of last resort, for hosts whose errors carry neither domain.
        let lower = combined.lowercased()
        if lower.contains("timed out") || lower.contains("timeout") {
            return NetworkDiagnosis(
                fault: .timeout, detail: message,
                remedy: "The download timed out. Try again on a stronger connection.",
                isTransient: true)
        }
        if lower.contains("certificate") || lower.contains("ssl") || lower.contains("tls") {
            return NetworkDiagnosis(
                fault: .tlsFailure, detail: message,
                remedy: "The secure connection could not be verified. If you are on public "
                      + "Wi-Fi, sign in to the network first.",
                isTransient: true)
        }
        return nil
    }

    /// Every spelling of "the name did not resolve" that has actually been seen.
    static func matchesDnsFailure(_ ns: NSError, _ combined: String) -> Bool {
        if ns.domain == NSURLErrorDomain && ns.code == NSURLErrorDNSLookupFailed { return true }
        if ns.domain == NSPOSIXErrorDomain,
           Int32(ns.code) == EAI_NONAME || Int32(ns.code) == EAI_NODATA { return true }

        // The Android type, by NAME: it does not exist in a portable library and
        // cannot be caught by type. This is deliberate, not lazy.
        if combined.contains("UnknownHostException") { return true }

        let lower = combined.lowercased()
        return lower.contains("unable to resolve host")
            || lower.contains("no address associated with hostname")
            || combined.contains("EAI_NODATA")
            || combined.contains("EAI_NONAME")
            || lower.contains("name or service not known")
            || lower.contains("nodename nor servname provided")
    }
}

// MARK: - Should this download run at all

public protocol IModelDownloadGate: Sendable {
    /// Why this download must not start, or nil to allow it.
    func blockReason(estimatedBytes: Int64) -> String?

    /// Whether the guarantee actually HOLDS on this host.
    var isEnforceable: Bool { get }
}

public struct ModelDownloadBlocked: Error, CustomStringConvertible {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var description: String { message }
}

/// Enforces "Wi-Fi only", which was INERT for months.
///
/// The option existed, defaulted to on, and was documented as protecting mobile
/// data. Nothing read it. The download service had no network awareness at all,
/// so the SDK documented a protection it did not provide — and the smallest
/// catalogued bundle is 433 MB, which is real money on a South African prepaid
/// bundle.
///
/// THE HONEST DIFFICULTY. A device context is documented as reporting
/// "wifi"/"cellular"/"none", but a default one can only say "online" or "none" —
/// it cannot tell metered from unmetered. On such a host the guarantee is
/// genuinely unenforceable. Failing CLOSED on "online" would stop every desktop
/// host downloading anything; failing OPEN silently recreates the original bug
/// on exactly the devices it was meant to protect.
///
/// So: fail open, but never SILENTLY. `isEnforceable` reports whether the
/// guarantee holds, so a host can say "we cannot tell whether you are on mobile
/// data" instead of the SDK pretending it checked.
public struct MeteredNetworkDownloadGate: IModelDownloadGate, Sendable {

    private let networkType: @Sendable () -> String?
    private let wifiOnly: Bool

    public init(device: (any IDeviceContext)? = nil, wifiOnly: Bool = true) {
        let captured = device
        self.networkType = { captured?.networkType }
        self.wifiOnly = wifiOnly
    }

    public init(networkType: @escaping @Sendable () -> String?, wifiOnly: Bool = true) {
        self.networkType = networkType
        self.wifiOnly = wifiOnly
    }

    static let unmetered: Set<String> = ["wifi", "ethernet", "unmetered"]
    static let metered: Set<String> = ["cellular", "mobile", "metered"]

    public var isEnforceable: Bool {
        guard wifiOnly else { return true }            // nothing to enforce
        guard let net = Self.normalise(networkType()) else { return false }
        return Self.unmetered.contains(net) || Self.metered.contains(net) || net == "none"
    }

    public func blockReason(estimatedBytes: Int64) -> String? {
        guard wifiOnly else { return nil }
        guard let net = Self.normalise(networkType()) else { return nil }

        if Self.metered.contains(net) {
            let size = estimatedBytes > 0
                ? String(format: "%.0f MB", Double(estimatedBytes) / 1024 / 1024)
                : "a large"
            return "This download is \(size) and you appear to be on mobile data. "
                 + "Connect to Wi-Fi, or allow mobile downloads in settings."
        }

        if net == "none" {
            return "No network connection is available for the model download."
        }

        // Unmetered is allowed. "online", "mesh" and anything unrecognised are
        // also allowed — but see isEnforceable: we could not actually verify it.
        return nil
    }

    static func normalise(_ value: String?) -> String? {
        guard let value else { return nil }
        let t = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return t.isEmpty ? nil : t
    }
}
