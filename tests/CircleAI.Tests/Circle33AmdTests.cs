// Circle33AmdTests.cs
//
// (3.3.0) Tests for answering machine detector.

using System;
using System.Buffers.Binary;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33AmdTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void Observe_NoAudio_VerdictUnknown()
    {
        var amd = new AnsweringMachineDetector();
        amd.Observe(new byte[480 * 2], SampleRate);
        Assert.Equal(AmdVerdict.Unknown, amd.CurrentVerdict);
    }

    [Fact]
    public void Observe_ShortUtteranceThenSilence_Human()
    {
        var amd = new AnsweringMachineDetector(new AmdOptions(
            HumanMinFirstUtteranceMs: 200,
            HumanMaxFirstUtteranceMs: 2000,
            SilenceFrameThresholdMs:  100));

        // 500ms speech.
        var speech = TonePcm(SampleRate / 2, 200, 0.5);
        amd.Observe(speech, SampleRate);

        // 200ms silence.
        for (int i = 0; i < 4; i++)
        {
            amd.Observe(new byte[SampleRate * 50 / 1000 * 2], SampleRate);
        }
        Assert.Equal(AmdVerdict.Human, amd.CurrentVerdict);
    }

    [Fact]
    public void Observe_LongUtterance_AnsweringMachine()
    {
        var amd = new AnsweringMachineDetector(new AmdOptions(
            HumanMaxFirstUtteranceMs: 800));

        // Single contiguous 1.5 second speech burst → machine.
        var speech = TonePcm(SampleRate * 3 / 2, 300, 0.5);
        amd.Observe(speech, SampleRate);

        Assert.Equal(AmdVerdict.AnsweringMachine, amd.CurrentVerdict);
    }

    [Fact]
    public void Observe_ObservationWindowExpires_FallsThroughUnknown()
    {
        var amd = new AnsweringMachineDetector(new AmdOptions(
            MaxObservationWindow:     500,
            HumanMinFirstUtteranceMs: 200,
            HumanMaxFirstUtteranceMs: 2000));

        // 600ms silence: window expires with no speech.
        for (int i = 0; i < 12; i++)
        {
            amd.Observe(new byte[SampleRate * 50 / 1000 * 2], SampleRate);
        }
        Assert.Equal(AmdVerdict.Unknown, amd.CurrentVerdict);
    }

    [Fact]
    public void Observe_ZeroSampleRate_Throws()
    {
        var amd = new AnsweringMachineDetector();
        Assert.Throws<ArgumentOutOfRangeException>(() => amd.Observe(new byte[100], 0));
    }

    [Fact]
    public void Reset_ClearsVerdict()
    {
        var amd = new AnsweringMachineDetector(new AmdOptions(HumanMaxFirstUtteranceMs: 200));
        var speech = TonePcm(SampleRate, 300, 0.5);
        amd.Observe(speech, SampleRate);
        Assert.Equal(AmdVerdict.AnsweringMachine, amd.CurrentVerdict);

        amd.Reset();
        Assert.Equal(AmdVerdict.Unknown, amd.CurrentVerdict);
    }

    [Fact]
    public void Observe_VerdictStickyAfterFirstDecision()
    {
        var amd = new AnsweringMachineDetector(new AmdOptions(HumanMaxFirstUtteranceMs: 200));
        amd.Observe(TonePcm(SampleRate, 300, 0.5), SampleRate);
        Assert.Equal(AmdVerdict.AnsweringMachine, amd.CurrentVerdict);

        // Feeding more audio shouldn't flip the verdict.
        amd.Observe(new byte[SampleRate * 2], SampleRate);
        Assert.Equal(AmdVerdict.AnsweringMachine, amd.CurrentVerdict);
    }

    private static byte[] TonePcm(int samples, double frequencyHz, double amplitude)
    {
        var buf = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            var s = amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 2, 2), (short)(s * short.MaxValue));
        }
        return buf;
    }
}
