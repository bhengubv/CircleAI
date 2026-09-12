// ThinkTagRoutingTests.cs
//
// What the phone actually shows when the model thinks out loud.
//
// THE ROUTER THAT DECIDES WHAT EVERY ANSWER LOOKS LIKE HAD NO TESTS, and the
// first message ever typed into the deployed app exposed it. On a P30,
// versionCode 7, "Remember that my clinic appointment moved to Friday" came
// back on screen as:
//
//     What was your original appointment time?
//     </think>
//
//     I don't need information about who the user is because I am just running
//     the app. What did they want me to check for them?
//
// A literal closing tag and the model's private monologue, rendered as the
// answer, on every single reply.
//
// THE CAUSE IS THE PROMPT, NOT THE MODEL. Qwen3's chat template pre-fills
// "<think>" at the END of the prompt, so the model generates the reasoning and
// the CLOSING tag and never the opening one. RouteUntil's content branch only
// ever searched for "<think>", so it stayed in Content mode and streamed the
// lot. Nothing was wrong with the reasoning router itself - it simply never
// started.
//
// These tests drive the router directly rather than through MNN, which is the
// point: this is arithmetic over a StringBuilder and needed no phone to check.

using System.Text;
using System.Threading;
using System.Threading.Channels;
using CircleAI.Core;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ThinkTagRoutingTests
{
    /// <summary>Feed text through the router and read back what each channel got.</summary>
    private static (string Content, string Reasoning) Route(
        string generated, bool includeReasoning = true)
    {
        var channel = Channel.CreateUnbounded<ChatFragment>();
        var sink = new MnnTokenSink
        {
            Model          = default!,
            Pending        = [],
            Emitted        = new StringBuilder(generated),
            StopSequences  = [],
            Writer         = channel.Writer,
            Ct             = CancellationToken.None,
            IncludeReasoning = includeReasoning,
        };

        MnnTokenRouter.DrainRemainder(sink);
        channel.Writer.Complete();

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        while (channel.Reader.TryRead(out var f))
            (f.Kind == ChatFragmentKind.Reasoning ? reasoning : content).Append(f.Text);

        return (content.ToString(), reasoning.ToString());
    }

    [Fact]
    public void A_closing_tag_with_no_opening_one_does_not_reach_the_screen()
    {
        // THE REGRESSION, in the shape the P30 produced it.
        var (content, reasoning) = Route(
            "What was your original appointment time?\n</think>\n\nFriday, then. Noted.");

        Assert.DoesNotContain("</think>", content);
        Assert.DoesNotContain("original appointment time", content);
        Assert.Contains("Friday, then. Noted.", content);

        // And it is not discarded - it is reasoning, and goes to that channel.
        Assert.Contains("original appointment time", reasoning);
    }

    [Fact]
    public void A_well_formed_block_still_routes_the_way_it_always_did()
    {
        var (content, reasoning) = Route("<think>weighing it up</think>The answer.");

        Assert.Equal("The answer.", content);
        Assert.Contains("weighing it up", reasoning);
    }

    [Fact]
    public void Reasoning_stays_hidden_when_the_caller_did_not_ask_for_it()
    {
        // The screen asks for content only. A stray closing tag must not become
        // a way for the monologue to arrive anyway.
        var (content, reasoning) = Route(
            "private deliberation\n</think>\n\nThe answer.", includeReasoning: false);

        Assert.Equal("The answer.", content.TrimStart());
        Assert.Empty(reasoning);
    }

    [Fact]
    public void An_answer_with_no_tags_at_all_is_untouched()
    {
        var (content, reasoning) = Route("Just an answer.");

        Assert.Equal("Just an answer.", content);
        Assert.Empty(reasoning);
    }

    [Fact]
    public void An_opening_tag_before_a_closing_one_wins()
    {
        // Both present and correctly ordered: the open tag decides, so the
        // stray-close path must not hijack a well-formed block.
        var (content, reasoning) = Route("before<think>inside</think>after");

        Assert.Equal("beforeafter", content);
        Assert.Equal("inside", reasoning);
    }

    [Fact]
    public void A_second_stray_close_does_not_swallow_the_answer()
    {
        // A model that emits the tag twice must not put the router into a state
        // where everything after it disappears.
        var (content, _) = Route("thinking\n</think>\nreal answer\n</think>\nmore answer");

        Assert.DoesNotContain("</think>", content);
        Assert.Contains("more answer", content);
    }

    [Fact]
    public void Text_before_an_unopened_block_is_still_reasoning()
    {
        // Everything up to the close came from inside the pre-filled block, so
        // none of it is the answer - including the first line.
        var (content, _) = Route("step one\nstep two\n</think>\nThe answer.");

        Assert.DoesNotContain("step one", content);
        Assert.DoesNotContain("step two", content);
        Assert.Contains("The answer.", content);
    }
}
