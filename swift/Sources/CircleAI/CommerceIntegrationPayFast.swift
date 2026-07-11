// CommerceIntegrationPayFast.swift
//
// Port of the Commerce.Integration.PayFast vertical from
// src/CircleAI.Commerce.Integration.PayFast/PayFastPrimitives.cs and the static
// domain-context constants from CommerceIntegrationPayFastDomainContext.cs:
//   • PayFastConfig, PayFastItnPayload      — domain records
//   • IPayFastBoard                         — signature builder / ITN verify /
//                                             webhook recorder
//   • InMemoryPayFastBoard                  — deterministic in-memory impl
//   • CommerceIntegrationPayFastDomainContext — system-prompt snippet + flags
//
// The Companion-facing wrapper (CommerceIntegrationPayFastCompanionAdapter) is
// intentionally NOT ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `decimal` → `Decimal`. `IReadOnlyDictionary<string,string>` for the
//     signature must preserve caller-supplied order, so the Swift signature
//     takes an ordered `[(String, String)]` (a Swift `Dictionary` is unordered
//     and would break signature parity). The C# call site iterates the field
//     order as supplied.
//   • The MD5 signature reproduces `SignatureFor` exactly:
//       key=urlencoded(value)&…  then (if passphrase set) passphrase=urlencoded
//       else trim a trailing '&'; MD5; lower-hex.
//     `WebUtility.UrlEncode` semantics are reproduced by `payFastUrlEncode`:
//     unreserved = A–Z a–z 0–9 - _ . ; space → '+' (already, so the C#
//     `.Replace("%20","+")` is a no-op but preserved for fidelity); everything
//     else → uppercase `%XX`. MD5 comes from CryptoKit's `Insecure.MD5`.
//   • `VerifyItn` returns `p.merchantId == Config.merchantId`.
//   • `RecentWebhooks` returns the most-recent `limit` in reverse-insertion
//     order (C# `_webhooks.AsEnumerable().Reverse().Take(limit)`).

import Foundation
import CryptoKit

// MARK: - Records

/// PayFast merchant configuration.
public struct PayFastConfig: Sendable, Equatable, Codable {
    /// PayFast merchant id.
    public let merchantId: String
    /// PayFast merchant key.
    public let merchantKey: String
    /// Signing passphrase (may be empty).
    public let passphrase: String
    /// Whether the sandbox environment is targeted.
    public let sandbox: Bool

    public init(merchantId: String, merchantKey: String, passphrase: String, sandbox: Bool) {
        self.merchantId = merchantId
        self.merchantKey = merchantKey
        self.passphrase = passphrase
        self.sandbox = sandbox
    }
}

/// A PayFast Instant Transaction Notification (ITN) payload.
public struct PayFastItnPayload: Sendable, Equatable, Codable {
    /// Merchant id echoed by PayFast.
    public let merchantId: String
    /// PayFast payment id.
    public let paymentId: String
    /// Payment status (e.g. "COMPLETE").
    public let paymentStatus: String
    /// Amount.
    public let amount: Decimal
    /// Merchant-supplied payment id (`m_payment_id`).
    public let mPaymentId: String
    /// Signature supplied with the ITN.
    public let signature: String

    public init(merchantId: String, paymentId: String, paymentStatus: String, amount: Decimal,
                mPaymentId: String, signature: String) {
        self.merchantId = merchantId
        self.paymentId = paymentId
        self.paymentStatus = paymentStatus
        self.amount = amount
        self.mPaymentId = mPaymentId
        self.signature = signature
    }
}

// MARK: - IPayFastBoard

/// PayFast signature building, ITN verification, and webhook recording. A
/// synchronous contract — implementations are expected to be thread-safe.
public protocol IPayFastBoard: AnyObject, Sendable {
    /// The active merchant configuration.
    var config: PayFastConfig { get }
    /// Builds the MD5 signature for the given *ordered* field list.
    func signatureFor(_ orderedFields: [(String, String)]) -> String
    /// Verifies an ITN payload against the configured merchant id.
    func verifyItn(_ p: PayFastItnPayload) -> Bool
    /// Records a received webhook payload.
    func recordWebhook(_ p: PayFastItnPayload)
    /// Up to `limit` most-recent webhooks, newest first.
    func recentWebhooks(limit: Int) -> [PayFastItnPayload]
}

public extension IPayFastBoard {
    /// Overload matching the C# default `limit = 20`.
    func recentWebhooks() -> [PayFastItnPayload] { recentWebhooks(limit: 20) }
}

// MARK: - InMemoryPayFastBoard

/// Deterministic in-memory `IPayFastBoard`. Webhook history is guarded by a
/// single `NSLock`.
public final class InMemoryPayFastBoard: IPayFastBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var webhooks: [PayFastItnPayload] = []
    public let config: PayFastConfig

    public init(_ cfg: PayFastConfig) { self.config = cfg }

    public func signatureFor(_ orderedFields: [(String, String)]) -> String {
        var s = ""
        for (key, value) in orderedFields {
            s += key + "=" + Self.payFastUrlEncode(value) + "&"
        }
        if !config.passphrase.isEmpty {
            s += "passphrase=" + Self.payFastUrlEncode(config.passphrase)
        } else if s.hasSuffix("&") {
            s.removeLast()
        }
        let digest = Insecure.MD5.hash(data: Data(s.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    public func verifyItn(_ p: PayFastItnPayload) -> Bool {
        p.merchantId == config.merchantId
    }

    public func recordWebhook(_ p: PayFastItnPayload) {
        lock.lock(); defer { lock.unlock() }
        webhooks.append(p)
    }

    public func recentWebhooks(limit: Int) -> [PayFastItnPayload] {
        lock.lock(); defer { lock.unlock() }
        return Array(webhooks.reversed().prefix(limit))
    }

    /// Reproduces `WebUtility.UrlEncode(value).Replace("%20", "+")`:
    /// unreserved = A–Z a–z 0–9 `-` `_` `.`; space → `+`; everything else →
    /// uppercase `%XX` over the UTF-8 bytes.
    static func payFastUrlEncode(_ value: String) -> String {
        var out = ""
        out.reserveCapacity(value.count)
        for byte in Array(value.utf8) {
            switch byte {
            case 0x41...0x5A, 0x61...0x7A, 0x30...0x39, // A-Z a-z 0-9
                 0x2D, 0x5F, 0x2E:                       // - _ .
                out.append(Character(UnicodeScalar(byte)))
            case 0x20: // space → '+'
                out.append("+")
            default:
                out.append("%")
                out.append(Self.hexUpper(byte))
            }
        }
        return out
    }

    private static func hexUpper(_ byte: UInt8) -> String {
        let digits = Array("0123456789ABCDEF")
        return String([digits[Int(byte >> 4)], digits[Int(byte & 0x0F)]])
    }
}

// MARK: - CommerceIntegrationPayFastDomainContext

/// Static domain-context constants for the PayFast integration vertical. Mirrors
/// `CommerceIntegrationPayFastDomainContext` in
/// CommerceIntegrationPayFastDomainContext.cs.
public enum CommerceIntegrationPayFastDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Commerce.Integration.PayFast] You are a PayFast payment gateway integration expert. Help with PayFast ITN (Instant Transaction Notification) webhook handling, payment flow debugging, refund processing, subscription billing, split payments, and PCI-DSS compliance guidance. Compliance: PCI-DSS, POPIA, PASA, Consumer Protection Act."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["PCI_DSS", "POPIA", "PASA", "Consumer_Protection_Act"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["payfast_api", "webhook_debugger", "document_editor"]
}
