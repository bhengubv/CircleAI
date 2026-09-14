// ToolCuesAreUsableTests.cs
//
// A static initialiser that ran in the wrong order, and cost four builds.
//
// AIService.ToolCues is a table of (phrase, servingWords) pairs, and the five
// string[] it referenced were declared BELOW it. C# runs static field
// initialisers in TEXTUAL ORDER, so every cue was constructed while all five
// arrays were still null. Every ToolCue therefore had Serves = null, and
// RelevantTools threw NullReferenceException on `cue.Serves.Length`.
//
// WHAT IT COST. On a P30 the first of the three suggestions the chat screen
// offers - "What is the weather like today?" - answered "Object reference not
// set to an instance of an object", every single time, and the whole agentic
// path was dead behind it. It compiled clean, raised no warning, and not one
// test in the suite noticed: nothing asked a tool-cue question through a real
// AIService, and the three separate guesses made at the cause by READING the
// source were all wrong. What found it was printing the exception.
//
// REFLECTION, DELIBERATELY. The table is private and should stay private; the
// thing worth pinning is not its contents but that it is USABLE at all, and no
// public surface exposes that. A test that reached it through behaviour would
// need a model.

using System.Reflection;
using CircleAI.Hosting;
using Xunit;

namespace CircleAI.Tests;

public class ToolCuesAreUsableTests
{
    static object[] Cues()
    {
        var field = typeof(AIService).GetField(
            "ToolCues", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(field is not null,
            "AIService.ToolCues has been renamed - this guard is now pointing at nothing");

        var value = field!.GetValue(null);
        Assert.True(value is not null, "AIService.ToolCues itself is null");

        return [.. ((Array)value!).Cast<object>()];
    }

    static T Read<T>(object cue, string name)
        => (T)cue.GetType().GetProperty(name)!.GetValue(cue)!;

    [Fact]
    public void There_are_cues_at_all()
        => Assert.NotEmpty(Cues());

    [Fact]
    public void No_cue_carries_a_null_serving_list()
    {
        // THE WHOLE BUG, IN ONE ASSERTION. Serves was null for every entry
        // because the arrays behind it had not been initialised yet.
        var broken = new List<string>();

        foreach (var cue in Cues())
        {
            var phrase = cue.GetType().GetProperty("Phrase")!.GetValue(cue) as string;
            var serves = cue.GetType().GetProperty("Serves")!.GetValue(cue);

            if (serves is null) broken.Add(phrase ?? "(no phrase)");
        }

        Assert.True(broken.Count == 0,
            $"{broken.Count} cues have a null Serves - the string[] they reference are "
            + $"declared BELOW ToolCues again, so they were still null when it was built. "
            + $"Affected: {string.Join(", ", broken.Take(8))}");
    }

    [Fact]
    public void No_cue_carries_an_empty_phrase()
    {
        // A cue whose phrase is "" matches every question ever asked, which
        // would buy a full tool catalogue - about ten seconds of prefill on a
        // P30 - for "hello".
        foreach (var cue in Cues())
            Assert.False(string.IsNullOrWhiteSpace(Read<string>(cue, "Phrase")));
    }

    [Fact]
    public void The_weather_cue_is_still_there_and_still_serves_something()
    {
        // Named because it is the one a person actually hit: it is the first
        // suggestion on an empty chat screen.
        var weather = Cues().FirstOrDefault(c =>
            string.Equals(Read<string>(c, "Phrase"), "weather", StringComparison.Ordinal));

        Assert.True(weather is not null, "the 'weather' cue has gone");
        Assert.NotNull(Read<string[]>(weather!, "Serves"));
        Assert.NotEmpty(Read<string[]>(weather!, "Serves"));
    }
}
