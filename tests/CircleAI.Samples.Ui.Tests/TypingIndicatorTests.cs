// TypingIndicatorTests.cs
//
// The wait, made visible.
//
// The reply bubble is added empty and streamed into, and the first token on a
// 0.6B on a P30 is ten seconds or more away. A blank bubble for ten seconds
// reads as a hang - "is it working?" - so the empty assistant bubble shows the
// three rising dots every messenger uses, replaced by the answer the instant a
// fragment lands.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

/// <summary>A brain that holds its answer until the test lets it go.</summary>
/// <remarks>
/// FakeBrain returns immediately, so the pending window never exists under it -
/// the very state this indicator is for. This one blocks in AskAsync until
/// Release(), so the empty bubble is on screen to assert against.
/// </remarks>
internal sealed class GatedBrain : IBrain
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Released { get; init; } = "Paris is the capital of France.";

    public void Release() => _gate.TrySetResult();

    public int MaxImageEdge => 512;

    public Task<BrainState> StateAsync(CancellationToken ct = default)
        => Task.FromResult(new BrainState(true, ""));

    public async Task<string> AskAsync(
        string prompt, Action<string>? token = null, CancellationToken ct = default)
    {
        await _gate.Task.ConfigureAwait(false);   // held open: the reply is pending
        token?.Invoke(Released);                   // then the answer streams in
        return Released;
    }

    public Task<string> AskWithToolsAsync(string prompt, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task<string> SeeAsync(
        string question, byte[] image, Action<string>? token = null, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}

public class TypingIndicatorTests : TestContext
{
    private GatedBrain Wire()
    {
        var brain = new GatedBrain();
        this.WireEverything();
        // AFTER WireEverything: the last AddSingleton for a type is the one
        // resolved, so this replaces the default FakeBrain.
        Services.AddSingleton<IBrain>(brain);
        return brain;
    }

    private void Send(IRenderedComponent<Chat> chat, string text)
    {
        chat.Find("input.input").Input(text);
        chat.Find("button.send").Click();
    }

    [Fact]
    public void A_pending_reply_shows_the_typing_dots()
    {
        var brain = Wire();
        var chat = RenderComponent<Chat>();

        // The composer is live once the brain reports ready.
        chat.WaitForAssertion(() =>
            Assert.False(chat.Find("button.send").HasAttribute("disabled")));

        Send(chat, "hello");

        // The answer is held open, so the assistant bubble is empty and the dots
        // stand in for it.
        chat.WaitForAssertion(() => Assert.NotEmpty(chat.FindAll(".typing")));
        Assert.DoesNotContain("Paris", chat.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dots_give_way_to_the_answer_the_moment_it_lands()
    {
        var brain = Wire();
        var chat = RenderComponent<Chat>();
        chat.WaitForAssertion(() =>
            Assert.False(chat.Find("button.send").HasAttribute("disabled")));

        Send(chat, "what is the capital of France");
        chat.WaitForAssertion(() => Assert.NotEmpty(chat.FindAll(".typing")));

        brain.Release();

        chat.WaitForAssertion(() =>
            Assert.Contains("Paris is the capital of France.", chat.Markup, StringComparison.Ordinal));

        // The dots are gone once there is real text to show.
        Assert.Empty(chat.FindAll(".typing"));
    }

    [Fact]
    public void Nothing_a_person_typed_ever_shows_the_dots()
    {
        // The indicator is for the ASSISTANT'S pending bubble only. A person's
        // own message is never empty and never pending, so it must never sprout
        // dots.
        var brain = Wire();
        var chat = RenderComponent<Chat>();
        chat.WaitForAssertion(() =>
            Assert.False(chat.Find("button.send").HasAttribute("disabled")));

        Send(chat, "hello");
        chat.WaitForAssertion(() => Assert.NotEmpty(chat.FindAll(".typing")));

        // Exactly one set of dots - the single pending assistant bubble - not one
        // against the user's line too.
        Assert.Single(chat.FindAll(".typing"));
    }
}
