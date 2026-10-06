// StreamingToolProgress.cs
//
// (3.3.0) Long-running tools push progress updates (% complete +
// status text) while they run, so the AI can keep the caller informed.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One progress update from a streaming tool.</summary>
/// <param name="CallId">The tool-call id this update belongs to.</param>
/// <param name="PercentComplete">0..100 progress fraction.</param>
/// <param name="StatusText">Optional status to speak to the caller.</param>
/// <param name="EmittedAt">Server time the update was created.</param>
public sealed record ToolProgressUpdate(string CallId, float PercentComplete, string? StatusText, DateTimeOffset EmittedAt);

/// <summary>(3.3.0) Streaming tool handler — accepts a progress sink it can push updates into.</summary>
public delegate ValueTask<string> StreamingToolHandler(
    string                       argumentsJson,
    IToolProgressSink            progressSink,
    CancellationToken            ct);

/// <summary>(3.3.0) The sink a tool pushes progress updates into.</summary>
public interface IToolProgressSink
{
    /// <summary>Emit one update. Implementations decide whether to forward to the caller.</summary>
    ValueTask EmitAsync(ToolProgressUpdate update, CancellationToken ct = default);
}

/// <summary>
/// (3.3.0) Default sink that throttles updates (≥<paramref name="MinIntervalMs"/> apart)
/// and speaks each via TTS to the active call session.
/// </summary>
public sealed class SpokenToolProgressSink : IToolProgressSink
{
    private readonly ICallSession _session;
    private readonly BriefingSynthesiser _tts;
    private readonly TimeSpan _minInterval;
    private readonly object _gate = new();
    private DateTimeOffset _lastSpoken;
    private readonly Func<DateTimeOffset> _clock;

    public SpokenToolProgressSink(
        ICallSession           session,
        BriefingSynthesiser    tts,
        TimeSpan?              minInterval = null,
        Func<DateTimeOffset>?  clock       = null)
    {
        _session     = session ?? throw new ArgumentNullException(nameof(session));
        _tts         = tts     ?? throw new ArgumentNullException(nameof(tts));
        _minInterval = minInterval ?? TimeSpan.FromSeconds(2);
        _clock       = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask EmitAsync(ToolProgressUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(update.StatusText)) return;

        var now = _clock();
        bool shouldSpeak;
        lock (_gate)
        {
            shouldSpeak = (now - _lastSpoken) >= _minInterval;
            if (shouldSpeak) _lastSpoken = now;
        }
        if (!shouldSpeak) return;

        var audio = await _tts(update.StatusText, ct).ConfigureAwait(false);
        if (!audio.IsEmpty)
        {
            await _session.SendAudioAsync(
                new AudioFrame(audio, CallMediaFormat.Pcm24000, TimeSpan.Zero), ct)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>(3.3.0) Sink that records updates for observability without speaking them.</summary>
public sealed class RecordingToolProgressSink : IToolProgressSink
{
    private readonly object _gate = new();
    private readonly System.Collections.Generic.List<ToolProgressUpdate> _updates = new();

    public System.Collections.Generic.IReadOnlyList<ToolProgressUpdate> Updates
    {
        get { lock (_gate) return _updates.ToArray(); }
    }

    public ValueTask EmitAsync(ToolProgressUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate) _updates.Add(update);
        return ValueTask.CompletedTask;
    }
}

/// <summary>(3.3.0) Run a streaming tool handler against a progress sink.</summary>
public static class StreamingToolRunner
{
    public static async ValueTask<ToolResult> RunAsync(
        ToolInvocation        invocation,
        StreamingToolHandler  handler,
        IToolProgressSink     sink,
        CancellationToken     ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(sink);

        try
        {
            var resultJson = await handler(invocation.ArgumentsJson, sink, ct).ConfigureAwait(false);
            return new ToolResult(invocation.CallId, true, resultJson ?? "{}");
        }
        catch (Exception ex)
        {
            return new ToolResult(invocation.CallId, false, "{}", ex.Message);
        }
    }
}
