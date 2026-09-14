// FindPageTests.cs
//
// Searching what the phone has been told.
//
// IRemembers.RecallAsync had been reachable only from a diagnostic screen, so
// everything the assistant learned accumulated where nobody could look at it.
// This page is the way in. It had no tests.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

/// <summary>A store that answers with whatever the test put in it.</summary>
internal sealed class FakeRemembers : IRemembers
{
    public IReadOnlyList<Remembered> Answers { get; init; } = [];

    /// <summary>What was actually asked for, so a test can check it was trimmed.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Thrown instead of answering, when a test wants the failure path.</summary>
    public Exception? Fails { get; init; }

    public Task<IReadOnlyList<Remembered>> RecallAsync(
        string about, int limit = 4, CancellationToken ct = default)
    {
        Asked.Add(about);
        return Fails is not null
            ? Task.FromException<IReadOnlyList<Remembered>>(Fails)
            : Task.FromResult(Answers);
    }

    public Task LearnAsync(string said, CancellationToken ct = default) => Task.CompletedTask;
}

public class FindPageTests : TestContext
{
    FakeRemembers Wire(FakeRemembers? store = null)
    {
        var fake = store ?? new FakeRemembers();
        Services.AddSingleton<IRemembers>(fake);
        this.WireEverything();
        return fake;
    }

    void Ask(IRenderedFragment cut, string what)
    {
        cut.Find("input.find-input").Input(what);
        cut.Find("button.find-go").Click();
    }

    [Fact]
    public void What_was_typed_reaches_the_store_trimmed()
    {
        var store = Wire();
        var cut = RenderComponent<Find>();

        Ask(cut, "  school fees  ");

        Assert.Equal(["school fees"], store.Asked);
    }

    [Fact]
    public void Everything_found_is_on_the_screen()
    {
        Wire(new FakeRemembers
        {
            Answers = [new Remembered("Her name is Thandi"), new Remembered("Rent is due on the 3rd")],
        });

        var cut = RenderComponent<Find>();
        Ask(cut, "thandi");

        Assert.Equal(2, cut.FindAll("li.find-hit").Count);
        Assert.Contains("Thandi", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Rent", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_found_teaches_rather_than_alarms()
    {
        // AN EMPTY RESULT IS NOT AN ERROR AND MUST NOT READ LIKE ONE. A phone
        // that has been talked to twice has almost nothing to recall, and
        // "nothing matched" on a blank screen reads as broken.
        Wire();
        var cut = RenderComponent<Find>();

        Ask(cut, "anything");

        Assert.Contains("Things you tell it show up here", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_that_will_not_open_says_so_in_words()
    {
        // NOT A TYPE NAME. A store that cannot be opened and a store with
        // nothing in it are different problems, and the screen has to be able to
        // tell somebody which one they have.
        Wire(new FakeRemembers { Fails = new UnauthorizedAccessException("Permission denied") });
        var cut = RenderComponent<Find>();

        Ask(cut, "anything");

        Assert.DoesNotContain("UnauthorizedAccessException", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("find-status", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_box_cannot_be_searched()
    {
        // Otherwise pressing Find on a blank screen asks the store for "" and
        // returns whatever the first twenty rows happen to be, which reads as
        // the phone volunteering things nobody asked about.
        var store = Wire();
        var cut = RenderComponent<Find>();

        Assert.True(cut.Find("button.find-go").HasAttribute("disabled"));

        cut.Find("input.find-input").Input("   ");
        Assert.True(cut.Find("button.find-go").HasAttribute("disabled"));
        Assert.Empty(store.Asked);
    }

    [Fact]
    public void A_certain_memory_is_not_labelled_with_a_percentage()
    {
        // HOW SURE, ONLY WHEN IT IS WORTH SAYING. Printing "100% sure" beside
        // something the person stated outright is noise on every row.
        Wire(new FakeRemembers { Answers = [new Remembered("Rent is due on the 3rd", 1.0)] });
        var cut = RenderComponent<Find>();

        Ask(cut, "rent");

        Assert.Empty(cut.FindAll("span.find-hit-sure"));
    }

    [Fact]
    public void An_inferred_memory_says_how_sure_it_is()
    {
        Wire(new FakeRemembers { Answers = [new Remembered("She may work in Durban", 0.6)] });
        var cut = RenderComponent<Find>();

        Ask(cut, "durban");

        Assert.Single(cut.FindAll("span.find-hit-sure"));
    }
}
