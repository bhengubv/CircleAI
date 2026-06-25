// FirstMessagePreamble.cs
//
// (3.3.0) Speak a greeting the moment a call connects, before the LLM
// has a chance to "warm up" — eliminates the awkward 1-2 second
// silence callers hate. Supports variable substitution (time of day,
// business name, agent identity) and per-call overrides.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Configuration for the first-message preamble.</summary>
/// <param name="Template">Template with <c>{{var}}</c> placeholders.</param>
/// <param name="MaxLatency">If the LLM responds before this elapses, skip the preamble. Default 250 ms.</param>
public sealed record FirstMessagePreambleOptions(
    string    Template,
    TimeSpan? MaxLatency = null)
{
    public TimeSpan MaxLatencyOrDefault => MaxLatency ?? TimeSpan.FromMilliseconds(250);
}

/// <summary>(3.3.0) Speaks a greeting at call-start.</summary>
public interface IFirstMessagePreamble
{
    /// <summary>
    /// (3.3.0) Speak the preamble. <paramref name="modelReady"/> is
    /// awaited concurrently — if it completes before <see cref="FirstMessagePreambleOptions.MaxLatency"/>
    /// the preamble is skipped (the model has its own greeting).
    /// </summary>
    Task SpeakAsync(
        ICallSession        session,
        BriefingSynthesiser tts,
        Task                modelReady,
        CancellationToken   ct = default);
}

/// <summary>(3.3.0) Default driver that resolves <see cref="FirstMessagePreambleOptions.Template"/> via a <see cref="PromptVariableResolver"/>.</summary>
public sealed class DefaultFirstMessagePreamble : IFirstMessagePreamble
{
    private readonly FirstMessagePreambleOptions _options;
    private readonly PromptVariableResolver _resolver;

    public DefaultFirstMessagePreamble(
        FirstMessagePreambleOptions options,
        PromptVariableResolver?     resolver = null)
    {
        _options  = options  ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? new PromptVariableResolver();
    }

    public async Task SpeakAsync(
        ICallSession        session,
        BriefingSynthesiser tts,
        Task                modelReady,
        CancellationToken   ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(modelReady);

        // Race the model. If it wins within the latency window, skip the preamble.
        var raceWindow = Task.Delay(_options.MaxLatencyOrDefault, ct);
        var winner     = await Task.WhenAny(modelReady, raceWindow).ConfigureAwait(false);
        if (winner == modelReady && modelReady.IsCompletedSuccessfully)
        {
            return;
        }

        var rendered = await _resolver.RenderAsync(_options.Template, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rendered)) return;

        var audio = await tts(rendered, ct).ConfigureAwait(false);
        if (audio.IsEmpty) return;

        await session.SendAudioAsync(
            new AudioFrame(audio, CallMediaFormat.Pcm24000, TimeSpan.Zero), ct)
            .ConfigureAwait(false);
    }
}
