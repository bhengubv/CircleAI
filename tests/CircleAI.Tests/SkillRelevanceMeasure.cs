// SkillRelevanceMeasure.cs
//
// THE RULER, BUILT BEFORE THE FIX.
//
// A P30 asked "Name three colours" and was handed ui-ux-design, threejs,
// inspira-ui, better-chatbot and creative-coding - 1,082 hits out of 1,378
// skills, 1,593 characters of web-development instructions dropped into a
// 4096-token window on a 0.6B model. The answer came back "Here is my name as
// an AI and the current weather as shown on your screen."
//
// The query was CLEAN for that turn - the log says "recall: 0 offered" - so the
// prompt-contamination fix does not touch this. This is the ranker.
//
// THE LAST TIME THIS WAS "FIXED" IT GOT WORSE. OPEN-GAPS E15: OR was replaced
// with a strict AND on the strength of a corrupted measurement, which excluded
// the two skills literally named threat-modeling and security-threat-model.
// So this file measures first, keeps E15's case as a regression guard, and any
// change has to satisfy both at once.

using CircleAI.Skills;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public class SkillRelevanceMeasure : IClassFixture<SkillLibraryFixture>
{
    private readonly ITestOutputHelper _out;
    private readonly SkillLibraryFixture _library;

    public SkillRelevanceMeasure(ITestOutputHelper output, SkillLibraryFixture library)
    {
        _out = output;
        _library = library;
    }

    private string Db
    {
        get
        {
            Assert.True(_library.Available, "skills.db.zip was not found");
            return _library.DatabasePath!;
        }
    }

    /// <summary>Questions a person actually asks an assistant, and none of them are about software.</summary>
    public static TheoryData<string> EverydayQuestions() =>
    [
        "Name three colours",
        "What is the capital of Kenya",
        "What is the weather like today",
        "Tell me a joke",
        "How do I plant maize",
        "Write a short thank-you message",
        "What is 12 times 8",
    ];

    [Theory]
    [MemberData(nameof(EverydayQuestions))]
    public void An_everyday_question_does_not_match_most_of_the_library(string question)
    {
        using var store = new SqliteSkillStore(Db, identifyingMatchOnly: true);

        var total = store.ListAsync().GetAwaiter().GetResult().Count;
        var hits = store.SearchAsync(question).GetAwaiter().GetResult();

        var share = total == 0 ? 0 : (double)hits.Count / total;
        _out.WriteLine($"\"{question}\" -> {hits.Count} of {total} ({share:P0})");
        foreach (var h in hits.Take(5)) _out.WriteLine($"    {h.Id}");

        // A QUARTER OF THE LIBRARY IS ALREADY ABSURD for a three-word question
        // about colours. This is not a tuning threshold; it is the line past
        // which "matched" has stopped meaning anything at all.
        Assert.True(share <= 0.25,
            $"\"{question}\" matched {hits.Count} of {total} skills ({share:P0}) - "
            + $"top: {string.Join(", ", hits.Take(5).Select(h => h.Id))}");
    }

    [Fact]
    public void The_question_E15_got_wrong_still_finds_the_right_skills_first()
    {
        // THE REGRESSION GUARD. OPEN-GAPS E15: switching OR to AND made this
        // query return four OSINT pages and EXCLUDE the two skills literally
        // named for it. Whatever fixes the flooding must not do that again.
        using var store = new SqliteSkillStore(Db, identifyingMatchOnly: true);
        var hits = store.SearchAsync("threat modeling for a mobile app").GetAwaiter().GetResult();

        _out.WriteLine($"threat modeling -> {hits.Count} hits");
        foreach (var h in hits.Take(5)) _out.WriteLine($"    {h.Id}");

        var topFive = hits.Take(5).Select(h => h.Id).ToList();
        Assert.Contains(topFive, id => id.Contains("threat", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Greetings and openers - what somebody says BEFORE they ask anything.</summary>
    public static TheoryData<string> Greetings() =>
    [
        "Hey, B.",
        "Hey B",
        "hello",
        "hi there",
        "good morning",
    ];

    [Theory]
    [MemberData(nameof(Greetings))]
    public void A_greeting_costs_the_model_nothing(string greeting)
    {
        // THE RULER THE OTHER ONE IS NOT. Share-of-library says this is fine:
        // "Hey, B." matched 3 skills out of 1,378, which is 0.2% and sails past
        // the 25% line above. It still cost twenty-six seconds.
        //
        // Measured on a P30 on 2026-10-03. A person said "Hey B"; the transcript
        // was the seven characters "Hey, B."; the search matched heygen-automation,
        // heyreach-automation and heyzine-automation - because "hey" is a SUBSTRING
        // of all three - and the prompt went from about thirty tokens to 573.
        //
        //     prefill=26256 ms | decode=1018 ms | total=27381 ms
        //
        // Twenty-six seconds of prefill, one second of thinking. What the model pays
        // for is CHARACTERS IN THE PROMPT, so that is what this measures.
        using var store = new SqliteSkillStore(Db, identifyingMatchOnly: true);
        var builder = new SkillContextBuilder(store);

        var block = builder.BuildContextAsync(greeting).GetAwaiter().GetResult();
        var hits = store.SearchAsync(greeting).GetAwaiter().GetResult();

        _out.WriteLine($"[{greeting}] -> {hits.Count} hits, {block.Length} chars injected");
        foreach (var h in hits.Take(5)) _out.WriteLine($"    {h.Id}");

        // NOTHING, NOT "A LITTLE". There is no skill in a 1,378-skill library that
        // a person saying hello needs, and on this phone every 45 characters of
        // prompt is about a second they spend waiting.
        Assert.Equal(0, block.Length);
    }

}
