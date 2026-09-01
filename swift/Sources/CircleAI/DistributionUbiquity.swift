// DistributionUbiquity.swift
//
// The ubiquity rails: every route the software takes onto a phone, and every
// fallback for somebody who has no data, no storage, or no smartphone.
//
// Ported from src/CircleAI.Distribution/UbiquityRails.cs and
// UbiquityRailsMissingDefaults.cs. The store/delta/preload half already lives
// in Distribution.swift; this adds the rest.

import Foundation
import CryptoKit

// MARK: - Peer file sync

public struct FileMetadata: Sendable, Equatable {
    public let contentHash: String
    public let name: String
    public let sizeBytes: Int64
    public init(contentHash: String, name: String, sizeBytes: Int64) {
        self.contentHash = contentHash
        self.name = name
        self.sizeBytes = sizeBytes
    }
}

public struct Peer: Sendable, Equatable {
    public let peerId: String
    public let endpoint: String
    public let availableHashes: [String]
    public init(peerId: String, endpoint: String, availableHashes: [String]) {
        self.peerId = peerId
        self.endpoint = endpoint
        self.availableHashes = availableHashes
    }
}

/// Content-addressed: a file IS its hash, so the same bytes from any peer are
/// the same file and nobody has to trust who handed them over.
public protocol IFileSync: Sendable {
    var backendId: String { get }
    func has(contentHash: String) async -> Bool
    func fetch(contentHash: String) async -> Data?
    func announce(_ metadata: FileMetadata, payload: Data) async
}

public protocol IPeerAdvertiser: Sendable {
    var backendId: String { get }
    func discover() async -> [Peer]
}

/// Holds nothing and finds nobody. The honest default with no transport wired.
public struct NullFileSync: IFileSync {
    public static let instance = NullFileSync()
    public init() {}
    public var backendId: String { "null" }
    public func has(contentHash: String) async -> Bool { false }
    public func fetch(contentHash: String) async -> Data? { nil }
    public func announce(_ metadata: FileMetadata, payload: Data) async {}
}

public struct NullPeerAdvertiser: IPeerAdvertiser {
    public static let instance = NullPeerAdvertiser()
    public init() {}
    public var backendId: String { "null" }
    public func discover() async -> [Peer] { [] }
}

// MARK: - Distribution channels

public protocol IPwaFallback: Sendable { var pwaUrl: String { get } }
public struct DefaultPwaFallback: IPwaFallback {
    public init() {}
    public var pwaUrl: String { "https://app.circle.ai" }
}

public protocol ISideloadChannel: Sendable { var formats: [String] { get } }
public struct DefaultSideloadChannel: ISideloadChannel {
    public init() {}
    public var formats: [String] { ["APK", "IPA", "MSIX"] }
}

public protocol ILinuxRepoFanout: Sendable { var repos: [String] { get } }
public struct DefaultLinuxRepoFanout: ILinuxRepoFanout {
    public init() {}
    public var repos: [String] { ["apt", "yum", "pacman", "brew", "flatpak", "snap"] }
}

// MARK: - Onboarding

public struct PersonalityChoice: Sendable, Equatable {
    public let name: String
    public init(_ name: String) { self.name = name }
}

public protocol IAiPersonalityWizard: Sendable {
    var presets: [PersonalityChoice] { get }
    func select(sessionId: String, choice: PersonalityChoice) throws
}

public final class DefaultAiPersonalityWizard: IAiPersonalityWizard, @unchecked Sendable {
    private let lock = NSLock()
    private var selections: [String: PersonalityChoice] = [:]

    public init() {}

    public var presets: [PersonalityChoice] {
        [PersonalityChoice("formal"), PersonalityChoice("warm"),
         PersonalityChoice("playful"), PersonalityChoice("professional")]
    }

    /// A personality outside the preset list is REFUSED rather than stored -
    /// an unknown name would be a setting nothing ever reads.
    public func select(sessionId: String, choice: PersonalityChoice) throws {
        guard !sessionId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("sessionId")
        }
        guard presets.contains(where: { $0.name.lowercased() == choice.name.lowercased() }) else {
            throw DistributionError.unknownPersonality(choice.name)
        }
        lock.lock(); selections[sessionId] = choice; lock.unlock()
    }

    public func selected(sessionId: String) -> PersonalityChoice? {
        lock.lock(); defer { lock.unlock() }
        return selections[sessionId]
    }
}

// MARK: - Trust and compliance posture
//
// These are DECLARATIONS, not enforcement: the value of having them in code is
// that a claim on a website has one place it is written down, and a test can
// hold it to that.

public protocol IThirdPartySecurityAuditPublisher: Sendable { var reportUrl: String { get } }
public struct DefaultThirdPartySecurityAuditPublisher: IThirdPartySecurityAuditPublisher {
    public init() {}
    public var reportUrl: String { "https://trust.circle.ai/audit" }
}

public protocol IComplianceCertifications: Sendable { var certifications: [String] { get } }
public struct DefaultComplianceCertifications: IComplianceCertifications {
    public init() {}
    public var certifications: [String] { ["SOC 2 Type II", "ISO 27001", "ISO 27701"] }
}

public protocol IBugBountyChannel: Sendable {
    var platform: String { get }
    var submissionUrl: String { get }
}
public struct DefaultBugBountyChannel: IBugBountyChannel {
    public init() {}
    public var platform: String { "HackerOne" }
    public var submissionUrl: String { "https://h1.com/circleai" }
}

public protocol IPrivacyRegulationCompliance: Sendable { var laws: [String] { get } }
public struct DefaultPrivacyRegulationCompliance: IPrivacyRegulationCompliance {
    public init() {}
    public var laws: [String] { ["GDPR", "POPIA", "CCPA", "LGPD"] }
}

public protocol IVerifiablePrivacyProof: Sendable {
    var buildIsReproducible: Bool { get }
    var sourceUrl: String { get }
}
public struct DefaultVerifiablePrivacyProof: IVerifiablePrivacyProof {
    public init() {}
    public var buildIsReproducible: Bool { true }
    public var sourceUrl: String { "https://github.com/bhengubv/CircleAI" }
}

public protocol ILawfulInterceptCompliance: Sendable { var posture: String { get } }
public struct DefaultLawfulInterceptCompliance: ILawfulInterceptCompliance {
    public init() {}
    /// The whole posture in one line: money is auditable, conversation is not.
    public var posture: String { "Money decryptable to law, comms permanently blind" }
}

public protocol ISarbSandboxStatus: Sendable { var approved: Bool { get } }
public struct DefaultSarbSandboxStatus: ISarbSandboxStatus {
    public init() {}
    /// False on purpose. A default that claimed approval would be a lie in code.
    public var approved: Bool { false }
}

public protocol IIcasaApprovalStatus: Sendable { var approved: Bool { get } }
public struct DefaultIcasaApprovalStatus: IIcasaApprovalStatus {
    public init() {}
    public var approved: Bool { false }
}

public protocol IGlobalRegulatorEngagement: Sendable { var activeJurisdictions: [String] { get } }
public struct DefaultGlobalRegulatorEngagement: IGlobalRegulatorEngagement {
    public init() {}
    public var activeJurisdictions: [String] { ["ZA", "NG", "KE", "US", "CA", "UK", "EU"] }
}

public protocol ITaxInvoiceRegistry: Sendable { var schemes: [String] { get } }
public struct DefaultTaxInvoiceRegistry: ITaxInvoiceRegistry {
    public init() {}
    public var schemes: [String] { ["VAT", "GST", "Sales Tax", "DST"] }
}

public protocol IChildProtectionMode: Sendable {
    var coppaCompliant: Bool { get }
    var gdprKCompliant: Bool { get }
}
public struct DefaultChildProtectionMode: IChildProtectionMode {
    public init() {}
    public var coppaCompliant: Bool { true }
    public var gdprKCompliant: Bool { true }
}

public protocol IIndigenousDataSovereignty: Sendable { var standard: String { get } }
public struct DefaultIndigenousDataSovereignty: IIndigenousDataSovereignty {
    public init() {}
    public var standard: String { "CARE Principles" }
}

public protocol IIndigenousKnowledgeProtocols: Sendable {
    func requiresElderReview(isoLanguage: String) -> Bool
}
public struct DefaultIndigenousKnowledgeProtocols: IIndigenousKnowledgeProtocols {
    public init() {}
    /// ALWAYS true. Defaulting to no review for an unrecognised language is
    /// exactly the failure this exists to prevent.
    public func requiresElderReview(isoLanguage: String) -> Bool { true }
}

public protocol IReligiousAccommodation: Sendable { var supportedModes: [String] { get } }
public struct DefaultReligiousAccommodation: IReligiousAccommodation {
    public init() {}
    public var supportedModes: [String] { ["prayer times", "Shabbat mode", "Eid silence"] }
}

public protocol IThirdPartyHarmLiability: Sendable { var framework: String { get } }
public struct DefaultThirdPartyHarmLiability: IThirdPartyHarmLiability {
    public init() {}
    public var framework: String { "Operator-of-record indemnity backed by insurance pool" }
}

// MARK: - Money

public struct PricingTier: Sendable, Equatable {
    public let name: String
    public let monthlyPriceLocal: Decimal
    public let currency: String
    public let features: [String]
    public init(name: String, monthlyPriceLocal: Decimal, currency: String, features: [String]) {
        self.name = name
        self.monthlyPriceLocal = monthlyPriceLocal
        self.currency = currency
        self.features = features
    }
}

public protocol IPricingMatrix: Sendable { var all: [PricingTier] { get } }
public struct DefaultPricingMatrix: IPricingMatrix {
    public init() {}
    /// A free tier that actually works offline is the point - not a trial.
    public var all: [PricingTier] {
        [
            PricingTier(name: "free", monthlyPriceLocal: 0, currency: "ZAR",
                        features: ["Local chat", "Family memory cap"]),
            PricingTier(name: "paid", monthlyPriceLocal: 19, currency: "ZAR",
                        features: ["Unlimited cloud calls", "Priority routing"]),
            PricingTier(name: "family", monthlyPriceLocal: 49, currency: "ZAR",
                        features: ["Up to 6 members"]),
            PricingTier(name: "stokvel", monthlyPriceLocal: 99, currency: "ZAR",
                        features: ["Group memory", "Group reporting"]),
            PricingTier(name: "enterprise", monthlyPriceLocal: 200, currency: "ZAR",
                        features: ["Dedicated brain", "SLA"]),
        ]
    }
}

public protocol IPluginMarketplaceRevenueShare: Sendable {
    var authorShare: Double { get }
    var verifiedSafeShare: Double { get }
}
public struct DefaultPluginMarketplaceRevenueShare: IPluginMarketplaceRevenueShare {
    public init() {}
    public var authorShare: Double { 0.70 }
    public var verifiedSafeShare: Double { 0.50 }
}

public protocol ICarrierRevenueShare: Sendable { var carrierShare: Double { get } }
public struct DefaultCarrierRevenueShare: ICarrierRevenueShare {
    public init() {}
    public var carrierShare: Double { 0.25 }
}

public protocol ISustainablePerUserCostMath: Sendable {
    var monthlyRevenuePerUser: Decimal { get }
    var monthlyMarginalCostPerUser: Decimal { get }
}
public struct DefaultSustainablePerUserCostMath: ISustainablePerUserCostMath {
    public init() {}
    public var monthlyRevenuePerUser: Decimal { 19 }
    public var monthlyMarginalCostPerUser: Decimal { Decimal(string: "3.8")! }
}

public protocol IPerCallCostCeiling: Sendable { var ceilingUsd: Decimal { get } }
public struct DefaultPerCallCostCeiling: IPerCallCostCeiling {
    public init() {}
    public var ceilingUsd: Decimal { Decimal(string: "0.40")! }
}

public protocol IFreeTierCostCapping: Sendable { var monthlyCapUsd: Decimal { get } }
public struct DefaultFreeTierCostCapping: IFreeTierCostCapping {
    public init() {}
    public var monthlyCapUsd: Decimal { Decimal(string: "0.20")! }
}

public protocol ILocalFirstRouting: Sendable { var preferred: Bool { get } }
public struct DefaultLocalFirstRouting: ILocalFirstRouting {
    public init() {}
    /// On-device first, always. This is what keeps the free tier affordable.
    public var preferred: Bool { true }
}

public protocol IReferralProgramme: Sendable {
    var rewardLocal: Decimal { get }
    var currency: String { get }
}
public struct DefaultReferralProgramme: IReferralProgramme {
    public init() {}
    public var rewardLocal: Decimal { 19 }
    public var currency: String { "ZAR" }
}

public protocol IFamilyAiSharing: Sendable { var maxMembers: Int { get } }
public struct DefaultFamilyAiSharing: IFamilyAiSharing {
    public init() {}
    public var maxMembers: Int { 6 }
}

public protocol ICrossProviderFederation: Sendable { var enabled: Bool { get } }
public struct DefaultCrossProviderFederation: ICrossProviderFederation {
    public init() {}
    public var enabled: Bool { true }
}

public protocol IGroupNetworkEffects: Sendable { var groupTypes: [String] { get } }
public struct DefaultGroupNetworkEffects: IGroupNetworkEffects {
    public init() {}
    public var groupTypes: [String] { ["Stokvel", "Church", "Community"] }
}

public protocol IUserGrowthFlywheel: Sendable { var mechanic: String { get } }
public struct DefaultUserGrowthFlywheel: IUserGrowthFlywheel {
    public init() {}
    public var mechanic: String { "user invites friend; both get a month free" }
}

// MARK: - Formatting

public protocol ICurrencyFormatter: Sendable {
    func format(_ amount: Decimal, isoCurrencyCode: String) -> String
}

/// Amount then CODE, invariant. Deliberately not locale-aware: a symbol that
/// changes with the phone locale turns R into $ on somebody travelling.
public struct DefaultCurrencyFormatter: ICurrencyFormatter {
    public init() {}
    public func format(_ amount: Decimal, isoCurrencyCode: String) -> String {
        var input = amount
        var rounded = Decimal()
        NSDecimalRound(&rounded, &input, 2, .plain)
        let f = NumberFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.numberStyle = .decimal
        f.usesGroupingSeparator = false
        f.minimumFractionDigits = 2
        f.maximumFractionDigits = 2
        let body = f.string(from: rounded as NSDecimalNumber) ?? "0.00"
        return "\(body) \(isoCurrencyCode)"
    }
}

public protocol IPhoneNumberFormatter: Sendable {
    func format(e164: String, countryCodeIsoAlpha2: String) -> String
}

/// Returns E.164 UNCHANGED. Pretty-printing a number differs by country and
/// getting it wrong makes a number un-diallable; the host overrides this.
public struct DefaultPhoneNumberFormatter: IPhoneNumberFormatter {
    public init() {}
    public func format(e164: String, countryCodeIsoAlpha2: String) -> String { e164 }
}

// MARK: - Culture

public protocol ICulturalNameRecogniser: Sendable {
    func recognisesLanguage(_ isoLanguage: String) -> Bool
}
public struct DefaultCulturalNameRecogniser: ICulturalNameRecogniser {
    static let supported: Set<String> = ["zul", "xho", "tsn", "sot", "yor", "ibo",
                                         "twi", "swa", "hin", "ben"]
    public init() {}
    public func recognisesLanguage(_ isoLanguage: String) -> Bool {
        Self.supported.contains(isoLanguage.lowercased())
    }
}

public protocol ICulturalGreetings: Sendable {
    func greeting(for isoLanguage: String) -> String
}
public struct DefaultCulturalGreetings: ICulturalGreetings {
    public init() {}
    /// Both the three-letter and two-letter codes, because callers pass either.
    public func greeting(for isoLanguage: String) -> String {
        switch isoLanguage {
        case "zul", "zu": return "Sawubona"
        case "xho", "xh": return "Molo"
        case "yor": return "\u{1EB8}\u{0300} k\u{00FA} \u{00E1}\u{00E0}r\u{1ECD}\u{0300}"
        case "hin": return "\u{0928}\u{092E}\u{0938}\u{094D}\u{0924}\u{0947}"
        default: return "Hello"
        }
    }
}

public protocol ISaServiceConnectors: Sendable {
    var banks: [String] { get }
    var wallets: [String] { get }
}
public struct DefaultSaServiceConnectors: ISaServiceConnectors {
    public init() {}
    public var banks: [String] { ["Capitec", "FNB", "Standard", "Absa", "Nedbank"] }
    public var wallets: [String] { ["PayFast", "SnapScan"] }
}

public protocol ICrossBorderCorridors: Sendable { var corridors: [String] { get } }
public struct DefaultCrossBorderCorridors: ICrossBorderCorridors {
    public init() {}
    public var corridors: [String] { ["SADC", "ECOWAS", "EAC"] }
}

// MARK: - Small phones
//
// The floors are LOW on purpose. This has to run on the cheapest handset
// somebody can buy, not on a review unit.

public protocol ILowRamPhoneSupport: Sendable { func supportsRamMb(_ ramMb: Int) -> Bool }
public struct DefaultLowRamPhoneSupport: ILowRamPhoneSupport {
    public init() {}
    public func supportsRamMb(_ ramMb: Int) -> Bool { ramMb >= 512 }
}

public protocol ILowCpuOptimization: Sendable { func supportsClockMhz(_ clockMhz: Int) -> Bool }
public struct DefaultLowCpuOptimization: ILowCpuOptimization {
    public init() {}
    public func supportsClockMhz(_ clockMhz: Int) -> Bool { clockMhz >= 600 }
}

public protocol IKaiOsSupport: Sendable { var isCompiled: Bool { get } }
public struct DefaultKaiOsSupport: IKaiOsSupport {
    public init() {}
    /// A feature phone is still a phone, and KaiOS is what a lot of people have.
    public var isCompiled: Bool { true }
}

// MARK: - Working without a connection

public protocol IOfflineQueuedOperation: Sendable {
    func enqueue(_ operationJson: String) throws
    var pending: [String] { get }
    func tryDequeue() -> String?
}

/// FIFO, and it survives being read: dequeue removes, pending peeks. Anything
/// done while offline waits here rather than being lost.
public final class DefaultOfflineQueuedOperation: IOfflineQueuedOperation, @unchecked Sendable {
    private let lock = NSLock()
    private var q: [String] = []

    public init() {}

    public func enqueue(_ operationJson: String) throws {
        guard !operationJson.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw DistributionError.missingField("operationJson")
        }
        lock.lock(); q.append(operationJson); lock.unlock()
    }

    public var pending: [String] {
        lock.lock(); defer { lock.unlock() }
        return q
    }

    public func tryDequeue() -> String? {
        lock.lock(); defer { lock.unlock() }
        return q.isEmpty ? nil : q.removeFirst()
    }
}

public protocol IBrainUnreachableMode: Sendable { var localTakeoverEnabled: Bool { get } }
public struct DefaultBrainUnreachableMode: IBrainUnreachableMode {
    public init() {}
    public var localTakeoverEnabled: Bool { true }
}

public protocol INoInternetCacheTarget: Sendable { var hitRateTarget: Float { get } }
public struct DefaultNoInternetCacheTarget: INoInternetCacheTarget {
    public init() {}
    public var hitRateTarget: Float { 0.80 }
}

public protocol IStorageFullDegradationPolicy: Sendable { var degradeOrder: String { get } }
public struct DefaultStorageFullDegradationPolicy: IStorageFullDegradationPolicy {
    public init() {}
    /// Chat history is next to last and the app itself is never dropped: a
    /// full phone should lose caches, not the thing the person relies on.
    public var degradeOrder: String { "cache > old-snapshots > chat-history > nothing" }
}

public protocol IPublicDisasterMode: Sendable { var currentState: String { get } }
public struct DefaultPublicDisasterMode: IPublicDisasterMode {
    public init() {}
    public var currentState: String { "normal" }
}

// MARK: - Reaching people without the app

public protocol ISmsFallback: Sendable {
    func answerViaSms(phoneNumber: String, question: String) async throws
    var sent: [(phone: String, question: String, at: Date)] { get }
}

/// Answering over SMS is the floor: no data, no app, still an answer.
public final class DefaultSmsFallback: ISmsFallback, @unchecked Sendable {
    private let lock = NSLock()
    private var log: [(phone: String, question: String, at: Date)] = []
    private let delivery: (@Sendable (String, String) async throws -> Void)?

    public init(delivery: (@Sendable (String, String) async throws -> Void)? = nil) {
        self.delivery = delivery
    }

    public func answerViaSms(phoneNumber: String, question: String) async throws {
        guard !phoneNumber.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("phoneNumber")
        }
        guard !question.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("question")
        }
        // Recorded BEFORE delivery, so a failed send is still visible.
        lock.lock(); log.append((phoneNumber, question, Date())); lock.unlock()
        try await delivery?(phoneNumber, question)
    }

    public var sent: [(phone: String, question: String, at: Date)] {
        lock.lock(); defer { lock.unlock() }
        return log
    }
}

public protocol IUssdFallback: Sendable {
    func respond(ussdSession: String, input: String) throws -> String
}

/// A USSD menu tree. Works on a phone with no data and no smartphone at all -
/// the session id is the only state, because USSD has no other.
public final class DefaultUssdFallback: IUssdFallback, @unchecked Sendable {
    struct Menu { let prompt: String; let routes: [String: String] }

    static let menus: [String: Menu] = [
        "root": Menu(prompt: "CircleAI:\n1. Balance\n2. Ask AI\n3. Help",
                     routes: ["1": "balance", "2": "ask", "3": "help"]),
        "balance": Menu(prompt: "Balance: R0.00\n0. Back", routes: ["0": "root"]),
        "ask": Menu(prompt: "Type question, then send.\n0. Back", routes: ["0": "root"]),
        "help": Menu(prompt: "Dial *120*CIRCLE# anytime.\n0. Back", routes: ["0": "root"]),
    ]

    private let lock = NSLock()
    private var sessions: [String: String] = [:]

    public init() {}

    public func respond(ussdSession: String, input: String) throws -> String {
        guard !ussdSession.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("ussdSession")
        }
        lock.lock(); defer { lock.unlock() }

        let current = sessions[ussdSession] ?? "root"
        sessions[ussdSession] = current

        guard let menu = Self.menus[current] else {
            sessions[ussdSession] = "root"
            return Self.menus["root"]!.prompt
        }
        // An unrecognised key REDISPLAYS the menu rather than dropping the
        // session - a mistyped digit must not end the call.
        guard let next = menu.routes[input.trimmingCharacters(in: .whitespaces)] else {
            return menu.prompt
        }
        sessions[ussdSession] = next
        return Self.menus[next]!.prompt
    }
}

public protocol IWhatsAppIntegration: Sendable {
    func send(phoneNumber: String, message: String) async throws
    var outbox: [(phone: String, body: String, at: Date)] { get }
}

public final class DefaultWhatsAppIntegration: IWhatsAppIntegration, @unchecked Sendable {
    private let lock = NSLock()
    private var log: [(phone: String, body: String, at: Date)] = []
    private let send_: (@Sendable (String, String) async throws -> Void)?

    public init(send: (@Sendable (String, String) async throws -> Void)? = nil) {
        self.send_ = send
    }

    public func send(phoneNumber: String, message: String) async throws {
        guard !phoneNumber.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("phoneNumber")
        }
        guard !message.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("message")
        }
        // The number is VALIDATED here and not in the SMS path, matching the
        // C#: WhatsApp rejects a malformed number outright, SMS just fails.
        guard DistributionPhone.isE164(phoneNumber) else {
            throw DistributionError.invalidPhone(phoneNumber)
        }
        lock.lock(); log.append((phoneNumber, message, Date())); lock.unlock()
        try await send_?(phoneNumber, message)
    }

    public var outbox: [(phone: String, body: String, at: Date)] {
        lock.lock(); defer { lock.unlock() }
        return log
    }
}

public protocol ITelegramIntegration: Sendable {
    func send(chatId: String, message: String) async throws
    var outbox: [(chat: String, body: String, at: Date)] { get }
}

public final class DefaultTelegramIntegration: ITelegramIntegration, @unchecked Sendable {
    private let lock = NSLock()
    private var log: [(chat: String, body: String, at: Date)] = []
    private let send_: (@Sendable (String, String) async throws -> Void)?

    public init(send: (@Sendable (String, String) async throws -> Void)? = nil) {
        self.send_ = send
    }

    public func send(chatId: String, message: String) async throws {
        guard !chatId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("chatId")
        }
        guard !message.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("message")
        }
        lock.lock(); log.append((chatId, message, Date())); lock.unlock()
        try await send_?(chatId, message)
    }

    public var outbox: [(chat: String, body: String, at: Date)] {
        lock.lock(); defer { lock.unlock() }
        return log
    }
}

/// E.164: an optional plus, a non-zero leading digit, then 6 to 14 more.
enum DistributionPhone {
    static func isE164(_ s: String) -> Bool {
        var chars = Array(s)
        if chars.first == "+" { chars.removeFirst() }
        guard chars.count >= 7, chars.count <= 15 else { return false }
        guard let first = chars.first, first.isASCII, first.isNumber, first != "0" else { return false }
        return chars.allSatisfy { $0.isASCII && $0.isNumber }
    }
}

// MARK: - Connector registries

public protocol IEmailConnectorRegistry: Sendable { var providers: [String] { get } }
public struct DefaultEmailConnectorRegistry: IEmailConnectorRegistry {
    public init() {}
    /// IMAP last, and deliberately present: it is the escape hatch for every
    /// provider not on the list.
    public var providers: [String] {
        ["Gmail", "Outlook", "iCloud", "ProtonMail", "Yandex", "Yahoo", "IMAP"]
    }
}

public protocol ICalendarConnectorRegistry: Sendable { var providers: [String] { get } }
public struct DefaultCalendarConnectorRegistry: ICalendarConnectorRegistry {
    public init() {}
    public var providers: [String] { ["Google", "Outlook", "Apple", "Yahoo", "CalDAV"] }
}

public protocol ICrmConnectorRegistry: Sendable { var providers: [String] { get } }
public struct DefaultCrmConnectorRegistry: ICrmConnectorRegistry {
    public init() {}
    public var providers: [String] { ["HubSpot", "Salesforce", "Pipedrive", "Zoho", "Bitrix"] }
}

public protocol IAccountingConnectorRegistry: Sendable { var providers: [String] { get } }
public struct DefaultAccountingConnectorRegistry: IAccountingConnectorRegistry {
    public init() {}
    public var providers: [String] { ["Xero", "Sage", "QuickBooks", "Wave", "Manager.io"] }
}

public protocol IBankingConnectorRegistry: Sendable { var providers: [String] { get } }
public struct DefaultBankingConnectorRegistry: IBankingConnectorRegistry {
    public init() {}
    public var providers: [String] { ["open-banking-ZA", "open-banking-NG", "open-banking-KE"] }
}

// MARK: - Losing the phone, and losing the person

public protocol ILostDeviceFlow: Sendable {
    func remoteWipe(deviceId: String) throws
    func isWiped(deviceId: String) -> Bool
}

public final class DefaultLostDeviceFlow: ILostDeviceFlow, @unchecked Sendable {
    private let lock = NSLock()
    private var wiped: [String: Date] = [:]

    public init() {}

    public func remoteWipe(deviceId: String) throws {
        guard !deviceId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("deviceId")
        }
        lock.lock(); wiped[deviceId] = Date(); lock.unlock()
    }

    public func isWiped(deviceId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return wiped[deviceId] != nil
    }
}

public protocol IInheritanceProtocol: Sendable {
    func designate(ownerId: String, designeeId: String) throws
    func designee(for ownerId: String) -> String?
}

/// Who gets this when somebody dies. A person cannot designate themselves -
/// that would be an inheritance that never triggers.
public final class DefaultInheritanceProtocol: IInheritanceProtocol, @unchecked Sendable {
    private let lock = NSLock()
    private var designees: [String: String] = [:]

    public init() {}

    public func designate(ownerId: String, designeeId: String) throws {
        guard !ownerId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("ownerId")
        }
        guard !designeeId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("designeeId")
        }
        guard ownerId != designeeId else { throw DistributionError.designeeEqualsOwner }
        lock.lock(); designees[ownerId] = designeeId; lock.unlock()
    }

    public func designee(for ownerId: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        return designees[ownerId]
    }
}

public protocol IDataPortabilityExport: Sendable {
    func export(ownerId: String) throws -> Data
}

public struct DefaultDataPortabilityExport: IDataPortabilityExport {
    public init() {}
    /// A SCHEMA-STAMPED envelope, not the data. The host overrides this to
    /// stream the real memory, contacts and transcripts.
    public func export(ownerId: String) throws -> Data {
        guard !ownerId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("ownerId")
        }
        let bundle: [String: String] = [
            "owner_id": ownerId,
            "exported_at": ISO8601DateFormatter().string(from: Date()),
            "schema": "circleai/portability/v1",
            "note": "Host overrides export to stream actual user data (memory, contacts, transcripts).",
        ]
        return (try? JSONSerialization.data(withJSONObject: bundle, options: [.sortedKeys])) ?? Data()
    }
}

public protocol IAccountCompromiseRecovery: Sendable {
    func begin(ownerId: String) throws
    func inRecovery(ownerId: String) -> Bool
    func complete(ownerId: String)
}

public final class DefaultAccountCompromiseRecovery: IAccountCompromiseRecovery, @unchecked Sendable {
    private let lock = NSLock()
    private var active: [String: Date] = [:]

    public init() {}

    public func begin(ownerId: String) throws {
        guard !ownerId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("ownerId")
        }
        lock.lock(); active[ownerId] = Date(); lock.unlock()
    }

    public func inRecovery(ownerId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return active[ownerId] != nil
    }

    public func complete(ownerId: String) {
        lock.lock(); active.removeValue(forKey: ownerId); lock.unlock()
    }
}

// MARK: - Modes for people in trouble

public protocol IImpairedUserMode: Sendable {
    func engage(ownerId: String) throws
    func isEngaged(ownerId: String) -> Bool
    func disengage(ownerId: String)
}

public final class DefaultImpairedUserMode: IImpairedUserMode, @unchecked Sendable {
    private let lock = NSLock()
    private var engaged = Set<String>()

    public init() {}

    public func engage(ownerId: String) throws {
        guard !ownerId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("ownerId")
        }
        lock.lock(); engaged.insert(ownerId); lock.unlock()
    }

    public func isEngaged(ownerId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return engaged.contains(ownerId)
    }

    public func disengage(ownerId: String) {
        lock.lock(); engaged.remove(ownerId); lock.unlock()
    }
}

// NOTE: IAbusiveEnvironmentMode and DefaultAbusiveEnvironmentMode are already
// ported in Distribution.swift, including the FNV-1a derived safety phrase.
// Not duplicated here.

public protocol IQuietMode: Sendable {
    func engage(reason: String, duration: TimeInterval) throws
    func isQuiet(at moment: Date) -> Bool
    var activeWindows: [(reason: String, startedAt: Date, endsAt: Date)] { get }
}

/// Windows of deliberate silence - a funeral, an exam, a prayer time.
public final class DefaultQuietMode: IQuietMode, @unchecked Sendable {
    private let lock = NSLock()
    private var windows: [(reason: String, startedAt: Date, endsAt: Date)] = []

    public init() {}

    public func engage(reason: String, duration: TimeInterval) throws {
        guard !reason.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("reason")
        }
        // A zero or negative window would be silence that never happens.
        guard duration > 0 else { throw DistributionError.nonPositiveDuration }
        let now = Date()
        lock.lock(); windows.append((reason, now, now.addingTimeInterval(duration))); lock.unlock()
    }

    /// INCLUSIVE at both ends: a moment exactly on the boundary is quiet.
    public func isQuiet(at moment: Date) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return windows.contains { moment >= $0.startedAt && moment <= $0.endsAt }
    }

    public var activeWindows: [(reason: String, startedAt: Date, endsAt: Date)] {
        let now = Date()
        lock.lock(); defer { lock.unlock() }
        return windows.filter { $0.endsAt >= now }
    }
}

// MARK: - Transparency

public protocol IPublicTransparency: Sendable {
    func linkEvidence(claim: String, evidenceUrl: String) throws
    var linked: [(claim: String, evidence: String, at: Date)] { get }
}

/// Every public claim gets a link to the thing that proves it.
public final class DefaultPublicTransparency: IPublicTransparency, @unchecked Sendable {
    private let lock = NSLock()
    private var links: [(claim: String, evidence: String, at: Date)] = []

    public init() {}

    public func linkEvidence(claim: String, evidenceUrl: String) throws {
        guard !claim.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw DistributionError.missingField("claim")
        }
        // An absolute http(s) URL only. A relative path is not evidence
        // anybody outside the app can follow.
        guard let u = URL(string: evidenceUrl),
              let scheme = u.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              u.host != nil else {
            throw DistributionError.invalidEvidenceUrl
        }
        lock.lock(); links.append((claim, evidenceUrl, Date())); lock.unlock()
    }

    public var linked: [(claim: String, evidence: String, at: Date)] {
        lock.lock(); defer { lock.unlock() }
        return links
    }
}
