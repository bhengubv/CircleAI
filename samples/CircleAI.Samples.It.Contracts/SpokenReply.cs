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
// So fragments are buffered, each sentence is handed to the voice the moment its
// end is seen, and the voice works through them in order while the model goes on
// producing the next one. The first sentence is heard after ITS OWN cost, not
// after the paragraph's.
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

namespace CircleAI.Samples.It;

/// <summary>Turns a streamed reply into speech, sentence by sentence, in order.</summary>
public sealed class SpokenReply : IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task> _say;
    private readonly CancellationToken _ct;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly StringBuilder _pending = new();
    private readonly Task _speaking;
    private bool _completed;

    /// <param name="say">Speaks one sentence and returns when it has been said.</param>
    /// <param name="ct">Ends the whole reply — the sentence being spoken and every one queued.</param>
    public SpokenReply(Func<string, CancellationToken, Task> say, CancellationToken ct = default)
    {
        _say = say;
        _ct = ct;
        _speaking = Task.Run(SpeakAllAsync, CancellationToken.None);
    }

    /// <summary>How many sentences have been handed to the voice so far.</summary>
    public int Queued { get; private set; }

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
            _queue.Writer.TryComplete();
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
        _queue.Writer.TryWrite(sentence);
    }

    private async Task SpeakAllAsync()
    {
        try
        {
            await foreach (var sentence in _queue.Reader.ReadAllAsync(_ct).ConfigureAwait(false))
            {
                try { await _say(sentence, _ct).ConfigureAwait(false); }
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
