// HostingVoiceOptions.swift
//
// How a host configures the voice loop.
//
// Ported from src/CircleAI.Hosting/VoiceOptions.cs.

import Foundation

public struct VoiceOptions: Sendable, Equatable, Codable {

    /// The phrase, lower case. Matching is case-insensitive downstream, but the
    /// stored form is normalised here so two hosts writing "Hey B" and "hey b"
    /// do not produce two different configurations of the same thing.
    public var wakeWord: String

    /// 16 kHz, which is what every wake and speech model in the catalogue was
    /// trained at. A host that captures at 44.1 and does not say so gets
    /// features computed against the wrong time base — the model runs, burns
    /// battery, and never fires.
    public var sampleRateHz: Int

    /// OFF by default. A microphone that opens itself the moment a library is
    /// constructed is not a decision a library gets to make.
    public var autoStart: Bool

    /// "null" by default: silent, and a host that never wires a real engine
    /// gets a working pipeline with no audio rather than a crash at the first
    /// reply.
    public var ttsBackend: String

    /// How long a person may pause before the turn is treated as finished.
    ///
    /// 800 ms is a deliberate compromise. Shorter and the assistant interrupts
    /// somebody thinking mid-sentence; longer and every exchange feels slow.
    public var endOfSpeechSilenceMs: Int

    public init(wakeWord: String = "hey b",
                sampleRateHz: Int = 16_000,
                autoStart: Bool = false,
                ttsBackend: String = "null",
                endOfSpeechSilenceMs: Int = 800) {
        self.wakeWord = wakeWord.lowercased()
        self.sampleRateHz = sampleRateHz
        self.autoStart = autoStart
        self.ttsBackend = ttsBackend
        self.endOfSpeechSilenceMs = endOfSpeechSilenceMs
    }
}
