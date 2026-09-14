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
// AND THEN IT DID NOT FIND THE NEXT THREE, because it was a hand-written list of
// fourteen [Fact]s and three pages were added without anybody adding a line.
// Find, Seeing and Music had no coverage at all; Transcribe had none either. The
// container could not even have built them - IMakesMusic, IPlaysMedia and
// IKeepsTranscripts were all missing from WireEverything - so the two ways of
// not noticing were pointing at each other, each looking like the other's job.
//
// So the list is gone. This asks the assembly which components carry a route,
// which is the same question the Router asks at runtime: a page that is
// reachable in the app is a page that is tested here, and adding one is not
// something anybody can forget to do.
//
// A page that renders is not a page that works. But a page that CANNOT render is
// never worth debugging further, and that is cheap to know.

using System.Reflection;
using Bunit;
using CircleAI.Samples.Shared.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace CircleAI.Samples.Ui.Tests;

public class EveryPageRendersTests : TestContext
{
    public EveryPageRendersTests() => this.WireEverything();

    /// <summary>Every routable component in the shared page library.</summary>
    /// <remarks>
    /// THE SAME QUESTION THE ROUTER ASKS. Routes.razor sets AppAssembly from a
    /// type in this assembly and Blazor then scans it for RouteAttribute; this
    /// scans the same assembly for the same attribute, so the set here and the
    /// set a person can navigate to cannot drift apart.
    /// </remarks>
    static IReadOnlyList<Type> Routable { get; } =
        [.. typeof(Home).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false })
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any())
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    public static TheoryData<Type> EveryRoutablePage()
    {
        var data = new TheoryData<Type>();
        foreach (var page in Routable) data.Add(page);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRoutablePage))]
    public void It_renders(Type page)
    {
        // Rendered by Type rather than by generic argument, because the whole
        // point is that this file does not name the pages.
        var cut = Render(builder =>
        {
            builder.OpenComponent(0, page);
            builder.CloseComponent();
        });

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public void The_scan_actually_found_the_screens()
    {
        // A REFLECTION QUERY THAT MATCHES NOTHING PASSES EVERY THEORY IT FEEDS.
        // Without this, renaming the pages namespace or moving Home would turn
        // the whole file green and silent, which is worse than the hand-written
        // list it replaced.
        Assert.True(Routable.Count >= 15,
            $"only {Routable.Count} routable pages found - the scan is probably broken");

        // The three that had no coverage at all, named here so a future refactor
        // that quietly drops one has to argue with a test.
        Assert.Contains(typeof(Find), Routable);
        Assert.Contains(typeof(Seeing), Routable);
        Assert.Contains(typeof(Music), Routable);
        Assert.Contains(typeof(Transcribe), Routable);
    }
}
