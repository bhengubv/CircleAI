// SurfaceHosts.swift
//
// The three small hosts that put a companion on a surface, plus the in-memory
// identity store and the null media library.
//
// Ported from src/CircleAI.Web/WebCompanionService.cs,
// src/CircleAI.IoT/IoTCompanionPipeline.cs,
// src/CircleAI.Identity/InMemoryIdentityStore.cs and
// src/CircleAI.MediaHub/NullImplementations.cs.

import Foundation

// MARK: - Web

public enum WebCompanionError: Error, Equatable, CustomStringConvertible {
    case notInitialised

    public var description: String {
        "The web companion has no session yet — call initialise first."
    }
}

/// One companion session, scoped to a browser session.
///
/// The session is created ONCE and reused. A surface that builds a new session
/// per request starts every message with an empty conversation, which reads as
/// an assistant with no memory rather than as a wiring mistake.
public final class WebCompanionService: @unchecked Sendable {

    private let factory: any ICompanionSessionFactory
    private let lock = NSLock()
    private var current: ICompanionSession?

    public init(factory: any ICompanionSessionFactory) {
        self.factory = factory
    }

    /// The live session, or a NAMED failure. Deliberately not optional: every
    /// caller would have to unwrap it, and the nil case has exactly one cause
    /// worth stating out loud.
    public func session() throws -> ICompanionSession {
        lock.lock(); defer { lock.unlock() }
        guard let current else { throw WebCompanionError.notInitialised }
        return current
    }

    public var isInitialised: Bool {
        lock.lock(); defer { lock.unlock() }
        return current != nil
    }

    /// Idempotent: initialising twice keeps the first session rather than
    /// replacing it, so a page that re-runs its setup does not silently discard
    /// the conversation so far.
    public func initialise(identityId: String) async throws {
        if isInitialised { return }
        let created = try await factory.create(identityId: identityId, interface: .web)

        lock.lock()
        if current == nil { current = created }
        lock.unlock()
    }

    public func close() async {
        lock.lock()
        let session = current
        current = nil
        lock.unlock()
        _ = session
    }
}

// MARK: - IoT

/// Voice in, voice out, on a device with a microphone and a speaker and not
/// much else.
///
/// NOTHING HERE IS ALLOWED TO THROW OUTWARD. An embedded device has no screen
/// to show an error on and nobody standing next to it: a pipeline that dies on
/// a bad utterance is a speaker that stops working until somebody power-cycles
/// it. Failures are reported through `onFaulted` and the loop carries on.
public final class IoTCompanionPipeline: @unchecked Sendable {

    private let session: any ICompanionSession
    private let ears: VoicePipeline
    private let tts: (any ITtsEngine)?

    private let lock = NSLock()
    private var disposed = false

    /// A reply has been synthesised and is ready to play.
    public var onAudioReady: (@Sendable (TtsSynthesisResult) -> Void)?

    /// Something failed. Surfaced rather than thrown, because there is nowhere
    /// for a throw to go on a device like this.
    public var onFaulted: (@Sendable (Error) -> Void)?

    public init(session: any ICompanionSession,
                wakeWord: any IWakeWordDetector,
                transcriber: any IVoiceTranscriber,
                audioCapture: (any IAudioCapture)? = nil,
                tts: (any ITtsEngine)? = nil) {
        self.session = session
        self.tts = tts
        self.ears = VoicePipeline(wake: wakeWord, transcriber: transcriber,
                                  capture: audioCapture, tts: tts)

        self.ears.onTranscribed = { [weak self] event in
            guard let self else { return }
            // Handed to a task rather than handled inline: the callback is on
            // the pipeline's own path, and awaiting a model there stops it
            // hearing anything else.
            Task { await self.handle(utterance: event.result.text) }
        }
    }

    public func start() async throws { try await ears.start() }

    public func stop() async throws { try await ears.stop() }

    public func close() async {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        lock.unlock()

        ears.onTranscribed = nil
        try? await ears.stop()
    }

    func handle(utterance: String) async {
        guard !utterance.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        do {
            let reply = try await session.send(utterance)
            if let tts, !reply.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                onAudioReady?(try await tts.synthesise(text: reply))
            }
        } catch {
            onFaulted?(error)
        }
    }
}

// MARK: - Identity

/// Identities and their devices, in memory.
public final class InMemoryIdentityStore: IIdentityStore, @unchecked Sendable {

    private let lock = NSLock()
    private var identities: [String: CircleIdentity] = [:]
    private var devices: [String: RegisteredDevice] = [:]

    public init() {}

    public func get(identityId: String) async throws -> CircleIdentity? {
        lock.lock(); defer { lock.unlock() }
        return identities[identityId]
    }

    public func save(_ identity: CircleIdentity) async throws {
        lock.lock(); identities[identity.identityId] = identity; lock.unlock()
    }

    public func getDevices(identityId: String) async throws -> [RegisteredDevice] {
        lock.lock(); defer { lock.unlock() }
        // Sorted by device id so the list does not reorder between calls. A
        // "your devices" screen that shuffles on every refresh looks broken even
        // though nothing changed.
        return devices.values
            .filter { $0.identityId == identityId }
            .sorted { $0.deviceId < $1.deviceId }
    }

    public func registerDevice(_ device: RegisteredDevice) async throws {
        lock.lock(); devices[device.deviceId] = device; lock.unlock()
    }

    /// The reverse lookup: which identity owns this device.
    ///
    /// A device registered to an identity that was never saved returns nil
    /// rather than a half-built identity — the device row exists, the person
    /// does not, and pretending otherwise puts an empty name on a screen.
    public func getByDevice(deviceId: String) async throws -> CircleIdentity? {
        lock.lock(); defer { lock.unlock() }
        guard let device = devices[deviceId] else { return nil }
        return identities[device.identityId]
    }
}

// MARK: - MediaHub

/// A library with nothing in it.
public final class NullMediaLibrary: IHubMediaLibrary, @unchecked Sendable {
    public static let instance = NullMediaLibrary()
    public init() {}

    public var backendId: String { "null" }
    public func get(_ id: String) async throws -> MediaItem? { nil }
    public func search(_ query: String, topK: Int) async throws -> [MediaItem] { [] }
}
