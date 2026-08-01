// BreathingRoomTests.cs
//
// Silence before the first word and after the last.
//
// A person draws breath before speaking and lets the last syllable decay. A
// synthesiser starts on sample zero and stops on the final one, which lands as
// abrupt at both ends — and on a phone the audio path often eats the opening
// milliseconds while the output stream spins up, so the first consonant is
// clipped as well as sudden.
//
// The trap these tests exist for is the single-segment fast path: it returns the
// inner engine's audio untouched, so padding could silently apply to long text
// and not to short. Grouping sentences makes that the COMMON case, not a corner.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Companion.Tests;

public class BreathingRoomTests
{
    /// <summary>An engine that returns a fixed tone, so lengths are predictable.</summary>
    private sealed class FixedToneEngine : ITtsEngine
    {
        public const int Rate = 16000;
        public const int Ms = 500;
        public int Calls { get; private set; }

        public Task<TtsSynthesisResult> SynthesiseAsync(string text, CancellationToken ct = default)
        {
            Calls++;
            var samples = Rate * Ms / 1000;
            var pcm = new byte[samples * 2];
            for (var i = 0; i < samples; i++)          // non-zero, so silence is distinguishable
            {
                pcm[i * 2] = 0x10;
                pcm[i * 2 + 1] = 0x20;
            }
            return Task.FromResult(new TtsSynthesisResult(pcm, Rate, 1, 16));
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
            string text,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return (await SynthesiseAsync(text, ct)).AudioData;
        }

        public void Dispose() { }
    }

    private static int Ms(TtsSynthesisResult r) =>
        (int)(r.AudioData.Length / 2.0 / r.SampleRate * 1000);

    private static int LeadingSilentMs(TtsSynthesisResult r)
    {
        var s = r.AudioData.Span;
        var i = 0;
        while (i + 1 < s.Length && s[i] == 0 && s[i + 1] == 0) i += 2;
        return (int)(i / 2.0 / r.SampleRate * 1000);
    }

    private static int TrailingSilentMs(TtsSynthesisResult r)
    {
        var s = r.AudioData.Span;
        var i = s.Length - 2;
        var n = 0;
        while (i >= 0 && s[i] == 0 && s[i + 1] == 0) { n += 2; i -= 2; }
        return (int)(n / 2.0 / r.SampleRate * 1000);
    }

    [Fact]
    public async Task Without_padding_the_audio_is_exactly_what_the_engine_produced()
    {
        using var phrased = new PhrasedTtsEngine(new FixedToneEngine());
        var r = await phrased.SynthesiseAsync("One sentence.");
        Assert.Equal(FixedToneEngine.Ms, Ms(r));
        Assert.Equal(0, LeadingSilentMs(r));
    }

    [Fact]
    public async Task A_single_sentence_still_gets_its_breathing_room()
    {
        // The fast path. Grouping collapses paragraphs to one segment, so if this
        // regressed, padding would apply to short text and vanish on long.
        using var phrased = new PhrasedTtsEngine(new FixedToneEngine())
        {
            LeadInSilenceMs = 200,
            TailSilenceMs = 300,
        };
        var r = await phrased.SynthesiseAsync("Sawubona mhlaba.");

        Assert.InRange(LeadingSilentMs(r), 190, 210);
        Assert.InRange(TrailingSilentMs(r), 290, 310);
        Assert.InRange(Ms(r), FixedToneEngine.Ms + 480, FixedToneEngine.Ms + 520);
    }

    [Fact]
    public async Task Padding_wraps_the_WHOLE_utterance_not_every_sentence()
    {
        // Three sentences must yield one breath at the front and one at the end,
        // not three of each — otherwise every full stop becomes a gasp.
        using var phrased = new PhrasedTtsEngine(new FixedToneEngine())
        {
            LeadInSilenceMs = 200,
            TailSilenceMs = 200,
        };
        var r = await phrased.SynthesiseAsync("One. Two. Three.");

        Assert.Equal(3, phrased.LastSegmentCount);
        Assert.InRange(LeadingSilentMs(r), 190, 210);
        Assert.InRange(TrailingSilentMs(r), 190, 260);   // tail + the last sentence's own pause
    }

    [Fact]
    public async Task Grouping_reduces_the_number_of_utterances()
    {
        var engine = new FixedToneEngine();
        using var phrased = new PhrasedTtsEngine(engine) { SentencesPerUtterance = 3 };
        await phrased.SynthesiseAsync("One. Two. Three. Four. Five.");

        // Five sentences in groups of three is two utterances — and two openings
        // for the model to over-lengthen instead of five.
        Assert.Equal(2, phrased.LastSegmentCount);
        Assert.Equal(2, engine.Calls);
    }
}
