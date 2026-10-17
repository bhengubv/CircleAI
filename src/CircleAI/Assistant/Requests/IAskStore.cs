// IAskStore.cs
//
// WHERE ACCEPTED QUESTIONS LIVE. One store per service, in the service's private
// storage; SqliteAskStore is the implementation every head uses. It holds people's
// questions and answers, so it keeps finished ones only for the retention window and
// forgets on request.

using System;
using System.Collections.Generic;

namespace CircleAI.Assistant.Requests;

/// <summary>Durable storage for <see cref="AskRecord"/>s.</summary>
/// <remarks>
/// Every write is durable before it returns - an ask the store has accepted survives
/// the process dying on the next line. Every write assigns the record a new
/// <see cref="AskRecord.Seq"/>, strictly greater than any before it, so a caller can
/// ask for "anything newer than N" and miss nothing.
/// </remarks>
public interface IAskStore : IDisposable
{
    /// <summary>The highest sequence number assigned so far.</summary>
    long LastSeq { get; }

    /// <summary>One ask by id, or null.</summary>
    AskRecord? Get(string id);

    /// <summary>The ask a caller submitted under this request id, or null.</summary>
    AskRecord? FindByClientRequest(string callerKey, string clientRequestId);

    /// <summary>
    /// Store a new ask. Returns it with its sequence number. When the caller already
    /// has an ask under the same client request id, returns THAT ask unchanged.
    /// </summary>
    AskRecord Insert(AskRecord record);

    /// <summary>
    /// Change one ask atomically: <paramref name="change"/> sees the stored record and
    /// returns the new one (or the same instance for no change). Returns the stored
    /// result, or null when there is no such ask. A final ask is never changed back.
    /// </summary>
    AskRecord? Update(string id, Func<AskRecord, AskRecord> change);

    /// <summary>A caller's asks, newest first. A null caller lists everyone's (service use only).</summary>
    IReadOnlyList<AskRecord> List(string? callerKey, int limit, bool openOnly = false);

    /// <summary>Every ask that is not final, oldest first - what a restarted service must pick up.</summary>
    IReadOnlyList<AskRecord> Open();

    /// <summary>Asks changed after <paramref name="seq"/>, oldest change first.</summary>
    IReadOnlyList<AskRecord> ChangedSince(long seq, int limit);

    /// <summary>Forget final asks that finished before <paramref name="finishedBefore"/>. Returns how many.</summary>
    int Purge(DateTimeOffset finishedBefore);

    /// <summary>Forget everything one caller asked (final or not). Returns how many.</summary>
    int Forget(string callerKey);
}
