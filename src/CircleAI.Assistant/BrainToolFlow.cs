// BrainToolFlow.cs
//
// THE ONE PLACE A TURN DECIDES WHETHER IT NEEDED A TOOL.
//
// Two surfaces answer questions - the typed chat screen and the spoken turn -
// and both want the same thing: stream the reply so a word is on screen or in
// the air within a second or two, but when the model reaches for a tool, say
// none of the raw call and re-run through the executor that actually runs it.
//
// That flow lived in DeviceConversation (voice) and NOWHERE on the typed path,
// so a calculator question typed into the chat screen was streamed straight to
// the bubble - the model's own <tool_call> JSON, or its wrong head-arithmetic -
// and the tool never ran, because nothing on that path was listening for a call.
// The voice path had the logic; the text path had a copy of half of it.
//
// A second copy of this decision is the exact defect this repo keeps finding:
// two owners of one behaviour drift, and the same question gets answered two
// different ways depending on whether it was typed or spoken. So it is written
// once, here, over the two IBrain methods that already exist (AskAsync to
// stream, AskWithToolsAsync to execute), and both surfaces call it.
//
// In CircleAI.Assistant on purpose: it has zero project references because a
// browser loads it, so the web head gets the same flow as the phone from the
// same code.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>
/// The shared "stream, and escalate to tools only when the model asks for one"
/// turn, over <see cref="IBrain"/>.
/// </summary>
public static class BrainToolFlow
{
    /// <summary>
    /// Answer <paramref name="asked"/>, streaming live fragments to
    /// <paramref name="onFragment"/> until - and only until - the model begins a
    /// tool call. If it does, nothing more is streamed; the turn re-runs through
    /// <see cref="IBrain.AskWithToolsAsync"/>, which actually invokes the tool and
    /// answers from its result, and the returned reply carries that answer with
    /// <see cref="ToolAwareReply.UsedTools"/> set - the signal to the caller to
    /// REPLACE whatever it showed or spoke live. If no tool is called, the reply
    /// is the streamed text the caller already has in hand.
    /// </summary>
    /// <param name="brain">The head that answers.</param>
    /// <param name="asked">The composed prompt, exactly as it would be streamed.</param>
    /// <param name="onFragment">
    /// Called with each fragment that is safe to show or speak - that is, every
    /// fragment before a tool call begins. Never called with any part of the call
    /// itself.
    /// </param>
    /// <param name="onToolStarted">
    /// Fired once, the moment a tool call is detected and streaming stops, so a
    /// surface can say "looking it up" before the slower second pass and clear the
    /// partial head it may already have shown. Optional.
    /// </param>
    /// <param name="ct">Cancellation for the whole turn.</param>
    /// <remarks>
    /// WHY WATCH THE STREAM RATHER THAN ALWAYS TAKE THE AGENTIC PATH: streaming is
    /// what gets the first word out in the second or two a person will wait, and
    /// the great majority of turns need no tool. The agentic path cannot stream -
    /// it must see the whole reply to know whether it was a tool call - so paying
    /// its latency on every turn to catch the few that reach the world is the
    /// wrong trade. Stream, and earn the second pass back only when the model asks.
    /// </remarks>
    public static async Task<ToolAwareReply> AskMaybeToolsAsync(
        this IBrain brain,
        string asked,
        Action<string> onFragment,
        Action? onToolStarted = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(onFragment);

        var streamed = new StringBuilder();
        var watch = new StringBuilder();
        var calling = false;

        await brain.AskAsync(asked, fragment =>
        {
            // NOTHING AFTER THE CALL BEGINS IS SHOWN OR SPOKEN. What is already
            // out has been seen or heard and cannot be taken back, which is why
            // the detector reads only the head of the stream - see ToolCall.Head.
            if (calling) return;

            watch.Append(fragment);
            if (ToolCall.Looks(watch))
            {
                calling = true;
                onToolStarted?.Invoke();
                return;
            }

            streamed.Append(fragment);
            onFragment(fragment);
        }, ct).ConfigureAwait(false);

        if (!calling)
            return new ToolAwareReply(streamed.ToString(), UsedTools: false);

        // THE SECOND PASS. The streamed answer was a tool call and is void; the
        // executor runs the call, feeds the result back, and answers from it.
        var tooled = await brain.AskWithToolsAsync(asked, ct).ConfigureAwait(false);

        // EMPTY MEANS THIS HEAD HAS NO TOOLS WIRED (the interface documents that a
        // head may return the empty string). Replacing a real streamed answer with
        // nothing would turn "cannot search" into "says nothing", so keep whatever
        // streamed rather than blanking the turn.
        return string.IsNullOrWhiteSpace(tooled)
            ? new ToolAwareReply(streamed.ToString(), UsedTools: false)
            : new ToolAwareReply(tooled, UsedTools: true);
    }
}

/// <summary>The outcome of a turn that may or may not have used a tool.</summary>
/// <param name="Text">The answer to show or speak.</param>
/// <param name="UsedTools">
/// True when the model called a tool and <see cref="Text"/> is the re-run,
/// tool-grounded answer that must REPLACE anything shown or spoken live; false
/// when no tool ran and <see cref="Text"/> is simply the streamed text the caller
/// already forwarded.
/// </param>
public readonly record struct ToolAwareReply(string Text, bool UsedTools);
