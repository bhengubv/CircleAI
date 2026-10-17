// ServiceLimits.cs
//
// EVERYTHING THE SERVICE BOUNDS, AS DATA, IN ONE RECORD.
//
// The queue used to be the binder thread pool: unbounded in practice, invisible, and
// shared with the cheap verbs, so a burst of questions could stop an app from even
// reading the setup screen. Generation had no deadline. A question had no length
// limit. Each of those is a number now, chosen per device from what was measured on
// it, and every one is reported to clients through the Status verb's settings.

using System;
using CircleAI.Core;

namespace CircleAI.Assistant.Requests;

/// <summary>The bounds the request coordinator enforces.</summary>
/// <param name="QueueCapacity">Asks that may be waiting at once, all lanes together.</param>
/// <param name="AssistantReserved">Of those, places only the Assistant lane may take - a full queue never stops somebody who spoke.</param>
/// <param name="PerCallerQueued">Open asks one third-party app may have.</param>
/// <param name="PerFirstPartyQueued">Open asks one of our own apps may have.</param>
/// <param name="InteractiveQueueDeadline">How long an Assistant or Foreground ask may wait for its turn.</param>
/// <param name="BackgroundQueueDeadline">How long a Background ask may wait for its turn.</param>
/// <param name="AssistantStaleAfter">A spoken question nobody heard answered within this is not worth speaking.</param>
/// <param name="LoadWaitBudget">How long one ask waits for the model to come up after its turn came.</param>
/// <param name="GenerationDeadline">The longest one answer may take to write.</param>
/// <param name="MaxTokensSpoken">The longest answer to a spoken question.</param>
/// <param name="MaxTokensTyped">The longest answer to a typed question.</param>
/// <param name="MaxMessageChars">The longest question accepted.</param>
/// <param name="HistoryChars">How much of a conversation's history goes into a follow-up.</param>
/// <param name="MaxAttempts">How many times an ask is started before a crash loop fails it.</param>
/// <param name="Retention">How long a finished ask is kept.</param>
/// <param name="TextWriteInterval">How often a running answer's text is written through to the store.</param>
public sealed record ServiceLimits(
    int QueueCapacity,
    int AssistantReserved,
    int PerCallerQueued,
    int PerFirstPartyQueued,
    TimeSpan InteractiveQueueDeadline,
    TimeSpan BackgroundQueueDeadline,
    TimeSpan AssistantStaleAfter,
    TimeSpan LoadWaitBudget,
    TimeSpan GenerationDeadline,
    int MaxTokensSpoken,
    int MaxTokensTyped,
    int MaxMessageChars,
    int HistoryChars,
    int MaxAttempts,
    TimeSpan Retention,
    TimeSpan TextWriteInterval)
{
    /// <summary>The defaults, sized for a 4 GB phone running a sub-billion model.</summary>
    /// <remarks>
    /// <para>ONE ANSWER AT A TIME, because there is one resident generator and its native
    /// handle is not re-entrant - so a queue of eight is already minutes of work.</para>
    /// <para>160 TOKENS SPOKEN, 512 TYPED. At ~113 ms a token on a P30, 512 tokens is
    /// almost a minute of speech nobody asked for; a spoken answer is a sentence or two.</para>
    /// <para>THE LOAD WAIT COVERS A COLD START: 13-23 s to load on the P30, plus up to
    /// 30 s waiting for the catalogue on the very first start after an install.</para>
    /// <para>SEVEN DAYS, the retention announced for this service: long enough for an app
    /// to come back for an answer, short enough that the store is not a diary.</para>
    /// </remarks>
    public static ServiceLimits Default { get; } = new(
        QueueCapacity:            8,
        AssistantReserved:        2,
        PerCallerQueued:          2,
        PerFirstPartyQueued:      4,
        InteractiveQueueDeadline: TimeSpan.FromMinutes(3),
        BackgroundQueueDeadline:  TimeSpan.FromMinutes(15),
        AssistantStaleAfter:      TimeSpan.FromMinutes(2),
        LoadWaitBudget:           TimeSpan.FromSeconds(90),
        GenerationDeadline:       TimeSpan.FromMinutes(2),
        MaxTokensSpoken:          160,
        MaxTokensTyped:           512,
        MaxMessageChars:          8000,
        HistoryChars:             1600,
        MaxAttempts:              3,
        Retention:                TimeSpan.FromDays(7),
        TextWriteInterval:        TimeSpan.FromMilliseconds(600));

    /// <summary>The limits for this device.</summary>
    /// <remarks>
    /// A phone with less memory gets a shorter history (every character of it is
    /// prefill, at ~29 ms a token on the slowest phone this targets); a desktop gets a
    /// longer queue and history. Everything else is the same everywhere.
    /// </remarks>
    public static ServiceLimits For(DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var total = probe.RamTotalBytes / (1024.0 * 1024 * 1024);
        if (total <= 0) return Default;
        if (total <= 4.5) return Default with { HistoryChars = 1200 };
        if (total <= 8.5) return Default with { HistoryChars = 2400, QueueCapacity = 12 };
        return Default with { HistoryChars = 6000, QueueCapacity = 24, PerCallerQueued = 4, PerFirstPartyQueued = 8 };
    }

    /// <summary>The longest answer for an ask.</summary>
    public int MaxTokensFor(bool spoken) => spoken ? MaxTokensSpoken : MaxTokensTyped;

    /// <summary>How long an ask in this lane may wait for its turn.</summary>
    public TimeSpan QueueDeadlineFor(AskLane lane) =>
        lane == AskLane.Background ? BackgroundQueueDeadline : InteractiveQueueDeadline;
}
