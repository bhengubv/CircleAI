// EveryPageRendersTests.cs
//
// Every screen, rendered once, against a container that holds everything.
//
// THIS IS THE TEST THAT WOULD HAVE FOUND IT. Settings.razor injects
// IResidentAssistant, and the Web head never registered it - so that page threw
// instead of rendering on one of the three heads this UI is shared by. Nobody
// noticed for weeks, and it was found by grepping every injection against every
// Program.cs by hand.
//
// A page that renders is not a page that works. But a page that CANNOT render is
// never worth debugging further, and that is cheap to know.

using Bunit;
using CircleAI.Samples.It.Shared.Pages;

namespace CircleAI.Samples.It.Ui.Tests;

public class EveryPageRendersTests : TestContext
{
    public EveryPageRendersTests() => this.WireEverything();

    [Fact] public void Loading()   => Assert.NotNull(RenderComponent<Loading>().Markup);
    [Fact] public void Home()      => Assert.NotEmpty(RenderComponent<Home>().Markup);
    [Fact] public void Services()  => Assert.NotEmpty(RenderComponent<Services>().Markup);
    [Fact] public void Settings()  => Assert.NotEmpty(RenderComponent<Settings>().Markup);
    [Fact] public void Translate() => Assert.NotEmpty(RenderComponent<Translate>().Markup);
    [Fact] public void Languages() => Assert.NotEmpty(RenderComponent<Languages>().Markup);
    [Fact] public void Abilities() => Assert.NotEmpty(RenderComponent<Abilities>().Markup);
    [Fact] public void Career()    => Assert.NotEmpty(RenderComponent<Career>().Markup);
    [Fact] public void JobSpec()   => Assert.NotEmpty(RenderComponent<JobSpec>().Markup);
    [Fact] public void Chat()      => Assert.NotEmpty(RenderComponent<Chat>().Markup);
    [Fact] public void You()       => Assert.NotEmpty(RenderComponent<You>().Markup);
    [Fact] public void WakeWord()  => Assert.NotEmpty(RenderComponent<WakeWord>().Markup);
    [Fact] public void Setup()     => Assert.NotEmpty(RenderComponent<Setup>().Markup);
    [Fact] public void NotFound()  => Assert.NotEmpty(RenderComponent<NotFound>().Markup);
}
