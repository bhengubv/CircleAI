// CapabilityRegistryTests.cs
//
// Two hundred features cannot live in one table.
//
// The first version of voice routing was exactly that - one list that had to
// know about every screen - and at ten entries it already showed the shape of
// the problem: more entries means more collisions and more hijacked questions.
// The registry only collects; each feature owns its own words, its own cost and
// its own readiness.
//
// So most of what is worth testing here is restraint: what it refuses to do.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

file sealed class Fake(string id, string title, string[] phrases,
                       Cost cost = Cost.Free, bool ready = true) : ICapability
{
    public string Id => id;
    public string Title => title;
    public IReadOnlyList<string> Phrases => phrases;
    public Cost Cost => cost;
    public bool Done { get; private set; }

    public Task<(bool Ready, string Why)> ReadyAsync(CancellationToken ct = default)
        => Task.FromResult((ready, ready ? "" : "not installed"));

    public Task<Did> DoAsync(Ask ask, CancellationToken ct = default)
    {
        Done = true;
        return Task.FromResult(new Did(true, $"did {id}"));
    }
}

public class CapabilityRegistryTests
{
    private static CapabilityRegistry With(params ICapability[] caps) => new(caps);

    [Fact]
    public void Finds_the_capability_somebody_asked_for()
    {
        var reg = With(new Fake("invoice", "Invoices", ["invoice", "bill"]));

        Assert.Equal("invoice", reg.Best("make an invoice")?.Id);
    }

    [Fact]
    public void Leaves_a_question_alone()
    {
        var reg = With(new Fake("invoice", "Invoices", ["invoice"]));

        Assert.Null(reg.Best("what is an invoice and when should I send one to a client"));
    }

    [Fact]
    public void A_tie_is_not_a_decision()
    {
        // TWO THINGS MATCHING EQUALLY WELL IS NOT SOMETHING TO SETTLE ON A COIN
        // TOSS. That is how a dispatcher does the wrong thing confidently. It
        // returns nothing, the turn answers normally, and the person says which
        // they meant - which is cheaper than being wrong.
        var reg = With(
            new Fake("a", "Invoices", ["report"]),
            new Fake("b", "Reports", ["report"]));

        Assert.Null(reg.Best("open report"));
        Assert.Equal(2, reg.Match("open report").Count);
    }

    [Fact]
    public void The_more_specific_capability_wins()
    {
        var reg = With(
            new Fake("cv", "Your CV", ["cv"]),
            new Fake("tailor", "Aim at a job", ["cv for this job"]));

        Assert.Equal("tailor", reg.Best("open cv for this job")?.Id);
    }

    [Fact]
    public void A_word_too_common_to_be_distinctive_is_ignored()
    {
        // The registry enforces the floor even if a feature declares a bad
        // phrase, because one careless capability must not be able to hijack
        // every sentence in the app.
        var reg = With(new Fake("you", "You", ["you", "me"]));

        Assert.Null(reg.Best("tell me about you"));
    }

    [Fact]
    public void Says_nothing_for_silence()
    {
        var reg = With(new Fake("a", "A", ["alpha"]));

        Assert.Empty(reg.Match(null));
        Assert.Empty(reg.Match("   "));
    }

    [Fact]
    public async Task A_capability_that_cannot_run_says_why()
    {
        // Catalogued, downloaded and unwired is exactly how this app spent weeks
        // able to translate and unable to speak. Offering it is the broken
        // promise; saying so is the feature.
        var cap = new Fake("tts", "Speaking", ["speak"], ready: false);

        var (ready, why) = await cap.ReadyAsync();

        Assert.False(ready);
        Assert.NotEmpty(why);
    }

    [Fact]
    public void Everything_is_browsable_even_when_it_has_no_phrases()
    {
        // Services stays the browse surface. A capability that cannot be named
        // distinctively gets no phrases and is still listed - deliberately
        // reachable by looking, not by asking.
        var reg = With(new Fake("quiet", "Something", []));

        Assert.Single(reg.All);
        Assert.Empty(reg.Match("something"));
    }

    [Theory]
    [InlineData(Cost.Free)]
    [InlineData(Cost.Draft)]
    [InlineData(Cost.Costly)]
    public void Every_capability_declares_what_being_wrong_costs(Cost cost)
    {
        // Declared by the feature, never inferred by the dispatcher: the only
        // thing that knows whether a misheard sentence may act silently is the
        // thing that would act.
        var cap = new Fake("x", "X", ["xylophone"], cost);

        Assert.Equal(cost, cap.Cost);
    }
}
