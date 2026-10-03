// OnByDefaultTests.cs
//
// Nobody should have to switch the brain on.
//
// Circle AI is two packages and the thin app holds no model, so the brain only
// ever started when something asked it a question. Open the app after an update, a
// reboot, or EMUI closing a foreground service, and it said
//
//     Circle AI stopped - this phone closed it.   [Turn it on]
//
// which is the app asking a person to perform its own startup. The standard is the
// other way round: everything on by default, and the UI's job is to say where each
// thing lives, how to turn it off, and what that costs.
//
// Binding IS starting - ConnectAsync binds with AutoCreate - so asking the brain
// how it is brings it up. This pins that the asking happens at all.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Layout;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Samples.Ui.Tests;

public sealed class OnByDefaultTests : TestContext
{
    [Fact]
    public async Task Opening_the_app_starts_the_brain_without_anybody_pressing_anything()
    {
        var brain = new FakeBrain();
        this.WireEverything();
        Services.AddSingleton<IBrain>(brain);   // last wins over the default

        RenderComponent<MainLayout>();

        // FIRE AND FORGET, so it is off the render thread - every screen already
        // handles not-ready and a brain that cannot start must not stop the app
        // drawing. Which is exactly why this needs a moment rather than an assert
        // on the synchronous path.
        for (var i = 0; i < 50 && brain.StateAsks == 0; i++)
            await Task.Delay(10);

        Assert.True(brain.StateAsks > 0,
            "the layout never asked the brain for its state, so nothing starts it "
            + "and the person is back to pressing Turn it on");
    }
}
