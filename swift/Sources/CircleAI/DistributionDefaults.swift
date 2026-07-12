// DistributionDefaults.swift
//
// Port of src/CircleAI.Distribution/UbiquityRailsMissingDefaults.cs — the
// Default* rails that had no non-Default implementation ported yet, plus the
// contracts + DTOs they need (from UbiquityRails.cs, ONBOARDING/TRUST sections).
//
// Already ported in Distribution.swift (NOT redeclared here):
//   • AppStorePackage, IAppStoreSubmitter, InMemoryAppStoreSubmitter
//   • DeltaUpdate, ISignedDeltaUpdater, InMemorySignedDeltaUpdater
//   • OEM/carrier preload catalogues, DefaultAbusiveEnvironmentMode
//
// This file adds:
//   • DefaultAppStoreSubmitter          (validating, store-allowlist)
//   • DefaultSignedDeltaUpdater         (HMAC-SHA256 verify + channel version)
//   • DefaultPhonePinBiometricOnboarding (E.164 + PIN-hash sessions)
//   • DefaultNoManualFirstRun           (single welcome card)
//   • DefaultVoiceLedSetup              (mother-tongue allowlist)
//   • DefaultPersonalDataImport         (registered-source recorder)
//   • DefaultFamilyOnboarding           (household roster + role validation)
//   • DefaultPerCallTransparency        (receipt store + Record)
//   • DefaultVerifiableWipe             (SHA-256 wipe certificate)
// and the DTOs/protocols: OnboardingSession, HouseholdMember,
// TransparencyReceipt, and the I* contracts above.
//
// Porting notes:
//   • `ValueTask`/`ValueTask<T>` → `async` / `async -> T`.
//   • `ConcurrentDictionary` → `[String: …]` guarded by an `NSLock`.
//   • `byte[]` → `Data`; `decimal` → `Decimal`.
//   • `HMACSHA256` / `SHA256` / `CryptographicOperations.FixedTimeEquals`
//     → CryptoKit `HMAC<SHA256>` / `SHA256` + a local constant-time compare.
//   • `TimeSpan` → `TimeInterval` (seconds).
//   • Validation errors surface as `ModelRuntimeError` (already the module-wide
//     error) rather than a new enum, matching the other ported rails.

import Foundation
import CryptoKit

// MARK: - DTOs (from UbiquityRails.cs ONBOARDING / TRUST)

/// (C# `OnboardingSession`.) A phone-pin+biometric onboarding session.
/// `timeToActive` is seconds (C# `TimeSpan`).
public struct OnboardingSession: Sendable, Equatable {
    public let sessionId: String
    public let phoneNumber: String
    public let biometricEnrolled: Bool
    public let timeToActive: TimeInterval

    public init(
        sessionId: String,
        phoneNumber: String,
        biometricEnrolled: Bool,
        timeToActive: TimeInterval
    ) {
        self.sessionId = sessionId
        self.phoneNumber = phoneNumber
        self.biometricEnrolled = biometricEnrolled
        self.timeToActive = timeToActive
    }
}

/// (C# `HouseholdMember`.) One member of a family household.
public struct HouseholdMember: Sendable, Equatable {
    public let memberId: String
    public let displayName: String
    public let role: String

    public init(memberId: String, displayName: String, role: String) {
        self.memberId = memberId
        self.displayName = displayName
        self.role = role
    }
}

/// (C# `TransparencyReceipt`.) A per-call transparency receipt: what the AI did,
/// what data left the device, and what it cost. `costUsd` is `Decimal`
/// (C# `decimal`).
public struct TransparencyReceipt: Sendable, Equatable {
    public let callId: String
    public let actionsTaken: [String]
    public let dataEgress: [String]
    public let costUsd: Decimal

    public init(callId: String, actionsTaken: [String], dataEgress: [String], costUsd: Decimal) {
        self.callId = callId
        self.actionsTaken = actionsTaken
        self.dataEgress = dataEgress
        self.costUsd = costUsd
    }
}

// MARK: - Contracts (from UbiquityRails.cs)

/// (C# `IPhonePinBiometricOnboarding`.)
public protocol IPhonePinBiometricOnboarding: Sendable {
    func start(phoneNumber: String) async throws -> OnboardingSession
    func complete(sessionId: String, pin: String, biometricOk: Bool) async throws
}

/// (C# `INoManualFirstRun`.)
public protocol INoManualFirstRun: Sendable {
    func show() async -> String
}

/// (C# `IVoiceLedSetup`.) Mother-tongue voice-led setup.
public protocol IVoiceLedSetup: Sendable {
    func run(motherTongue: String) async throws -> Bool
}

/// (C# `IPersonalDataImport`.)
public protocol IPersonalDataImport: Sendable {
    func importData(sessionId: String, source: String) async throws
}

/// (C# `IFamilyOnboarding`.)
public protocol IFamilyOnboarding: Sendable {
    func createHousehold(ownerId: String, members: [HouseholdMember]) async throws
}

/// (C# `IPerCallTransparency`.) The C# interface only requires `ReceiptFor`; the
/// default implementation additionally exposes `Record`.
public protocol IPerCallTransparency: Sendable {
    func receiptFor(callId: String) async throws -> TransparencyReceipt
}

/// (C# `IVerifiableWipe`.) Wipe on-device data and return a verifiable
/// certificate.
public protocol IVerifiableWipe: Sendable {
    func wipeAndCertify(ownerId: String) async throws -> Data
}

// MARK: - DefaultAppStoreSubmitter

/// (3.3.0) Default app-store submitter — validates the package and records the
/// submission. (C# `DefaultAppStoreSubmitter`.) Distinct from the pre-existing
/// `InMemoryAppStoreSubmitter`: this one enforces the known-store allowlist and
/// keys submissions by `store/version`.
public final class DefaultAppStoreSubmitter: IAppStoreSubmitter, @unchecked Sendable {
    private static let knownStores: Set<String> = [
        "playstore", "appstore", "galaxy store", "huawei appgallery",
        "microsoft store", "f-droid",
    ]
    private let lock = NSLock()
    private var submitted: [String: AppStorePackage] = [:]

    public init() {}

    public func submit(_ package: AppStorePackage) async -> Bool {
        // C# throws ArgumentException for blank required fields. `submit` is
        // non-throwing in the Swift `IAppStoreSubmitter` contract, so a blank
        // required field is treated as a rejected (false) submission.
        if package.storeName.isBlank || package.packagePath.isBlank || package.version.isBlank {
            return false
        }
        if !Self.knownStores.contains(package.storeName.lowercased()) { return false }
        let key = "\(package.storeName)/\(package.version)"
        lock.lock(); submitted[key] = package; lock.unlock()
        return true
    }

    /// Snapshot of all accepted submissions.
    public var allSubmitted: [AppStorePackage] {
        lock.lock(); defer { lock.unlock() }
        return Array(submitted.values)
    }
}

// MARK: - DefaultSignedDeltaUpdater

/// (3.3.0) Signed delta updater — verifies HMAC-SHA256 signature before
/// applying. (C# `DefaultSignedDeltaUpdater`.) The HMAC is computed over
/// `Channel|FromVersion|ToVersion|` (UTF-8) concatenated with the raw payload
/// bytes, byte-identical to the C# writer.
public final class DefaultSignedDeltaUpdater: ISignedDeltaUpdater, @unchecked Sendable {
    private let hmacKey: SymmetricKey
    private let lock = NSLock()
    private var channelVersion: [String: String] = [:]

    /// - Parameter hmacKey: the shared HMAC key. Must be at least 16 bytes.
    public init(hmacKey: Data) {
        precondition(hmacKey.count >= 16, "hmacKey must be at least 16 bytes")
        self.hmacKey = SymmetricKey(data: hmacKey)
    }

    public func apply(_ update: DeltaUpdate) async -> Bool {
        if update.channel.isBlank || update.toVersion.isBlank { return false }

        lock.lock()
        if let current = channelVersion[update.channel], current != update.fromVersion {
            lock.unlock()
            return false
        }
        lock.unlock()

        // HMAC over Channel|FromVersion|ToVersion|Payload.
        var msg = Data("\(update.channel)|\(update.fromVersion)|\(update.toVersion)|".utf8)
        msg.append(update.payload)
        let expected = Data(HMAC<SHA256>.authenticationCode(for: msg, using: hmacKey))
        guard Self.fixedTimeEquals(expected, update.signature) else { return false }

        lock.lock(); channelVersion[update.channel] = update.toVersion; lock.unlock()
        return true
    }

    /// Current applied version for `channel`, or nil if none applied yet.
    public func currentVersion(_ channel: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        return channelVersion[channel]
    }

    /// Constant-time byte comparison (mirrors
    /// `CryptographicOperations.FixedTimeEquals`).
    static func fixedTimeEquals(_ a: Data, _ b: Data) -> Bool {
        if a.count != b.count { return false }
        var diff: UInt8 = 0
        let ba = Array(a), bb = Array(b)
        for i in 0..<ba.count { diff |= ba[i] ^ bb[i] }
        return diff == 0
    }
}

// MARK: - DefaultPhonePinBiometricOnboarding

/// (3.3.0) Phone-pin biometric onboarding — real session tracking with PIN
/// strength + biometric flag. (C# `DefaultPhonePinBiometricOnboarding`.)
public final class DefaultPhonePinBiometricOnboarding: IPhonePinBiometricOnboarding, @unchecked Sendable {
    // ^\+?[1-9]\d{6,14}$  — E.164 with optional leading '+'.
    private static let e164 = try! NSRegularExpression(pattern: "^\\+?[1-9]\\d{6,14}$")

    private let lock = NSLock()
    private var sessions: [String: OnboardingSession] = [:]
    private var pinHashes: [String: String] = [:]

    public init() {}

    public func start(phoneNumber: String) async throws -> OnboardingSession {
        if phoneNumber.isBlank { throw ModelRuntimeError.argument("phoneNumber required") }
        let range = NSRange(phoneNumber.startIndex..<phoneNumber.endIndex, in: phoneNumber)
        if Self.e164.firstMatch(in: phoneNumber, range: range) == nil {
            throw ModelRuntimeError.argument("Invalid E.164 phone '\(phoneNumber)'.")
        }
        let sid = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let session = OnboardingSession(
            sessionId: sid, phoneNumber: phoneNumber,
            biometricEnrolled: false, timeToActive: 0)
        lock.lock(); sessions[sid] = session; lock.unlock()
        return session
    }

    public func complete(sessionId: String, pin: String, biometricOk: Bool) async throws {
        if sessionId.isBlank { throw ModelRuntimeError.argument("sessionId required") }
        if pin.isEmpty || pin.count < 4 || !pin.allSatisfy({ $0.isNumber }) {
            throw ModelRuntimeError.argument("PIN must be at least 4 digits")
        }
        lock.lock(); defer { lock.unlock() }
        guard let s = sessions[sessionId] else {
            throw ModelRuntimeError.invalidOperation("Unknown session \(sessionId)")
        }
        // C# uses a 1-minute placeholder for actual elapsed time; mirror it.
        let elapsed: TimeInterval = 60
        pinHashes[s.phoneNumber] = Self.pinHash(pin: pin, phone: s.phoneNumber)
        sessions[sessionId] = OnboardingSession(
            sessionId: s.sessionId, phoneNumber: s.phoneNumber,
            biometricEnrolled: biometricOk, timeToActive: elapsed)
    }

    /// Verify a stored PIN for a phone number (constant-time over the hex hash).
    public func verifyPin(phoneNumber: String, pin: String) -> Bool {
        lock.lock(); let stored = pinHashes[phoneNumber]; lock.unlock()
        guard let stored = stored else { return false }
        let candidate = Self.pinHash(pin: pin, phone: phoneNumber)
        return Self.fixedTimeEquals(Data(stored.utf8), Data(candidate.utf8))
    }

    /// SHA-256 over `pin + phone`, upper-hex (mirrors C# `Convert.ToHexString`).
    private static func pinHash(pin: String, phone: String) -> String {
        let digest = SHA256.hash(data: Data((pin + phone).utf8))
        return digest.map { String(format: "%02X", $0) }.joined()
    }

    static func fixedTimeEquals(_ a: Data, _ b: Data) -> Bool {
        if a.count != b.count { return false }
        var diff: UInt8 = 0
        let ba = Array(a), bb = Array(b)
        for i in 0..<ba.count { diff |= ba[i] ^ bb[i] }
        return diff == 0
    }
}

// MARK: - DefaultNoManualFirstRun

/// (3.3.0) No-manual first-run — shows a single welcome card.
/// (C# `DefaultNoManualFirstRun`.)
public final class DefaultNoManualFirstRun: INoManualFirstRun, @unchecked Sendable {
    private let welcome: String

    public init(welcomeCard: String? = nil) {
        self.welcome = welcomeCard
            ?? "Welcome to Circle AI. Tap the mic and say hello — that's it."
    }

    public func show() async -> String { welcome }
}

// MARK: - DefaultVoiceLedSetup

/// (3.3.0) Voice-led setup — accepts supported mother tongues; rejects unknown
/// ones. (C# `DefaultVoiceLedSetup`.) The BCP-47 primary subtag (before the
/// first '-') is matched case-insensitively against the supported set.
public final class DefaultVoiceLedSetup: IVoiceLedSetup, @unchecked Sendable {
    private static let supported: Set<String> = [
        "en", "af", "zu", "xh", "st", "tn", "ts", "ss", "ve", "nr", "nso",  // SA official
        "sw", "ha", "yo", "ig", "am", "fr", "pt", "ar", "hi", "bn", "es",   // continent + global
    ]

    public init() {}

    public func run(motherTongue: String) async throws -> Bool {
        if motherTongue.isBlank { throw ModelRuntimeError.argument("motherTongue required") }
        let prefix = motherTongue.split(separator: "-", maxSplits: 1,
                                        omittingEmptySubsequences: false)[0]
        return Self.supported.contains(prefix.lowercased())
    }
}

// MARK: - DefaultPersonalDataImport

/// (3.3.0) Personal data import — accepts a registered source name; records the
/// import. (C# `DefaultPersonalDataImport`.)
public final class DefaultPersonalDataImport: IPersonalDataImport, @unchecked Sendable {
    private static let knownSources: Set<String> = [
        "google-takeout", "apple-data-export", "whatsapp-archive",
        "icloud", "csv", "vcard", "ics",
    ]
    private let lock = NSLock()
    private var imports: [String: [String]] = [:]

    public init() {}

    public func importData(sessionId: String, source: String) async throws {
        if sessionId.isBlank { throw ModelRuntimeError.argument("sessionId required") }
        if source.isBlank { throw ModelRuntimeError.argument("source required") }
        if !Self.knownSources.contains(source.lowercased()) {
            throw ModelRuntimeError.invalidOperation("Unsupported import source '\(source)'.")
        }
        lock.lock(); imports[sessionId, default: []].append(source); lock.unlock()
    }

    /// Sources imported for `sessionId`, in insertion order.
    public func importsFor(_ sessionId: String) -> [String] {
        lock.lock(); defer { lock.unlock() }
        return imports[sessionId] ?? []
    }
}

// MARK: - DefaultFamilyOnboarding

/// (3.3.0) Family onboarding — household + member roster with role validation.
/// (C# `DefaultFamilyOnboarding`.)
public final class DefaultFamilyOnboarding: IFamilyOnboarding, @unchecked Sendable {
    private static let validRoles: Set<String> = [
        "owner", "parent", "child", "guardian", "elder", "partner", "guest",
    ]
    private let lock = NSLock()
    private var households: [String: [HouseholdMember]] = [:]

    public init() {}

    public func createHousehold(ownerId: String, members: [HouseholdMember]) async throws {
        if ownerId.isBlank { throw ModelRuntimeError.argument("ownerId required") }
        for m in members {
            if m.memberId.isBlank { throw ModelRuntimeError.argument("MemberId required") }
            if m.displayName.isBlank { throw ModelRuntimeError.argument("DisplayName required") }
            if !Self.validRoles.contains(m.role.lowercased()) {
                throw ModelRuntimeError.invalidOperation("Unknown role '\(m.role)'.")
            }
        }
        lock.lock(); households[ownerId] = members; lock.unlock()
    }

    /// Members of `ownerId`'s household, or empty if none.
    public func membersOf(_ ownerId: String) -> [HouseholdMember] {
        lock.lock(); defer { lock.unlock() }
        return households[ownerId] ?? []
    }
}

// MARK: - DefaultPerCallTransparency

/// (3.3.0) Per-call transparency receipt — real receipt store with a `record`
/// action. (C# `DefaultPerCallTransparency`.) When no receipt exists for a
/// `callId`, an empty (zero-cost) receipt is returned rather than throwing.
public final class DefaultPerCallTransparency: IPerCallTransparency, @unchecked Sendable {
    private let lock = NSLock()
    private var receipts: [String: TransparencyReceipt] = [:]

    public init() {}

    public func record(_ receipt: TransparencyReceipt) throws {
        if receipt.callId.isBlank { throw ModelRuntimeError.argument("CallId required") }
        lock.lock(); receipts[receipt.callId] = receipt; lock.unlock()
    }

    public func receiptFor(callId: String) async throws -> TransparencyReceipt {
        if callId.isBlank { throw ModelRuntimeError.argument("callId required") }
        lock.lock(); let r = receipts[callId]; lock.unlock()
        return r ?? TransparencyReceipt(callId: callId, actionsTaken: [], dataEgress: [], costUsd: 0)
    }
}

// MARK: - DefaultVerifiableWipe

/// (3.3.0) Verifiable wipe — returns a SHA-256 certificate over
/// `wipe|ownerId|iso-timestamp|nonce`. (C# `DefaultVerifiableWipe`.) The nonce
/// is 16 random bytes, base64-encoded, so each certificate is unique.
public final class DefaultVerifiableWipe: IVerifiableWipe, @unchecked Sendable {
    public init() {}

    public func wipeAndCertify(ownerId: String) async throws -> Data {
        if ownerId.isBlank { throw ModelRuntimeError.argument("ownerId required") }
        // Certificate = SHA-256 over "wipe|ownerId|iso-timestamp|nonce".
        var nonce = Data(count: 16)
        for i in 0..<16 { nonce[i] = UInt8.random(in: 0...255) }
        let timestamp = Self.iso8601Round.string(from: Date())
        let payload = "wipe|\(ownerId)|\(timestamp)|\(nonce.base64EncodedString())"
        return Data(SHA256.hash(data: Data(payload.utf8)))
    }

    /// ISO-8601 with fractional seconds + zone, matching .NET's round-trip "O"
    /// format closely enough for a nonce'd, non-reproducible certificate.
    private static let iso8601Round: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
}
