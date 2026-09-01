// VoiceKaldiFbankTests.swift
//
// These tests exist because every way this can be wrong is SILENT. A shifted
// mel range, a zero-padded first frame, a x32768 that should not be there —
// none of them error, none of them change the shape of the output, and all of
// them stop the wake word firing. So each one is asserted on its own.

import XCTest
@testable import CircleAI

final class VoiceKaldiFbankTests: XCTestCase {

    // MARK: - The five silent killers

    func testNegativeHighFreqIsAnOffsetDownFromNyquist() {
        // -400 at 16 kHz is 7600 Hz. Read as a frequency it would be -400 Hz,
        // and every filter in the bank would sit in the wrong place.
        let o = KaldiFbankOptions()
        XCTAssertEqual(o.highFreqHz, -400)
        XCTAssertEqual(o.resolvedHighFreq, 7600, accuracy: 1e-6)
    }

    func testPositiveHighFreqIsUsedAsWritten() {
        var o = KaldiFbankOptions()
        o.highFreqHz = 6000
        XCTAssertEqual(o.resolvedHighFreq, 6000, accuracy: 1e-6)
    }

    func testFramesAreCentredSoTheFirstOneStartsBeforeZero() {
        // frame 0 covers [-120, 280) at the default 25 ms / 10 ms. If this is 0
        // the frame count and the first frames both change.
        let f = KaldiFbank()
        XCTAssertEqual(f.firstSample(of: 0), -120)
        XCTAssertEqual(f.firstSample(of: 1), 40)
        XCTAssertEqual(f.firstSample(of: 2), 200)
    }

    func testSnipEdgesStartsAtZeroInstead() {
        var o = KaldiFbankOptions()
        o.snipEdges = true
        let f = KaldiFbank(options: o)
        XCTAssertEqual(f.firstSample(of: 0), 0)
        XCTAssertEqual(f.firstSample(of: 1), 160)
    }

    func testOutOfRangeSamplesAreMirroredNotZeroPadded() {
        // A constant signal must stay constant in frame 0. Zero-padding would
        // put 120 zeros at the front, which after windowing is a step, not
        // silence — a completely different first feature vector.
        var o = KaldiFbankOptions()
        o.removeDcOffset = false
        o.preemphasisCoefficient = 0
        let f = KaldiFbank(options: o)
        f.acceptWaveform([Float](repeating: 0.25, count: 16_000))
        f.flush()

        let zero = KaldiFbank(options: o)
        zero.acceptWaveform([Float](repeating: 0, count: 16_000))
        zero.flush()

        let a = f.frame(at: 0)!
        let b = f.frame(at: 20)!
        // Mirroring a constant gives the same constant, so frame 0 and a frame
        // well inside the audio must agree. With zero-padding they would not.
        for i in 0..<a.count { XCTAssertEqual(a[i], b[i], accuracy: 1e-3) }
        XCTAssertNotEqual(a[0], zero.frame(at: 0)![0], accuracy: 1e-9)
    }

    func testSamplesAreNotScaledToInt16ByDefault() {
        XCTAssertFalse(KaldiFbankOptions().scaleToInt16)

        // Turning it on must move the energies by exactly log(32768^2), because
        // the power spectrum is quadratic in the samples.
        var scaled = KaldiFbankOptions()
        scaled.scaleToInt16 = true

        let tone = (0..<8000).map { sinf(2 * .pi * 440 * Float($0) / 16_000) * 0.5 }
        let plain = KaldiFbank(); plain.acceptWaveform(tone); plain.flush()
        let big = KaldiFbank(options: scaled); big.acceptWaveform(tone); big.flush()

        let p = plain.frame(at: 10)!, q = big.frame(at: 10)!
        let expected = Float(log(32768.0 * 32768.0))
        for i in 0..<p.count where p[i] > -10 {
            XCTAssertEqual(q[i] - p[i], expected, accuracy: 1e-2)
        }
    }

    func testWindowIsPoveyNotHamming() {
        let w = KaldiFbank.poveyWindow(400)
        // Povey is (0.5-0.5cos)^0.85: it reaches zero at both ends. Hamming
        // does not — it bottoms out at 0.08, which is the giveaway.
        XCTAssertEqual(w[0], 0, accuracy: 1e-6)
        XCTAssertEqual(w[399], 0, accuracy: 1e-6)
        XCTAssertEqual(w[199], Float(pow(0.5 - 0.5 * cos(2 * Double.pi * 199 / 399), 0.85)),
                       accuracy: 1e-6)
        XCTAssertGreaterThan(w[200], 0.99)   // ~1 at the centre
    }

    func testDcIsRemovedSoAConstantSignalIsSilence() {
        // Order matters. A constant offset that survives to the pre-emphasis
        // stage leaks a (1-0.97) fraction of itself into every sample.
        var o = KaldiFbankOptions()
        o.snipEdges = true
        let f = KaldiFbank(options: o)
        f.acceptWaveform([Float](repeating: 1.0, count: 4000))
        f.flush()
        let frame = f.frame(at: 5)!
        // A pure DC signal, mean-removed first, is exactly silence.
        for v in frame { XCTAssertEqual(v, log(Float.ulpOfOne), accuracy: 1e-3) }
    }

    func testLogFloorIsFloatEpsilonNotDenormalMin() {
        // 1.19e-7 gives log ~= -15.94. The denormal minimum would give -103,
        // which is a wildly different value in every silent frame.
        let f = KaldiFbank()
        f.acceptWaveform([Float](repeating: 0, count: 16_000))
        f.flush()
        let frame = f.frame(at: 30)!
        for v in frame { XCTAssertEqual(v, -15.9424, accuracy: 1e-3) }
    }

    // MARK: - Shape and streaming

    func testDimensionAndDefaults() {
        let o = KaldiFbankOptions()
        XCTAssertEqual(o.numMelBins, 80)
        XCTAssertEqual(o.frameLength, 400)
        XCTAssertEqual(o.frameShift, 160)
        XCTAssertEqual(o.paddedWindow, 512)
        XCTAssertEqual(KaldiFbank().dimension, 80)
        XCTAssertNil(KaldiFbank().frame(at: 0))
    }

    func testFrameCountForACompleteUtteranceMatchesKaldi() {
        // One second at 10 ms hop is 100 frames, not 98 and not 101.
        let f = KaldiFbank()
        f.acceptWaveform([Float](repeating: 0.1, count: 16_000))
        f.flush()
        XCTAssertEqual(f.framesReady, 100)
    }

    func testMidStreamOnlyEmitsFramesWhoseWindowIsComplete() {
        // The mirrored tail is withheld until flush, because a frame computed
        // from a mirror that later turns out to have real audio behind it is a
        // different frame — and a streaming detector cannot take that back.
        let f = KaldiFbank()
        f.acceptWaveform([Float](repeating: 0.1, count: 1_600))
        let midStream = f.framesReady
        f.flush()
        XCTAssertGreaterThan(f.framesReady, midStream)
        XCTAssertEqual(f.framesReady, 10)
    }

    func testAcceptingInPiecesGivesTheSameFramesAsAllAtOnce() {
        let tone = (0..<8_000).map { sinf(2 * .pi * 300 * Float($0) / 16_000) * 0.4 }

        let whole = KaldiFbank(); whole.acceptWaveform(tone); whole.flush()

        let piecemeal = KaldiFbank()
        var i = 0
        while i < tone.count {
            piecemeal.acceptWaveform(Array(tone[i..<min(i + 317, tone.count)]))
            i += 317
        }
        piecemeal.flush()

        XCTAssertEqual(whole.framesReady, piecemeal.framesReady)
        for f in stride(from: 0, to: whole.framesReady, by: 7) {
            let a = whole.frame(at: f)!, b = piecemeal.frame(at: f)!
            for k in 0..<a.count { XCTAssertEqual(a[k], b[k], accuracy: 1e-3) }
        }
    }

    func testResetClearsEverything() {
        let f = KaldiFbank()
        f.acceptWaveform([Float](repeating: 0.1, count: 16_000))
        f.flush()
        XCTAssertGreaterThan(f.framesReady, 0)
        f.reset()
        XCTAssertEqual(f.framesReady, 0)
        XCTAssertNil(f.frame(at: 0))
    }

    // MARK: - The maths on its own

    func testMelScaleIsTheKaldiFormula() {
        XCTAssertEqual(KaldiFbank.melScale(0), 0, accuracy: 1e-6)
        XCTAssertEqual(KaldiFbank.melScale(700), Float(1127.0 * log(2.0)), accuracy: 1e-3)
        XCTAssertGreaterThan(KaldiFbank.melScale(8000), KaldiFbank.melScale(7600))
    }

    func testMelBanksSpanTheResolvedRangeAndAreTriangles() {
        let (banks, start) = KaldiFbank.melBanks(KaldiFbankOptions())
        XCTAssertEqual(banks.count, 80)
        XCTAssertEqual(start.count, 80)
        for b in banks {
            XCTAssertFalse(b.isEmpty)
            for w in b { XCTAssertGreaterThan(w, 0); XCTAssertLessThanOrEqual(w, 1.0001) }
        }
        // Bins march upward: a higher mel bin starts at a higher FFT bin.
        for m in 1..<80 { XCTAssertGreaterThanOrEqual(start[m], start[m - 1]) }
    }

    func testFftPowerSpectrumFindsAPureTone() {
        // 512-point frame, 16 kHz, bin width 31.25 Hz. A tone at bin 40 is
        // 1250 Hz and must dominate.
        let n = 512
        let x = (0..<n).map { sinf(2 * .pi * 40 * Float($0) / Float(n)) }
        let p = KaldiFbank.powerSpectrum(x)
        XCTAssertEqual(p.count, n / 2 + 1)
        let peak = p.enumerated().max { $0.element < $1.element }!.offset
        XCTAssertEqual(peak, 40)
    }

    func testFftOfSilenceIsZero() {
        let p = KaldiFbank.powerSpectrum([Float](repeating: 0, count: 512))
        for v in p { XCTAssertEqual(v, 0, accuracy: 1e-9) }
    }

    func testFftOfDcPutsAllEnergyInBinZero() {
        let p = KaldiFbank.powerSpectrum([Float](repeating: 1, count: 512))
        XCTAssertEqual(p[0], 512 * 512, accuracy: 1)
        for k in 1..<p.count { XCTAssertEqual(p[k], 0, accuracy: 1e-2) }
    }

    func testLoudToneLightsUpItsOwnBandAndNotTheTop() {
        // The end-to-end sanity check: a 1 kHz tone must light up mel bins in
        // the middle of the range and leave the top near the floor.
        let f = KaldiFbank()
        f.acceptWaveform((0..<16_000).map { sinf(2 * .pi * 1000 * Float($0) / 16_000) * 0.7 })
        f.flush()
        let frame = f.frame(at: 50)!
        let loudest = frame.enumerated().max { $0.element < $1.element }!.offset
        XCTAssertGreaterThan(loudest, 15)
        XCTAssertLessThan(loudest, 55)
        XCTAssertGreaterThan(frame[loudest] - frame[79], 5)
    }
}
