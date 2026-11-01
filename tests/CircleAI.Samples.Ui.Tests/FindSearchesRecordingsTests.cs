// FindSearchesRecordingsTests.cs
//
// OPEN-GAPS A7: Find searched memory and nothing else, while the app head kept every
// transcript it made - "Find had half a corpus to search", in MauiProgram's own words. These
// pin the other half: a kept recording's line is found, says which recording it came from,
// and does not push memory out of the list.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

public class FindSearchesRecordingsTests : TestContext
{
    IRenderedComponent<Find> Render(FakeRemembers memory, params SavedTranscript[] kept)
    {
        Services.AddSingleton<IRemembers>(memory);
        this.WireEverything();
        // After the wiring, which registers a store that keeps nothing: the last one wins.
        Services.AddSingleton<IKeepsTranscripts>(new Kept(kept));
        return RenderComponent<Find>();
    }

    static void Ask(IRenderedFragment cut, string what)
    {
        cut.Find("input.find-input").Input(what);
        cut.Find("button.find-go").Click();
    }

    static SavedTranscript Recording(string id, string title, string line) =>
        new(id, title, DateTimeOffset.UnixEpoch,
            new Transcript(line, [new TranscriptLine(line, TimeSpan.Zero, TimeSpan.FromSeconds(3))]));

    [Fact]
    public void A_line_from_a_kept_recording_is_found_and_says_which_recording()
    {
        var cut = Render(new FakeRemembers(),
            Recording("r1", "Clinic visit", "the nurse said to come back on Tuesday"));

        Ask(cut, "nurse");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.find-hit")));
        Assert.Equal("Clinic visit", cut.Find("span.find-hit-from").TextContent);
        Assert.Contains("come back on Tuesday", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_leads_and_the_recording_still_shows()
    {
        var memory = new FakeRemembers { Answers = [new Remembered("Thandi is the nurse")] };
        var cut = Render(memory, Recording("r1", "Clinic visit", "the nurse said Tuesday"));

        Ask(cut, "nurse");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li.find-hit").Count));
        // Enumerated, not indexed: bUnit's collection indexer misses a method in the AngleSharp
        // this suite resolves (MissingMethodException), while enumerating it works.
        var hits = cut.FindAll("li.find-hit").ToList();
        Assert.Contains("Thandi is the nurse", hits[0].TextContent, StringComparison.Ordinal);
        Assert.Empty(hits[0].QuerySelectorAll("span.find-hit-from"));
        Assert.Contains("Clinic visit", hits[1].TextContent, StringComparison.Ordinal);
    }

    private sealed class Kept(SavedTranscript[] saved) : IKeepsTranscripts
    {
        public Task<string> KeepAsync(string title, Transcript transcript, CancellationToken ct = default) => Task.FromResult("");
        public Task<IReadOnlyList<SavedTranscript>> AllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SavedTranscript>>(saved);
        public Task<SavedTranscript?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(saved.FirstOrDefault(s => s.Id == id));
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
