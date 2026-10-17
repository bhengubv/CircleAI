// Asks.cs
//
// A QUESTION THE SERVICE HAS ACCEPTED IS A RECORD, NOT A BINDER THREAD.
//
// An ask used to exist only as a blocked binder transaction: OnTransact ran the whole
// thing - the cold-start wait, the prefill, the decode - with GetAwaiter().GetResult()
// and handed the answer back in the reply parcel. If the app went away the answer was
// generated and thrown away; if the service died the question was gone; and the
// client's retry after a DeadObjectException re-sent the same question with nothing to
// say it was the same one, so it was asked twice.
//
// So an ask is saved, with an identity, BEFORE anybody is told it was accepted. The
// caller can disconnect, come back, and read where it got to: waiting for the model,
// running, answered, failed and why, or paused because Android stopped the work. A
// retry carrying the same client request id finds the same record and does no work.

using System;
using CircleAI.Assistant.Lifecycle;

namespace CircleAI.Assistant.Requests;

/// <summary>Where an accepted question has got to. Appended, never renumbered: it crosses the link by name.</summary>
public enum AskState
{
    /// <summary>Accepted and waiting its turn.</summary>
    Queued = 0,

    /// <summary>Its turn has come and the model is loading.</summary>
    WaitingForModel = 1,

    /// <summary>There is no usable model yet; it waits for setup to finish.</summary>
    WaitingForSetup = 2,

    /// <summary>It needs a permission or grant first.</summary>
    WaitingForPermission = 3,

    /// <summary>Being answered; the text so far is on the record.</summary>
    Running = 4,

    /// <summary>Android stopped the work; it is kept and resumes by itself.</summary>
    Paused = 5,

    /// <summary>Answered. Final.</summary>
    Succeeded = 6,

    /// <summary>Could not be answered; the record says why. Final.</summary>
    Failed = 7,

    /// <summary>The caller withdrew it. Final.</summary>
    Cancelled = 8,

    /// <summary>It waited too long to still be wanted. Final.</summary>
    Expired = 9,
}

/// <summary>Which queue a question waits in. Higher lanes are served first.</summary>
public enum AskLane
{
    /// <summary>Somebody spoke to the phone and is waiting to hear the answer.</summary>
    Assistant = 0,

    /// <summary>An app on screen asked and is waiting to show the answer.</summary>
    Foreground = 1,

    /// <summary>Work nobody is watching.</summary>
    Background = 2,
}

/// <summary>Helpers over <see cref="AskState"/>.</summary>
public static class AskStates
{
    /// <summary>Whether nothing more will happen to an ask in this state.</summary>
    public static bool IsFinal(AskState state) => state is AskState.Succeeded or AskState.Failed
        or AskState.Cancelled or AskState.Expired;

    /// <summary>Whether an ask in this state is waiting rather than being worked on.</summary>
    public static bool IsWaiting(AskState state) => state is AskState.Queued or AskState.WaitingForModel
        or AskState.WaitingForSetup or AskState.WaitingForPermission or AskState.Paused;
}

/// <summary>What a caller asks for when it submits a question.</summary>
/// <param name="CallerKey">Who is asking, as the SERVICE identified them - never what the client claims.</param>
/// <param name="ClientRequestId">The caller's idempotency key. The same key from the same caller is the same ask.</param>
/// <param name="SessionId">The caller's conversation id; empty means a stateless question.</param>
/// <param name="Lane">Which queue.</param>
/// <param name="Message">What to put to the model (it may carry the caller's own context).</param>
/// <param name="Question">The question alone, as the person asked it, when the caller composed <paramref name="Message"/>.</param>
/// <param name="Spoken">The answer will be read aloud, so it is kept short.</param>
/// <param name="Agentic">The caller allows tools for this turn.</param>
/// <param name="SideEffectFree">Safe to run again after a crash. Anything that changes the world is false.</param>
public sealed record AskSubmission(
    string CallerKey,
    string? ClientRequestId,
    string SessionId,
    AskLane Lane,
    string Message,
    string? Question = null,
    bool Spoken = false,
    bool Agentic = false,
    bool SideEffectFree = true)
{
    /// <summary>The question to search and classify on: the raw question when given, else the message.</summary>
    public string Asked => string.IsNullOrWhiteSpace(Question) ? Message : Question!;
}

/// <summary>Where the time for one answer went. Milliseconds; -1 means not measured.</summary>
/// <param name="QueueMs">From acceptance until its turn came.</param>
/// <param name="LoadWaitMs">Waiting for the model to load, after its turn came.</param>
/// <param name="EnrichMs">Composing the prompt (skills, memory, history).</param>
/// <param name="PrefillMs">Reading the prompt, up to the first piece of answer.</param>
/// <param name="PromptTokens">How long the prompt was, in tokens.</param>
/// <param name="DecodeMs">Writing the answer, after the first piece.</param>
/// <param name="OutputTokens">How long the answer was, in tokens.</param>
/// <param name="TtftMs">From its turn coming to the first piece of answer.</param>
/// <param name="TotalMs">From acceptance to the answer being complete.</param>
/// <param name="ModelId">The model that answered.</param>
/// <param name="KvPath">How the prompt cache was used: reset, prefix-hit, continued, or none.</param>
/// <param name="Finish">Why generation stopped: eos, length, cancel, error, shortcut.</param>
public sealed record TurnTiming(
    long QueueMs = -1,
    long LoadWaitMs = -1,
    long EnrichMs = -1,
    long PrefillMs = -1,
    int PromptTokens = -1,
    long DecodeMs = -1,
    int OutputTokens = -1,
    long TtftMs = -1,
    long TotalMs = -1,
    string? ModelId = null,
    string? KvPath = null,
    string? Finish = null)
{
    /// <summary>Nothing measured.</summary>
    public static TurnTiming None { get; } = new();

    /// <summary>Prompt processing speed, tokens per second, when both parts were measured.</summary>
    public double? PrefillTokensPerSecond => PrefillMs > 0 && PromptTokens > 0 ? PromptTokens * 1000.0 / PrefillMs : null;

    /// <summary>Generation speed, tokens per second, when both parts were measured.</summary>
    public double? DecodeTokensPerSecond => DecodeMs > 0 && OutputTokens > 1 ? (OutputTokens - 1) * 1000.0 / DecodeMs : null;

    /// <summary>One text-free log line: never the question, never the answer.</summary>
    public string ToLogLine(string askId) =>
        $"CIRCLEAI-TURN id={askId} model={ModelId ?? "-"} queue={QueueMs} wait={LoadWaitMs} enrich={EnrichMs} "
        + $"prefill={PrefillMs}ms/{PromptTokens}tok({Rate(PrefillTokensPerSecond)}) "
        + $"decode={DecodeMs}ms/{OutputTokens}tok({Rate(DecodeTokensPerSecond)}) "
        + $"ttft={TtftMs} total={TotalMs} kv={KvPath ?? "-"} finish={Finish ?? "-"}";

    private static string Rate(double? perSecond) =>
        perSecond is { } r ? r.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " tok/s" : "-";
}

/// <summary>One accepted question, as stored.</summary>
/// <param name="Id">The service's identity for it.</param>
/// <param name="CallerKey">Who asked.</param>
/// <param name="ClientRequestId">The caller's idempotency key, if any.</param>
/// <param name="SessionId">The caller's conversation id.</param>
/// <param name="Lane">Which queue.</param>
/// <param name="Message">What is put to the model.</param>
/// <param name="Question">The question alone, when the caller composed the message.</param>
/// <param name="Spoken">Read aloud: kept short.</param>
/// <param name="Agentic">Tools allowed.</param>
/// <param name="SideEffectFree">Safe to run again after a crash.</param>
/// <param name="State">Where it has got to.</param>
/// <param name="Seq">The store's change sequence at its last change; a client polls for anything newer.</param>
/// <param name="Text">The answer so far (partial while Running, whole once Succeeded).</param>
/// <param name="Kind">Why it failed, paused or waits, when it did.</param>
/// <param name="Sentence">What to tell the person about that, in plain words.</param>
/// <param name="NeedsPermission">Which permission or scope it waits for, when it waits for one.</param>
/// <param name="ModelId">The model working on it, once one is.</param>
/// <param name="Attempts">How many times it has been started (more than one after a crash).</param>
/// <param name="CreatedAt">When it was accepted.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="StartedAt">When its turn first came.</param>
/// <param name="FinishedAt">When it became final.</param>
/// <param name="Timing">Where the time went, once known.</param>
public sealed record AskRecord(
    string Id,
    string CallerKey,
    string? ClientRequestId,
    string SessionId,
    AskLane Lane,
    string Message,
    string? Question,
    bool Spoken,
    bool Agentic,
    bool SideEffectFree,
    AskState State,
    long Seq,
    string Text,
    FailureKind? Kind,
    string? Sentence,
    string? NeedsPermission,
    string? ModelId,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    TurnTiming? Timing)
{
    /// <summary>Whether nothing more will happen to it.</summary>
    public bool IsFinal => AskStates.IsFinal(State);

    /// <summary>The question to search and classify on.</summary>
    public string Asked => string.IsNullOrWhiteSpace(Question) ? Message : Question!;

    /// <summary>A new record for an accepted submission. The store assigns <see cref="Seq"/>.</summary>
    public static AskRecord Accept(string id, AskSubmission s, DateTimeOffset now) => new(
        Id: id,
        CallerKey: s.CallerKey,
        ClientRequestId: string.IsNullOrWhiteSpace(s.ClientRequestId) ? null : s.ClientRequestId,
        SessionId: s.SessionId ?? string.Empty,
        Lane: s.Lane,
        Message: s.Message,
        Question: string.IsNullOrWhiteSpace(s.Question) ? null : s.Question,
        Spoken: s.Spoken,
        Agentic: s.Agentic,
        SideEffectFree: s.SideEffectFree,
        State: AskState.Queued,
        Seq: 0,
        Text: string.Empty,
        Kind: null,
        Sentence: null,
        NeedsPermission: null,
        ModelId: null,
        Attempts: 0,
        CreatedAt: now,
        UpdatedAt: now,
        StartedAt: null,
        FinishedAt: null,
        Timing: null);
}

/// <summary>The answer to a submission: accepted (new or the same one again), or refused at the door.</summary>
/// <param name="Accepted">Whether there is an ask to follow.</param>
/// <param name="Record">The ask, when accepted.</param>
/// <param name="Duplicate">The caller had already submitted this request id; this is that ask.</param>
/// <param name="Position">How many asks are ahead of it (0 = next), when accepted and waiting.</param>
/// <param name="Kind">Why it was refused.</param>
/// <param name="Refusal">What to tell the person, when refused.</param>
public sealed record AskAdmission(
    bool Accepted,
    AskRecord? Record,
    bool Duplicate,
    int Position,
    FailureKind? Kind,
    string? Refusal)
{
    /// <summary>Accepted.</summary>
    public static AskAdmission Of(AskRecord record, bool duplicate, int position) =>
        new(true, record, duplicate, position, null, null);

    /// <summary>Refused at the door.</summary>
    public static AskAdmission Refused(FailureKind kind, string refusal) =>
        new(false, null, false, -1, kind, refusal);
}
