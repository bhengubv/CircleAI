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
}
