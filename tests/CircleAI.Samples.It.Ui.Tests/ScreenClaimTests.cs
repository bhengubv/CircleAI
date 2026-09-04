// ScreenClaimTests.cs
//
// The six screens that had nothing but "it rendered".
//
// A PAGE THAT RENDERS IS NOT A PAGE THAT WORKS - EveryPageRendersTests says so
// in its own header, and then that is all any of these six got. Rendering
// catches a missing registration and nothing else: every defect this project was
// created for rendered perfectly. "78 languages, spoken out loud" rendered on a
// build that could speak one. A circle offering translation rendered beside a
// bar that did not offer it.
//
// So these ask the next question instead: given what the host reported, does the
// screen say something TRUE about it. That is the question a screenshot cannot
// answer and a claim can be wrong about.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class ServicesScreenTests : TestContext
{
    public ServicesScreenTests() => this.WireEverything();

    [Fact]
    public void It_counts_what_it_actually_lists()
    {
        // THE CLAIM AT THE TOP OF THE SCREEN. "N things it can help you with" is
        // a number a person will believe, and a count that drifts from the
        // catalogue is the same class of lie as "78 languages, spoken out loud"
        // on a build that could speak one.
        var services = RenderComponent<Services>();

        var listed = Capabilities.All.Sum(g => g.Items.Length);
        Assert.Contains(listed.ToString(), services.Find(".intro").TextContent);
    }

    [Fact]
    public void Every_group_says_how_many_are_in_it()
    {
        var services = RenderComponent<Services>();

        var heads = services.FindAll(".panel-count").ToList();
        Assert.Equal(Capabilities.All.Count, heads.Count);

        for (var i = 0; i < heads.Count; i++)
            Assert.Equal(
                Capabilities.All[i].Items.Length.ToString(),
                heads[i].TextContent.Trim());
    }

    [Fact]
    public void A_group_opens_to_show_its_tiles()
    {
        // Services is the BROWSE side - the circle does the work now - so the
        // one thing it owes is that looking around actually works.
        var services = RenderComponent<Services>();
        Assert.Empty(services.FindAll(".tile"));

        services.FindAll(".panel-head").ToList()[0].Click();

        Assert.Equal(Capabilities.All[0].Items.Length, services.FindAll(".tile").Count);
    }
}

public class ChatScreenTests : TestContext
{
    [Fact]
    public void It_says_what_is_missing_rather_than_inviting_a_question()
    {
        // A TEXT BOX ON A PHONE WITH NO MODEL IS A PROMISE IT CANNOT KEEP. The
        // reason has to be the thing on screen, because "Ask it something"
        // followed by silence is indistinguishable from a broken app.
        this.WireEverything();
        Services.AddSingleton<IBrain>(new FakeBrain
        {
            Ready = false,
        });

        var chat = RenderComponent<Chat>();

        chat.WaitForAssertion(() =>
            Assert.Contains("Not ready yet", chat.Find(".empty").TextContent));
    }

    [Fact]
    public void A_ready_phone_is_invited_to_ask()
    {
        this.WireEverything();

        var chat = RenderComponent<Chat>();

        chat.WaitForAssertion(() =>
            Assert.Contains("Ask it something", chat.Find(".empty").TextContent));
    }
}

public class YouScreenTests : TestContext
{
    [Fact]
    public void An_empty_profile_says_nothing_is_filled_in()
    {
        // The percentage is a claim about the person's own data, and 0% with a
        // reason beats a bare 0%.
        this.WireEverything();

        var you = RenderComponent<You>();

        you.WaitForAssertion(() => Assert.Contains("0%", you.Find(".done").TextContent));
        Assert.Contains("nothing yet", you.Find(".missing").TextContent);
    }

    [Fact]
    public void A_section_opens_when_it_is_asked_to()
    {
        this.WireEverything();

        var you = RenderComponent<You>();
        var folds = you.FindAll(".fold").ToList();
        Assert.NotEmpty(folds);

        folds[0].Click();

        Assert.Contains("fold-on", you.FindAll(".fold").ToList()[0].ClassName);
    }
}

public class JobSpecScreenTests : TestContext
{
    public JobSpecScreenTests() => this.WireEverything();

    [Fact]
    public void It_will_not_run_on_an_empty_advert()
    {
        // Otherwise the button spends a model load on nothing and comes back
        // with an answer about no job at all.
        var spec = RenderComponent<JobSpec>();

        Assert.True(spec.Find("button.go").HasAttribute("disabled"));
    }

    [Fact]
    public void Pasting_an_advert_arms_the_button()
    {
        var spec = RenderComponent<JobSpec>();

        spec.Find("textarea.spec").Input("Senior plumber, must own tools.");

        Assert.False(spec.Find("button.go").HasAttribute("disabled"));
    }

    [Fact]
    public void A_head_with_no_model_says_so_instead_of_producing_a_CV()
    {
        // FakeTailor reports what a browser head genuinely reports: it cannot.
        // Saying that is the feature; a blank result is the bug.
        var spec = RenderComponent<JobSpec>();

        spec.Find("textarea.spec").Input("Senior plumber, must own tools.");
        spec.Find("button.go").Click();

        spec.WaitForAssertion(() => Assert.Contains("no model", spec.Find(".out").TextContent));
    }
}

public class SetupScreenTests : TestContext
{
    [Fact]
    public void A_finished_setup_does_not_offer_to_download_nothing()
    {
        // FakeSetup reports an empty plan - everything asked for is already on
        // the phone. A live Start button would then spend a tap, a spinner and a
        // person's attention on downloading no files.
        this.WireEverything();

        var setup = RenderComponent<Setup>();

        setup.WaitForAssertion(() =>
            Assert.True(setup.Find("button.start").HasAttribute("disabled")));
    }
}

public class CareerScreenTests : TestContext
{
    [Fact]
    public void It_asks_the_question_the_host_gave_it()
    {
        // The interview is driven entirely by the host, so the screen showing
        // its OWN question would mean the two had come apart - and a CV built
        // from answers to questions nobody asked is worse than no CV.
        this.WireEverything();

        var career = RenderComponent<Career>();

        career.WaitForAssertion(() => Assert.Contains("What do you do?", career.Markup));
    }
}
