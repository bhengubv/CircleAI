// AskFrontGenerator.cs
//
// THE SERVICE'S BRAIN, BEHIND ITS ONE DOOR, AS THE GENERATOR A LIBRARY EXPECTS.
//
// The translation engine (LlmTranslationEngine) was written to sit on an IChatGenerator, and the
// service's brain is not one: it is a model in its own process, reached only through the ask
// queue, which keeps one answer at a time, waits for a cold brain, and survives the model's
// process dying. A translation that went around the queue would collide with the question being
// answered ("an answer is already being written") or find no model loaded at all. So the engine
// gets the queue wearing the interface it asked for, and every call is an ask like any other -
// queued, recorded, cancellable, timed - on whichever brain the device chose (BrainPlan), the C#
// engine's Bonsai 2 on the P30.
//
// PLAIN, ALWAYS. A raw generator takes the messages as given and adds nothing; that is the
// contract, and the engine's prompt is a whole prompt. So each call is a plain ask: no persona,
// no notes, no tools, no shortcut answering "hello" with a greeting, no answer guard.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;

namespace CircleAI.Assistant.Requests;

/// <summary>An <see cref="IChatGenerator"/> over an <see cref="IAskFront"/>: each call is one plain ask.</summary>
public sealed class AskFrontGenerator : IChatGenerator
{
    private readonly IAskFront _front;
    private readonly string _caller;
    private readonly AskLane _lane;
    private readonly TimeSpan _wait;

    /// <param name="front">The door: the service's request coordinator.</param>
    /// <param name="callerKey">Whose ask it is - the app that asked, as the service identified it.</param>
    /// <param name="lane">Which queue it waits in.</param>
    /// <param name="wait">
    /// How long a call waits for its answer before it gives up and withdraws the ask - nobody
    /// is left to read an answer that comes later. Null waits three minutes: a cold load and a
    /// sentence's worth of words on the P30.
    /// </param>
    public AskFrontGenerator(IAskFront front, string callerKey, AskLane lane = AskLane.Foreground, TimeSpan? wait = null)
    {
        _front = front ?? throw new ArgumentNullException(nameof(front));
        ArgumentException.ThrowIfNullOrWhiteSpace(callerKey);
        _caller = callerKey;
        _lane = lane;
        _wait = wait is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(3);
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
    {
        var id = Submit(messages);
        AskRecord? done;
        try
        {
            // THE WAIT ENDS QUIETLY WHEN THE CALLER GIVES UP - it hands back the record as it
            // stands - so the caller's cancellation is checked here, and the ask withdrawn.
            done = await _front.WaitAsync(_caller, id, _wait, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _front.Cancel(_caller, id);
            throw;
        }
        return Answer(id, done);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = Submit(messages);
        var giveUp = DateTimeOffset.UtcNow + _wait;
        var sent = 0;
        long seq = -1;
        var finished = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var left = giveUp - DateTimeOffset.UtcNow;
                if (left <= TimeSpan.Zero) break;

                // NEWS AS IT COMES: the record's text grows while it is answered.
                var now = await _front.WaitForChangeAsync(_caller, id, seq, left < TimeSpan.FromSeconds(2) ? left : TimeSpan.FromSeconds(2), ct)
                                      .ConfigureAwait(false);
                if (now is null) break;
                seq = now.Seq;

                // A RETRY AFTER A CRASH STARTS THE ANSWER AGAIN; what was streamed stands, and
                // only text past it is new.
                if (now.Text.Length > sent && (now.State == AskState.Running || now.State == AskState.Succeeded))
                {
                    var piece = now.Text[sent..];
                    sent = now.Text.Length;
                    yield return piece;
                }

                if (now.IsFinal)
                {
                    finished = true;
                    if (now.State != AskState.Succeeded) Answer(id, now); // throws the reason
                    yield break;
                }
            }
        }
        finally
        {
            // LEFT EARLY - the reader stopped, or the wait ran out: nobody is left to read the rest.
            if (!finished) _front.Cancel(_caller, id);
        }
        throw new TimeoutException("Circle AI is still working on that.");
    }

    /// <summary>Nothing to dispose: the brain belongs to the service.</summary>
    public void Dispose() { }

    // One plain ask for the conversation's last user message, which is the whole prompt here:
    // the libraries that sit on a generator build one self-contained message, and the ask holds one.
    private string Submit(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        string? message = null;
        for (var i = messages.Count - 1; i >= 0 && message is null; i--)
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase)) message = messages[i].Content;
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("There is no user message to ask.", nameof(messages));

        var admission = _front.Submit(new AskSubmission(
            CallerKey: _caller,
            ClientRequestId: Guid.NewGuid().ToString("N"),
            SessionId: string.Empty,
            Lane: _lane,
            Message: message,
            Plain: true));
        if (!admission.Accepted || admission.Record is null)
            throw new InvalidOperationException(admission.Refusal ?? AskSentences.Busy);
        return admission.Record.Id;
    }

    // The answer, or the reason there is none - in the queue's own sentence, which a person can read.
    private string Answer(string id, AskRecord? done)
    {
        if (done is null) throw new InvalidOperationException(AskSentences.CouldNotAnswer);
        if (!done.IsFinal)
        {
            _front.Cancel(_caller, id);
            throw new TimeoutException(done.Sentence ?? "Circle AI is still working on that.");
        }
        return done.State == AskState.Succeeded
            ? done.Text
            : throw new InvalidOperationException(done.Sentence ?? AskSentences.CouldNotAnswer);
    }
}
