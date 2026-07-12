// TelephonyRecording.swift
//
// Port of the CircleAI.Telephony media-side + provisioning-orchestration layer:
//   • StereoCallRecorder.cs      — StereoCallRecorder (+ IRecorderSink, the
//                                  injected seekable byte sink standing in for
//                                  System.IO.Stream).
//   • HoldMusicMixer.cs          — HoldMusicMixer.
//   • PhoneNumberProvisioner.cs  — IProvisionedNumberStore,
//                                  InMemoryProvisionedNumberStore,
//                                  PhoneNumberProvisioner.
//
// CONVENTIONS:
//   • C# `System.IO.Stream` (write + optional seek) → an injected `IRecorderSink`
//     protocol; the default `InMemoryRecorderSink` is a growable byte buffer that
//     supports the backfill Finalize needs. This keeps the port in-memory and off
//     the filesystem while preserving the exact WAV byte layout.
//   • `BinaryPrimitives.Write{Int16,Int32}LittleEndian` → explicit little-endian
//     byte packing (Swift is not guaranteed LE; we pack bytes by hand to match
//     the wire format on every platform).
//   • `short`/`Int16` sample math uses `Int` intermediates + `Math.Clamp` →
//     Swift `Int` with `min/max` clamping, then `Int16(truncatingIfNeeded:)` on
//     the already-clamped value (no overflow trap).
//   • `Microsoft.Extensions.Logging.ILogger` → an optional `@Sendable (String) -> Void`
//     log hook (defaults to a no-op). No logging framework dependency.
//   • `ValueTask` store methods → `async` (the in-memory store completes
//     synchronously; the async signature preserves the contract).

import Foundation

// =====================================================================
// StereoCallRecorder.cs
// =====================================================================

/// A seekable byte sink the recorder writes the WAV stream into. Models the
/// subset of `System.IO.Stream` `StereoCallRecorder` uses: append bytes, report
/// whether seeking is supported, and backfill the 44-byte header at position 0.
///
/// `canSeek == false` reproduces the C# "streams that can't seek can't backfill"
/// branch — the placeholder header is left in place for live appends.
public protocol IRecorderSink: AnyObject, Sendable {
    /// True if `overwrite(at:_:)` is supported (mirrors `Stream.CanSeek`).
    var canSeek: Bool { get }
    /// Append bytes at the current end (mirrors `Stream.Write`).
    func append(_ bytes: [UInt8])
    /// Overwrite `bytes` starting at absolute `offset` (mirrors seek + write).
    /// Only called when `canSeek` is true.
    func overwrite(at offset: Int, _ bytes: [UInt8])
}

/// Default in-memory seekable sink. Thread-safety is provided by the recorder's
/// own lock (the recorder never calls the sink concurrently), so this type keeps
/// no lock of its own.
public final class InMemoryRecorderSink: IRecorderSink, @unchecked Sendable {
    private var storage: [UInt8] = []

    public init() {}

    public var canSeek: Bool { true }

    public func append(_ bytes: [UInt8]) {
        storage.append(contentsOf: bytes)
    }

    public func overwrite(at offset: Int, _ bytes: [UInt8]) {
        for (i, b) in bytes.enumerated() {
            let idx = offset + i
            if idx < storage.count {
                storage[idx] = b
            } else {
                // Shouldn't happen for the header backfill (44 bytes reserved up
                // front), but grow defensively rather than trap.
                storage.append(b)
            }
        }
    }

    /// The full WAV byte stream captured so far (snapshot copy).
    public var data: Data { Data(storage) }
}

/// Records a call to a byte sink as a stereo PCM-16 WAV. Left channel = caller,
/// right = agent. Port of `CircleAI.Telephony.StereoCallRecorder`.
///
/// `IAsyncDisposable`/`IDisposable` → `finalizeRecording()` (idempotent) plus an
/// explicit `dispose()`; there is no OS handle to release, so `leaveOpen` only
/// governs whether `dispose()` is a no-op beyond finalising.
public final class StereoCallRecorder: @unchecked Sendable {
    private let output: IRecorderSink
    private let sampleRateHz: Int
    private let gate = NSLock()
    private var samplesWritten: Int64 = 0   // total interleaved sample pairs
    private var headerWritten = false
    private var finalized = false

    public init(output: IRecorderSink, sampleRateHz: Int) {
        precondition(sampleRateHz > 0, "sampleRateHz must be positive")
        self.output = output
        self.sampleRateHz = sampleRateHz
    }

    /// Write inbound (caller) PCM-16 mono audio. Caller side is the left channel.
    public func writeCallerFrame(_ pcmFrame: [UInt8]) {
        writeSide(pcmFrame, isCaller: true)
    }

    /// Write outbound (agent) PCM-16 mono audio. Agent side is the right channel.
    public func writeAgentFrame(_ pcmFrame: [UInt8]) {
        writeSide(pcmFrame, isCaller: false)
    }

    /// Finalise the WAV header. After this, no more writes take effect.
    public func finalizeRecording() {
        gate.lock(); defer { gate.unlock() }
        finaliseLocked()
    }

    private func writeSide(_ pcmFrame: [UInt8], isCaller: Bool) {
        if pcmFrame.count < 2 { return }
        gate.lock(); defer { gate.unlock() }
        if finalized { return }
        ensureHeader()
        let samples = pcmFrame.count / 2
        for i in 0..<samples {
            let mono = Self.readInt16LE(pcmFrame, at: i * 2)
            var stereo = [UInt8](repeating: 0, count: 4)
            if isCaller {
                Self.writeInt16LE(&stereo, at: 0, mono)
                Self.writeInt16LE(&stereo, at: 2, 0)
            } else {
                Self.writeInt16LE(&stereo, at: 0, 0)
                Self.writeInt16LE(&stereo, at: 2, mono)
            }
            output.append(stereo)
            samplesWritten += 1
        }
    }

    private func ensureHeader() {
        if headerWritten { return }
        // Reserve 44 bytes for the WAV header — backfilled in finalise.
        output.append([UInt8](repeating: 0, count: 44))
        headerWritten = true
    }

    private func finaliseLocked() {
        if finalized { return }
        if !headerWritten { finalized = true; return }
        let dataSize = samplesWritten * 4        // 2 channels × 2 bytes
        let chunkSize = 36 + dataSize
        finalized = true
        if !output.canSeek {
            // Can't backfill — accept the placeholder header for live appends.
            return
        }
        var header = [UInt8](repeating: 0, count: 44)
        header[0] = UInt8(ascii: "R"); header[1] = UInt8(ascii: "I"); header[2] = UInt8(ascii: "F"); header[3] = UInt8(ascii: "F")
        Self.writeInt32LE(&header, at: 4, Int32(truncatingIfNeeded: chunkSize))
        header[8] = UInt8(ascii: "W"); header[9] = UInt8(ascii: "A"); header[10] = UInt8(ascii: "V"); header[11] = UInt8(ascii: "E")
        header[12] = UInt8(ascii: "f"); header[13] = UInt8(ascii: "m"); header[14] = UInt8(ascii: "t"); header[15] = UInt8(ascii: " ")
        Self.writeInt32LE(&header, at: 16, 16)                                   // Subchunk1Size
        Self.writeInt16LE(&header, at: 20, 1)                                    // PCM
        Self.writeInt16LE(&header, at: 22, 2)                                    // channels
        Self.writeInt32LE(&header, at: 24, Int32(sampleRateHz))
        Self.writeInt32LE(&header, at: 28, Int32(sampleRateHz * 4))              // byte rate
        Self.writeInt16LE(&header, at: 32, 4)                                    // block align
        Self.writeInt16LE(&header, at: 34, 16)                                   // bits per sample
        header[36] = UInt8(ascii: "d"); header[37] = UInt8(ascii: "a"); header[38] = UInt8(ascii: "t"); header[39] = UInt8(ascii: "a")
        Self.writeInt32LE(&header, at: 40, Int32(truncatingIfNeeded: dataSize))
        output.overwrite(at: 0, header)
    }

    /// Idempotent dispose — finalises then releases (no OS handle to close).
    public func dispose() {
        finalizeRecording()
    }

    // MARK: little-endian byte helpers

    private static func readInt16LE(_ bytes: [UInt8], at offset: Int) -> Int16 {
        let lo = UInt16(bytes[offset])
        let hi = UInt16(bytes[offset + 1]) << 8
        return Int16(bitPattern: lo | hi)
    }

    private static func writeInt16LE(_ bytes: inout [UInt8], at offset: Int, _ value: Int16) {
        let u = UInt16(bitPattern: value)
        bytes[offset] = UInt8(u & 0xFF)
        bytes[offset + 1] = UInt8((u >> 8) & 0xFF)
    }

    private static func writeInt32LE(_ bytes: inout [UInt8], at offset: Int, _ value: Int32) {
        let u = UInt32(bitPattern: value)
        bytes[offset] = UInt8(u & 0xFF)
        bytes[offset + 1] = UInt8((u >> 8) & 0xFF)
        bytes[offset + 2] = UInt8((u >> 16) & 0xFF)
        bytes[offset + 3] = UInt8((u >> 24) & 0xFF)
    }
}

// =====================================================================
// HoldMusicMixer.cs
// =====================================================================

/// Background-audio mixer for call-on-hold experiences. Loops a music track and
/// mixes the AI's speech on top at adjustable gain, ducking the background when
/// speech frames arrive. Port of `CircleAI.Telephony.HoldMusicMixer`.
///
/// The C# `Span<byte>` in/out signature becomes an array-in / array-return:
/// `mixFrame` returns the freshly written destination bytes (length = the C#
/// `frameLength` return). The loop cursor is instance state guarded by a lock so
/// concurrent renders stay ordered.
public final class HoldMusicMixer: @unchecked Sendable {
    private let backgroundLoop: [UInt8]
    private let backgroundGain: Float
    private let duckedGain: Float
    private let gate = NSLock()
    private var loopCursor: Int = 0

    /// `backgroundLoop`: PCM-16 mono buffer the mixer loops over.
    /// `backgroundGain`: gain when no speech (0..1). Default 0.6.
    /// `duckedGain`: gain while speech is mixed (0..1). Default 0.15.
    public init(backgroundLoop: [UInt8], backgroundGain: Float = 0.6, duckedGain: Float = 0.15) {
        precondition(backgroundLoop.count >= 2, "Background loop must contain at least one PCM-16 sample.")
        precondition(backgroundGain >= 0 && backgroundGain <= 1, "backgroundGain out of range")
        precondition(duckedGain >= 0 && duckedGain <= 1, "duckedGain out of range")
        self.backgroundLoop = backgroundLoop
        self.backgroundGain = backgroundGain
        self.duckedGain = duckedGain
    }

    /// Reset the loop cursor to the start.
    public func reset() {
        gate.lock(); loopCursor = 0; gate.unlock()
    }

    /// Mix `speechFrame` on top of looped background. Pass an empty speech buffer
    /// to render plain background of `renderLength` bytes. Returns the written
    /// bytes (length matches the C# `frameLength`).
    ///
    /// `renderLength` corresponds to the C# `destination.Length` for the
    /// background-only path (the caller sizes the destination). When speech is
    /// present the output length equals the speech length, as in C#.
    public func mixFrame(speech speechFrame: [UInt8], renderLength: Int) -> [UInt8] {
        if renderLength < 2 { return [] }
        let hasSpeech = speechFrame.count >= 2
        let frameLength = hasSpeech ? speechFrame.count : renderLength
        precondition(renderLength >= frameLength, "destination must be at least as long as the speech frame.")

        var destination = [UInt8](repeating: 0, count: frameLength)
        let gain = hasSpeech ? duckedGain : backgroundGain

        gate.lock()
        var i = 0
        while i < frameLength - 1 {   // process full 16-bit samples only (i, i+1)
            let speechSample: Int16 = hasSpeech ? Self.readInt16LE(speechFrame, at: i) : 0

            // Pull background sample from the loop, wrapping as needed.
            let bgSample = Self.readInt16LE(backgroundLoop, at: loopCursor)
            loopCursor = (loopCursor + 2) % backgroundLoop.count
            if loopCursor % 2 != 0 { loopCursor -= 1 } // align to 16-bit boundary

            var mixed = Int(speechSample) + Int(Float(bgSample) * gain)
            mixed = Swift.min(Swift.max(mixed, Int(Int16.min)), Int(Int16.max))
            Self.writeInt16LE(&destination, at: i, Int16(truncatingIfNeeded: mixed))
            i += 2
        }
        gate.unlock()
        return destination
    }

    private static func readInt16LE(_ bytes: [UInt8], at offset: Int) -> Int16 {
        let lo = UInt16(bytes[offset])
        let hi = UInt16(bytes[offset + 1]) << 8
        return Int16(bitPattern: lo | hi)
    }

    private static func writeInt16LE(_ bytes: inout [UInt8], at offset: Int, _ value: Int16) {
        let u = UInt16(bitPattern: value)
        bytes[offset] = UInt8(u & 0xFF)
        bytes[offset + 1] = UInt8((u >> 8) & 0xFF)
    }
}

// =====================================================================
// PhoneNumberProvisioner.cs
// =====================================================================

/// Persistence contract for assigned numbers. Port of
/// `CircleAI.Telephony.IProvisionedNumberStore`. `ValueTask` → `async`.
public protocol IProvisionedNumberStore: Sendable {
    func save(_ number: ProvisionedNumber) async
    func list() async -> [ProvisionedNumber]
    func find(_ phoneNumber: String) async -> ProvisionedNumber?
    func remove(_ phoneNumber: String) async
}

/// Default in-memory store. Thread-safe. Port of
/// `CircleAI.Telephony.InMemoryProvisionedNumberStore`.
///
/// C# keys with `StringComparer.OrdinalIgnoreCase`; here the dictionary is keyed
/// by the lowercased phone number so lookups/removes are case-insensitive, while
/// the stored value keeps the original-cased `ProvisionedNumber`.
public final class InMemoryProvisionedNumberStore: IProvisionedNumberStore, @unchecked Sendable {
    private let gate = NSLock()
    private var byNumber: [String: ProvisionedNumber] = [:]

    public init() {}

    public func save(_ number: ProvisionedNumber) async {
        gate.lock(); byNumber[number.phoneNumber.lowercased()] = number; gate.unlock()
    }

    public func list() async -> [ProvisionedNumber] {
        gate.lock(); defer { gate.unlock() }
        return Array(byNumber.values)
    }

    public func find(_ phoneNumber: String) async -> ProvisionedNumber? {
        gate.lock(); defer { gate.unlock() }
        return byNumber[phoneNumber.lowercased()]
    }

    public func remove(_ phoneNumber: String) async {
        gate.lock(); byNumber.removeValue(forKey: phoneNumber.lowercased()); gate.unlock()
    }
}

/// Service that buys + configures + persists phone numbers from any carrier
/// behind `ITelephonyCarrier`. Port of
/// `CircleAI.Telephony.PhoneNumberProvisioner`.
public final class PhoneNumberProvisioner: @unchecked Sendable {
    private let carrier: ITelephonyCarrier
    private let store: IProvisionedNumberStore
    private let log: @Sendable (String) -> Void

    public init(
        carrier: ITelephonyCarrier,
        store: IProvisionedNumberStore? = nil,
        log: (@Sendable (String) -> Void)? = nil
    ) {
        self.carrier = carrier
        self.store = store ?? InMemoryProvisionedNumberStore()
        self.log = log ?? { _ in }
    }

    /// Buy a number, wire its inbound webhook, persist it, return the metadata.
    /// `countryCode`: ISO country code (e.g. "US", "ZA", "NG"). `inboundWebhook`:
    /// HTTPS URL the carrier hits when the number rings. `areaCode`: optional
    /// prefix preference.
    public func provision(
        countryCode: String,
        inboundWebhook: URL,
        areaCode: String? = nil
    ) async throws -> ProvisionedNumber {
        if countryCode.isBlank {
            throw TelephonyError.argument("countryCode is required")
        }
        // Mirror C# `!inboundWebhook.IsAbsoluteUri`: require scheme + host.
        if inboundWebhook.scheme == nil || inboundWebhook.host == nil {
            throw TelephonyError.argument("inboundWebhook must be an absolute URI")
        }

        log("Provisioning number on \(carrier.carrierId) for \(countryCode)/\(areaCode ?? "(any)")")

        let provisioned = try await carrier.provisionNumber(countryCode: countryCode, areaCode: areaCode)

        do {
            try await carrier.configureInboundWebhook(phoneNumber: provisioned.phoneNumber, inboundWebhook: inboundWebhook)
        } catch {
            log("Webhook configuration failed for \(provisioned.phoneNumber) on \(carrier.carrierId): \(error)")
            throw error
        }

        await store.save(provisioned)
        return provisioned
    }

    /// The provisioned numbers we know about, locally + via the carrier. The
    /// carrier list is authoritative and overwrites stale store entries (C# merges
    /// store first, then carrier, keyed OrdinalIgnoreCase by phone number).
    public func list() async throws -> [ProvisionedNumber] {
        let stored = await store.list()
        let carrierNumbers = try await carrier.listNumbers()
        var merged: [String: ProvisionedNumber] = [:]
        for n in stored { merged[n.phoneNumber.lowercased()] = n }
        for n in carrierNumbers { merged[n.phoneNumber.lowercased()] = n }
        return Array(merged.values)
    }
}
