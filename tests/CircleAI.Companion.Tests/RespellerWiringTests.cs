// RespellerWiringTests.cs
//
// Does teaching the ear change what the mouth says?
//
// This is the test that was missing, and its absence hid a real gap: the whole
// respelling chain lived inside the test probe, so the live conversation spoke
// through the engine directly and never respelt anything. Every unit below it
// passed. A person could teach their phone how they say a word and be answered
// in the old pronunciation forever, and nothing would have reported a fault.
//
// So these tests deliberately do not test Respeller in isolation. They go in
// through the seam the conversation actually uses — an ITtsEngine — and assert on
// the text that reached the voice.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Companion.Tests;

public class RespellerWiringTests
{
    /// <summary>A voice that says nothing and remembers what it was asked to say.</summary>
    private sealed class RecordingEngine : ITtsEngine
    {
        public string? LastText { get; private set; }

        public Task<TtsSynthesisResult> SynthesiseAsync(string text, CancellationToken ct = default)
        {
            LastText = text;
            return Task.FromResult(new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, 22050, 1, 16));
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
            string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastText = text;
            await Task.CompletedTask;
            yield break;
        }
    }

    private static (RecordingEngine Voice, ITtsEngine Mouth) Mouth(PersonalRespellings? personal = null)
    {
        var voice = new RecordingEngine();
        return (voice, new RespellingTtsEngine(voice,
            new Respeller { HostLanguage = "zu", Personal = personal }));
    }

    [Fact]
    public async Task A_settled_borrowing_reaches_the_voice_respelt()
    {
        var (voice, mouth) = Mouth();
        await mouth.SynthesiseAsync("Ngithumele i-SMS manje");

        Assert.Contains("esemese", voice.LastText);
        Assert.DoesNotContain("SMS", voice.LastText);
    }

    [Fact]
    public async Task What_the_person_taught_us_is_what_the_voice_is_given()
    {
        // The point of the whole exercise. Five hearings of their pronunciation,
        // and the next thing said back to them uses it.
        var personal = new PersonalRespellings();
        var shipped = LoanwordRespeller.Table("zu");
        for (var i = 0; i < 5; i++)
            personal.LearnFrom("ngicela i-wayifayi ekhaya", shipped);

        var (voice, mouth) = Mouth(personal);
        await mouth.SynthesiseAsync("Ithi i-WiFi ayisebenzi");

        Assert.Contains("wayifayi", voice.LastText);
    }

    [Fact]
    public async Task Before_they_have_taught_us_the_shipped_spelling_is_used()
    {
        // The same sentence as above with an untaught table, so the test above is
        // proving the learning rather than the shipped entry that was there anyway.
        var (voice, mouth) = Mouth(new PersonalRespellings());
        await mouth.SynthesiseAsync("Ithi i-WiFi ayisebenzi");

        Assert.Equal(LoanwordRespeller.Respell("WiFi", "zu"), Spoken(voice.LastText!, "wa"));

        static string Spoken(string text, string startsWith) =>
            text.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .First(w => w.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_host_language_is_left_alone()
    {
        // Most of any sentence is the person's own language and must arrive at the
        // voice exactly as written. Rewriting it would be a pronunciation bug in
        // the one place the voice was already correct.
        const string zulu = "Sawubona ngicela usizo namuhla";
        var (voice, mouth) = Mouth();
        await mouth.SynthesiseAsync(zulu);

        Assert.Equal(zulu, voice.LastText);
    }

    [Fact]
    public async Task An_unknown_borrowing_is_at_least_made_pronounceable()
    {
        // No table entry and no English G2P on this device. Splitting the compound
        // is not a respelling and does not pretend to be one — it just stops the
        // voice reading a run of letters as a single unreadable word.
        Assert.Null(LoanwordRespeller.Respell("PowerBank", "zu"));   // genuinely unknown

        var (voice, mouth) = Mouth();
        await mouth.SynthesiseAsync("Ngithenge i-PowerBank");

        Assert.DoesNotContain("PowerBank", voice.LastText);
        Assert.Contains("Power Bank", voice.LastText);
    }

    [Fact]
    public async Task A_language_these_spellings_were_not_written_for_is_untouched()
    {
        // Afrikaans has its own forms for these words. Applying isiZulu spellings
        // to it would mangle words that were never borrowed the same way.
        const string afrikaans = "Stuur vir my 'n SMS";
        var voice = new RecordingEngine();
        var mouth = new RespellingTtsEngine(voice, new Respeller { HostLanguage = "af" });

        await mouth.SynthesiseAsync(afrikaans);
        Assert.Equal(afrikaans, voice.LastText);
    }

    [Fact]
    public async Task Streaming_respells_too()
    {
        // Streaming is the path a real conversation takes, and it is the one easiest
        // to leave behind when only the single-shot call is wired.
        var (voice, mouth) = Mouth();
        await foreach (var _ in mouth.StreamSynthesiseAsync("Ngithumele i-SMS")) { }

        Assert.Contains("esemese", voice.LastText);
    }

    [Fact]
    public void The_learning_chain_outranks_the_shipped_table()
    {
        // Order matters more than any single rung: a person is the authority on
        // their own speech, so what they taught us must win over what we shipped.
        var personal = new PersonalRespellings();
        for (var i = 0; i < 5; i++) personal.Observe("SMS", "esemesi", "esemese");

        var respeller = new Respeller { HostLanguage = "zu", Personal = personal };
        Assert.Equal("esemesi", respeller.For("SMS"));
        Assert.Equal("esemese", LoanwordRespeller.Respell("SMS", "zu"));   // shipped, unchanged
    }
}
