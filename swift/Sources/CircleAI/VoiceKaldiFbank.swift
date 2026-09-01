// VoiceKaldiFbank.swift
//
// 80-dimensional log-mel filterbank features, bit-compatible with Kaldi.
//
// WHY NOT THE MEL WE ALREADY HAVE. The speaker-identity path computes a mel
// spectrogram, and it is a perfectly good generic one: Hamming window, plain
// hop, no pre-emphasis, no DC removal. Feeding that to a Kaldi-trained model
// produces features that are the right SHAPE and the wrong NUMBERS — so the
// model loads, runs, burns battery, and never fires. Nothing errors. That
// failure looks exactly like "the wake word is not very good", which is why
// this is written out properly rather than approximated.
//
// The five details that decide whether this works, each a silent killer alone:
//
//   highFreq = -400    NEGATIVE means nyquist + highFreq. The top of the mel
//                      range is 7600 Hz, not 8000. Wrong here shifts every
//                      filter.
//   snipEdges = false  Frames are CENTRED, the first starts at -120, and
//                      out-of-range samples are MIRRORED, not zero-padded.
//                      Changes both the frame count and the first frames.
//   NO x 32768         Samples go in at [-1, 1] and are used AS THEY ARE.
//   povey window       (0.5 - 0.5cos)^0.85, not Hamming, not Hann.
//   preemph + DC       Per frame, in that order: subtract the frame mean, then
//                      pre-emphasise at 0.97, THEN window.
//
// Streaming by construction: frame f needs samples [f*160-120, +400), which
// does not depend on how much audio arrives later, so frames are emitted as
// soon as their window is complete. Only the last frames of a finished
// utterance need the mirrored tail, which is what flush() is for.
//
// Ported from src/CircleAI.Voice/KaldiFbank.cs.

import Foundation

public struct KaldiFbankOptions: Sendable, Equatable {
    public var sampleRateHz: Int
    public var numMelBins: Int
    public var lowFreqHz: Float
    /// NEGATIVE means nyquist + this. See `resolvedHighFreq`.
    public var highFreqHz: Float
    public var frameLengthMs: Float
    public var frameShiftMs: Float
    public var preemphasisCoefficient: Float
    public var removeDcOffset: Bool
    public var snipEdges: Bool
    /// OFF by default. Samples arrive at [-1, 1] and Kaldi uses them as they are.
    public var scaleToInt16: Bool

    public init(sampleRateHz: Int = 16_000,
                numMelBins: Int = 80,
                lowFreqHz: Float = 20.0,
                highFreqHz: Float = -400.0,
                frameLengthMs: Float = 25.0,
                frameShiftMs: Float = 10.0,
                preemphasisCoefficient: Float = 0.97,
                removeDcOffset: Bool = true,
                snipEdges: Bool = false,
                scaleToInt16: Bool = false) {
        self.sampleRateHz = sampleRateHz
        self.numMelBins = numMelBins
        self.lowFreqHz = lowFreqHz
        self.highFreqHz = highFreqHz
        self.frameLengthMs = frameLengthMs
        self.frameShiftMs = frameShiftMs
        self.preemphasisCoefficient = preemphasisCoefficient
        self.removeDcOffset = removeDcOffset
        self.snipEdges = snipEdges
        self.scaleToInt16 = scaleToInt16
    }

    public var frameLength: Int { Int(Float(sampleRateHz) * frameLengthMs / 1000) }

    public var frameShift: Int { Int(Float(sampleRateHz) * frameShiftMs / 1000) }

    public var paddedWindow: Int {
        var n = 1
        while n < frameLength { n <<= 1 }
        return n
    }

    /// A positive value is a frequency; a NEGATIVE one is an offset down from
    /// nyquist. -400 at 16 kHz means 7600 Hz, not -400 Hz.
    public var resolvedHighFreq: Float {
        highFreqHz > 0 ? highFreqHz : Float(sampleRateHz) / 2 + highFreqHz
    }
}

public final class KaldiFbank: @unchecked Sendable {

    private let o: KaldiFbankOptions
    private let window: [Float]          // povey
    private let melBanks: [[Float]]      // [bin][fftBin]
    private let melStart: [Int]          // first non-zero fft bin per mel bin

    private var samples: [Float] = []
    private var framesRead = 0

    public private(set) var framesReady = 0

    public init(options: KaldiFbankOptions = KaldiFbankOptions()) {
        self.o = options
        self.window = Self.poveyWindow(options.frameLength)
        let (banks, start) = Self.melBanks(options)
        self.melBanks = banks
        self.melStart = start
    }

    public var dimension: Int { o.numMelBins }

    public func acceptWaveform(_ incoming: [Float]) {
        // If scaling is asked for it belongs HERE, once, before anything reads a
        // sample — everything downstream inherits the factor.
        let scale: Float = o.scaleToInt16 ? 32768 : 1
        samples.append(contentsOf: incoming.map { $0 * scale })
        recount(flush: false)
    }

    public func flush() { recount(flush: true) }

    public func reset() {
        samples.removeAll()
        framesRead = 0
        framesReady = 0
    }

    private func recount(flush: Bool) {
        let n = samples.count
        var frames: Int
        if o.snipEdges {
            frames = n < o.frameLength ? 0 : 1 + (n - o.frameLength) / o.frameShift
        } else if flush {
            // Kaldi's count for a complete utterance.
            frames = (n + o.frameShift / 2) / o.frameShift
        } else {
            // Mid-stream: only frames whose window is entirely inside the audio
            // actually held. The mirrored tail is deliberately withheld.
            frames = 0
            while firstSample(of: frames) + o.frameLength <= n { frames += 1 }
        }
        framesReady = max(0, frames)
    }

    /// CENTRED when snipEdges is off: the midpoint of the frame minus half a
    /// window, so frame 0 starts at -120 and is filled by mirroring.
    func firstSample(of frame: Int) -> Int {
        o.snipEdges
            ? frame * o.frameShift
            : frame * o.frameShift + o.frameShift / 2 - o.frameLength / 2
    }

    public func frame(at index: Int) -> [Float]? {
        guard index >= 0, index < framesReady else { return nil }

        let n = samples.count
        let start = firstSample(of: index)
        var buf = [Float](repeating: 0, count: o.paddedWindow)  // zero-padded to the FFT size

        for i in 0..<o.frameLength {
            var s = start + i
            // Kaldi MIRRORS rather than zero-padding. Looping, because a very
            // short utterance can reflect off both ends more than once.
            while s < 0 || s >= n {
                if s < 0 { s = -s - 1 } else { s = 2 * n - 1 - s }
            }
            buf[i] = samples[s]
        }

        // Order matters and is Kaldi's: mean, then pre-emphasis, then window.
        if o.removeDcOffset {
            var sum: Float = 0
            for i in 0..<o.frameLength { sum += buf[i] }
            let mean = sum / Float(o.frameLength)
            for i in 0..<o.frameLength { buf[i] -= mean }
        }

        if o.preemphasisCoefficient != 0 {
            let c = o.preemphasisCoefficient
            var i = o.frameLength - 1
            while i > 0 {
                buf[i] -= c * buf[i - 1]
                i -= 1
            }
            buf[0] -= c * buf[0]   // Kaldi repeats sample 0
        }

        for i in 0..<o.frameLength { buf[i] *= window[i] }

        let power = Self.powerSpectrum(buf)

        var out = [Float](repeating: 0, count: o.numMelBins)
        for m in 0..<o.numMelBins {
            let bank = melBanks[m]
            let first = melStart[m]
            var e: Float = 0
            for k in 0..<bank.count { e += power[first + k] * bank[k] }
            // Float.ulpOfOne (1.19e-7), NOT Float.leastNonzeroMagnitude
            // (1.4e-45). Kaldi uses numeric_limits<float>::epsilon(); the
            // denormal minimum is a completely different floor and would change
            // every silent frame's value.
            out[m] = log(max(e, Float.ulpOfOne))
        }
        return out
    }

    public func consume(frames: Int) {
        guard frames > 0 else { return }
        framesRead += frames
        let keepFrom = max(0, firstSample(of: framesRead))
        guard keepFrom > 0 else { return }
        samples.removeFirst(min(keepFrom, samples.count))
        // Indices are relative to the buffer, so the frame origin shifts with it.
        framesRead = 0
        recount(flush: false)
    }

    // MARK: - The maths

    static func poveyWindow(_ n: Int) -> [Float] {
        var w = [Float](repeating: 0, count: n)
        let a = 2 * Double.pi / Double(n - 1)
        for i in 0..<n {
            w[i] = Float(pow(0.5 - 0.5 * cos(a * Double(i)), 0.85))
        }
        return w
    }

    static func melScale(_ hz: Float) -> Float { 1127.0 * log(1.0 + hz / 700.0) }

    static func melBanks(_ o: KaldiFbankOptions) -> ([[Float]], [Int]) {
        let fftBins = o.paddedWindow / 2
        let binWidth = Float(o.sampleRateHz) / Float(o.paddedWindow)

        let melLow = melScale(o.lowFreqHz)
        let melHigh = melScale(o.resolvedHighFreq)
        let delta = (melHigh - melLow) / Float(o.numMelBins + 1)

        var banks: [[Float]] = []
        var start: [Int] = []

        for m in 0..<o.numMelBins {
            let left = melLow + Float(m) * delta
            let centre = melLow + Float(m + 1) * delta
            let right = melLow + Float(m + 2) * delta

            var weights: [Float] = []
            var first = -1
            for i in 0..<fftBins {
                let mel = melScale(binWidth * Float(i))
                if mel <= left || mel >= right {
                    if first >= 0 { break }   // past the triangle
                    continue
                }
                if first < 0 { first = i }
                weights.append(mel <= centre
                               ? (mel - left) / (centre - left)
                               : (right - mel) / (right - centre))
            }
            banks.append(weights)
            start.append(first < 0 ? 0 : first)
        }
        return (banks, start)
    }

    /// Radix-2 in place. Written out rather than taken from Accelerate so the
    /// numbers are identical on every platform this ports to — a vendor FFT is
    /// free to reassociate, and a 1-ULP difference here is a different feature
    /// vector.
    static func powerSpectrum(_ frame: [Float]) -> [Float] {
        let n = frame.count
        var re = frame
        var im = [Float](repeating: 0, count: n)

        // Bit-reversal permutation.
        var j = 0
        for i in 1..<n {
            var bit = n >> 1
            while j & bit != 0 {
                j ^= bit
                bit >>= 1
            }
            j ^= bit
            if i < j {
                re.swapAt(i, j)
                im.swapAt(i, j)
            }
        }

        var len = 2
        while len <= n {
            let ang = -2 * Double.pi / Double(len)
            let wRe = Float(cos(ang))
            let wIm = Float(sin(ang))
            var i = 0
            while i < n {
                var curRe: Float = 1
                var curIm: Float = 0
                for k in 0..<(len / 2) {
                    let uRe = re[i + k]
                    let uIm = im[i + k]
                    let vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm
                    let vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe
                    re[i + k] = uRe + vRe
                    im[i + k] = uIm + vIm
                    re[i + k + len / 2] = uRe - vRe
                    im[i + k + len / 2] = uIm - vIm
                    let nextRe = curRe * wRe - curIm * wIm
                    curIm = curRe * wIm + curIm * wRe
                    curRe = nextRe
                }
                i += len
            }
            len <<= 1
        }

        var power = [Float](repeating: 0, count: n / 2 + 1)
        for k in 0...(n / 2) { power[k] = re[k] * re[k] + im[k] * im[k] }
        return power
    }
}
