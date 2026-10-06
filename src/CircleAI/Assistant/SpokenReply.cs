// SpokenReply.cs
//
// Speaks an answer one sentence at a time, as it is still being written.
//
// THE GAP THIS CLOSES WAS MEASURED, NOT IMAGINED. On 2026-09-09 a Redmi 12 took
// 4,4 s to think and then 11,8 s to synthesise a 175-character reply as ONE
// block before a single sound played; a P30 the same day took 21,7 s to think
// and then started with a four-character chunk. Nothing was heard until the
// whole answer existed and the whole of it had been rendered. The owner's words
// for it: "big gaps between speech input and Circle AI response — we need
// shorter gaps."
//
// The answer arrives as fragments, and sentences are natural units of speech.
// So fragments are buffered, each sentence is handed on the moment its end is
// seen, and the voice works through them in order while the model goes on
// producing the next one. The first sentence is heard after ITS OWN cost, not
// after the paragraph's.
//
// TWO STAGES WHEN THE HOST CAN MANAGE IT. With a single "say" per sentence,
// sentence two cannot start rendering until sentence one has finished playing,
// which on a phone that renders slower than real time is a gap between every
// sentence. Given a render step and a play step separately, rendering runs one
// sentence ahead of playback, bounded so a long answer does not render itself
// to the end while the first sentence is still being heard.
//
// IN CONTRACTS BECAUSE THE ONLY HARD PART IS PURE TEXT. Deciding where a
// sentence ends inside a stream that is still growing is the whole difficulty,
// and it has to be testable - "3.5 percent" must not be cut at the dot, "Yes."
// must be spoken without waiting for more, and whatever is left when the model
// stops must still be said.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>Turns a streamed reply into speech, sentence by sentence, in order.</summary>
public sealed class SpokenReply : IAsyncDisposable
{
    private readonly CancellationToken _ct;
    private readonly Channel<string> _sentences = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly StringBuilder _pending = new();
    private readonly Task _speaking;
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    /// <summary>One stage: each sentence is rendered and played by one call.</summary>
    /// <param name="say">Speaks one sentence and returns when it has been said.</param>
    /// <param name="ct">Ends the whole reply — the sentence being spoken and every one queued.</param>
    public SpokenReply(Func<string, CancellationToken, Task> say, CancellationToken ct = default)
    {
        _ct = ct;
        _speaking = Task.Run(() => SayEachAsync(say), CancellationToken.None);
    }

    /// <summary>
    /// Two stages: sentences are rendered one ahead of the one being played.
    /// </summary>
    /// <param name="render">Renders one sentence, or returns null if it could not.</param>
    /// <param name="play">Plays one rendered sentence to the end.</param>
    /// <param name="ct">Ends the whole reply.</param>
    /// <param name="lookAhead">
    /// How many rendered sentences may wait to be played. One is enough to hide
    /// the render of the next behind the playback of this; more only spends
    /// work on sentences that will be thrown away if the person interrupts.
    /// </param>
    public SpokenReply(
        Func<string, CancellationToken, Task<string?>> render,
        Func<string, CancellationToken, Task> play,
        CancellationToken ct = default,
        int lookAhead = 1)
    {
        _ct = ct;
        _speaking = Task.Run(() => RenderAheadAsync(render, play, Math.Max(1, lookAhead)), CancellationToken.None);
    }

    /// <summary>Completes when the first sentence actually starts being spoken.</summary>
    /// <remarks>
    /// THE SIGNAL BARGE-IN WAS MISSING. A watcher armed when this object is
    /// CREATED is armed through the whole think gap - measured at 5 to 13
    /// seconds on 2026-09-09 - during which there is nothing to interrupt, and
    /// it cancelled replies before they were ever spoken. Waiting on this means
    /// the microphone only opens once there is sound to talk over.
    /// <para>
    /// Never completes if nothing is ever spoken, which is correct: a reply that
    /// produced no audio cannot be interrupted.
    /// </para>
    /// </remarks>
    public Task Started => _started.Task;

    /// <summary>How many sentences have been handed on so far.</summary>
    public int Queued { get; private set; }

    /// <summary>How many sentences have finished rendering (two-stage only).</summary>
    public int Rendered { get; private set; }

    /// <summary>How many sentences have been played to the end.</summary>
    public int Spoken { get; private set; }

    /// <summary>Adds the next fragment of the reply as it arrives.</summary>
    public void Push(string? fragment)
    {
        if (string.IsNullOrEmpty(fragment) || _completed) return;
        _pending.Append(fragment);
        foreach (var sentence in TakeComplete(_pending)) Enqueue(sentence);
    }

    /// <summary>
    /// The reply is finished: speak whatever is left, then wait for the last word.
    /// </summary>
    public Task CompleteAsync()
    {
        if (!_completed)
        {
            _completed = true;
            var rest = _pending.ToString().Trim();
            _pending.Clear();
            if (rest.Length > 0) Enqueue(rest);
            _sentences.Writer.TryComplete();
        }
        return _speaking;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Completing rather than abandoning: a turn that threw half-way still has
    /// sentences in flight, and the honest ending is to finish saying what was
    /// already decided, unless the token has said otherwise.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try { await CompleteAsync().ConfigureAwait(false); }
        catch { /* the turn is over either way */ }
    }

    private void Enqueue(string sentence)
    {
        Queued++;
        _sentences.Writer.TryWrite(sentence);
    }

    private async Task SayEachAsync(Func<string, CancellationToken, Task> say)
    {
        try
        {
            await foreach (var sentence in _sentences.Reader.ReadAllAsync(_ct).ConfigureAwait(false))
            {
                _started.TrySetResult();
                try { await say(sentence, _ct).ConfigureAwait(false); Spoken++; }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // ONE SENTENCE FAILING MUST NOT SILENCE THE REST. A voice that
                    // could not say a name should still say the sentence after it.
                }
            }
        }
        catch (OperationCanceledException) { /* asked to stop */ }
    }

    private async Task RenderAheadAsync(
        Func<string, CancellationToken, Task<string?>> render,
        Func<string, CancellationToken, Task> play,
        int lookAhead)
    {
        // Bounded, so rendering cannot run away from playback: a channel that
        // is full makes the renderer wait for the player to take one.
        var rendered = Channel.CreateBounded<string>(new BoundedChannelOptions(lookAhead)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
        });

        var renderer = Task.Run(async () =>
        {
            try
            {
                await foreach (var sentence in _sentences.Reader.ReadAllAsync(_ct).ConfigureAwait(false))
                {
                    string? item;
                    try { item = await render(sentence, _ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch { continue; }   // could not render this one; the next may be fine

                    if (item is null) continue;
                    Rendered++;
                    await rendered.Writer.WriteAsync(item, _ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* asked to stop */ }
            finally { rendered.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in rendered.Reader.ReadAllAsync(_ct).ConfigureAwait(false))
            {
                _started.TrySetResult();
                try { await play(item, _ct).ConfigureAwait(false); Spoken++; }
                catch (OperationCanceledException) { break; }
                catch { /* one bad file must not silence the rest */ }
            }
        }
        catch (OperationCanceledException) { /* asked to stop */ }

        try { await renderer.ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// Removes and returns every complete sentence at the front of the buffer,
    /// leaving whatever cannot yet be called finished.
    /// </summary>
    /// <remarks>
    /// A sentence ends at <c>. ! ? …</c> or a line break, but only when the next
    /// thing is whitespace or the buffer ends AFTER whitespace — a dot followed by
    /// a digit is "3.5", a dot at the very end of the buffer may be the model
    /// pausing mid-number, and neither is a place to start speaking.
    /// <para>
    /// The exception is a line break: a model that has moved to a new line has
    /// finished the old one, whatever it ended with.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> TakeComplete(StringBuilder buffer)
    {
        var text = buffer.ToString();
        var found = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var boundary =
                c == '\n'
                || ((c == '.' || c == '!' || c == '?' || c == '…')
                    && i + 1 < text.Length
                    && char.IsWhiteSpace(text[i + 1]));

            if (!boundary) continue;

            // Swallow a run of closing punctuation ("?!", "...") and quotes.
            var end = i + 1;
            while (end < text.Length && (text[end] is '.' or '!' or '?' or '…' or '"' or '\'' or ')'))
                end++;

            var sentence = text[start..end].Trim();
            if (sentence.Length > 0) found.Add(sentence);
            start = end;
        }

        if (start > 0)
        {
            var rest = text[start..];
            buffer.Clear();
            buffer.Append(rest);
        }

        return found;
    }
}
