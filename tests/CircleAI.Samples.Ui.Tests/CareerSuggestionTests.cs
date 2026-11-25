using Bunit;
using CircleAI.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CareerScreen = CircleAI.Samples.Shared.Pages.Career;

namespace CircleAI.Samples.Ui.Tests;

// The suggest-and-confirm path on the CV screen: the model only ever PROPOSES, and
// the person's tap decides which words are stored. Driven with a fake that RETURNS a
// suggestion, because the default fake returns null - which renders the screen exactly
// as before and never exercises the offer at all. That gap is how a feature ships
// "wired but never run".
public class CareerSuggestionTests : TestContext
{
    private FakeCareer Wire(string? suggestion)
    {
        this.WireEverything();
        var career = new FakeCareer();
        // Last registration wins, so these replace the defaults WireEverything set.
        Services.AddSingleton<ICareerInterview>(career);
        Services.AddSingleton<ICareerArticulator>(new FakeArticulator { Suggestion = suggestion });
        return career;
    }

    [Fact]
    public void It_offers_a_clearer_wording_without_storing_anything_yet()
    {
        var career = Wire("Motor mechanic");
        var cut = RenderComponent<CareerScreen>();

        cut.Find("input.answer").Input("i fix cars");
        cut.Find("button.btn-primary").Click();   // Next

        // The offer is on screen, their own words are still shown, and nothing has
        // been committed to the CV - the choice is still theirs.
        cut.WaitForAssertion(() => Assert.Contains("Motor mechanic", cut.Markup));
        Assert.Contains("A clearer way to say it", cut.Markup);
        Assert.Contains("i fix cars", cut.Markup);
        Assert.Null(career.Stored);
    }

    [Fact]
    public void Use_this_stores_the_models_wording()
    {
        var career = Wire("Motor mechanic");
        var cut = RenderComponent<CareerScreen>();

        cut.Find("input.answer").Input("i fix cars");
        cut.Find("button.btn-primary").Click();                 // Next -> offer
        cut.WaitForAssertion(() => Assert.Contains("Motor mechanic", cut.Markup));
        cut.Find("button.btn-primary").Click();                 // Use this

        cut.WaitForAssertion(() => Assert.Equal("Motor mechanic", career.Stored));
    }

    [Fact]
    public void Keep_mine_stores_the_persons_own_words()
    {
        var career = Wire("Motor mechanic");
        var cut = RenderComponent<CareerScreen>();

        cut.Find("input.answer").Input("i fix cars");
        cut.Find("button.btn-primary").Click();                 // Next -> offer
        cut.WaitForAssertion(() => Assert.Contains("Motor mechanic", cut.Markup));
        cut.Find("button.btn-soft").Click();                    // Keep mine

        cut.WaitForAssertion(() => Assert.Equal("i fix cars", career.Stored));
    }

    [Fact]
    public void No_suggestion_stores_the_answer_and_moves_on()
    {
        // The honest default: a fact field or a busy model yields null, and Next
        // behaves exactly as it always did - store and advance, with no offer.
        var career = Wire(null);
        var cut = RenderComponent<CareerScreen>();

        cut.Find("input.answer").Input("Thabo Mokoena");
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Equal("Thabo Mokoena", career.Stored));
        Assert.DoesNotContain("A clearer way to say it", cut.Markup);
    }
}
