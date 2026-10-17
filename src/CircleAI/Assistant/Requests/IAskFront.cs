// IAskFront.cs
//
// THE ONE DOOR QUESTIONS COME IN BY. The link service (other apps), the wake turn
// (somebody spoke to the phone), the assistant session (the OS's assist gesture) and
// the desktop's loopback endpoint all submit here, and nothing else talks to the
// brain. That is what makes "a question during model loading waits in the queue",
// "a retry does not duplicate work" and "two apps cannot see each other's
// conversation" true everywhere at once instead of in whichever surface remembered.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant.Requests;

/// <summary>Accepts questions, tracks them, and hands back what happened.</summary>
public interface IAskFront
{
    /// <summary>
    /// Accept a question. The ask is stored before this returns, so an accepted ask
    /// survives the process dying on the next line. A repeat of the same caller's
    /// client request id returns the existing ask and starts no new work.
    /// </summary>
    AskAdmission Submit(AskSubmission submission);

    /// <summary>One of this caller's asks, or null when it is not theirs or does not exist.</summary>
    AskRecord? Status(string callerKey, string askId);

    /// <summary>Withdraw one of this caller's asks. True when it was open and is now cancelled.</summary>
    bool Cancel(string callerKey, string askId);

    /// <summary>This caller's recent asks, newest first.</summary>
    IReadOnlyList<AskRecord> List(string callerKey, int limit, bool openOnly = false);

    /// <summary>How many asks are waiting ahead of this one (0 = next), or -1 when it is not waiting.</summary>
    int PositionOf(string askId);

    /// <summary>
    /// Wait until the ask is final or <paramref name="budget"/> runs out, and return it
    /// as it stands. Never throws for a timeout - the record says it is still open.
    /// </summary>
    Task<AskRecord?> WaitAsync(string callerKey, string askId, TimeSpan budget, CancellationToken ct);

    /// <summary>
    /// Wait until the ask changes past <paramref name="afterSeq"/>, is final, or the
    /// budget runs out. For a client that polls: it gets an answer as soon as there is
    /// news, or the unchanged record when there is none.
    /// </summary>
    Task<AskRecord?> WaitForChangeAsync(string callerKey, string askId, long afterSeq, TimeSpan budget, CancellationToken ct);

    /// <summary>Raised after every change to any ask. Handlers must not throw or block.</summary>
    event EventHandler<AskRecord>? Changed;
}

/// <summary>
/// An answer that does not need the model: a greeting, a question about the assistant
/// itself, the date from the device clock. Tried in order before the brain; the first
/// non-null answer wins.
/// </summary>
/// <remarks>
/// Run when the ask's turn comes, not at submission, so a shortcut with a side effect
/// (clearing dead weight) runs once, from the queue, like everything else.
/// </remarks>
public interface IAskShortcut
{
    /// <summary>A short name for logs.</summary>
    string Name { get; }

    /// <summary>The answer, or null to let the next shortcut (and finally the model) answer.</summary>
    ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct);
}
