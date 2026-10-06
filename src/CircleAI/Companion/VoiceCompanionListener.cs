// VoiceCompanionListener.cs
//
// Concrete IVoiceListener that wires a VoicePipeline to an ICompanionSession.
// When the wake word fires → the user speaks → VoicePipeline.Transcribed fires
// → we forward the text to the session → we raise ResponseReady with the reply.
//
// The Companion call is dispatched on the thread-pool (fire-and-forget) so we
// never block the wake-word detection thread. Activation failures are traced but
// do not crash the host — consistent with VoicePipeline.ActivationFailed semantics.

using CircleAI.Voice;

namespace CircleAI.Companion;

/// <summary>
/// Wires a <see cref="VoicePipeline"/> to an <see cref="ICompanionSession"/>:
/// transcriptions are forwarded to the session and the Companion's reply is
/// surfaced via <see cref="IVoiceListener.ResponseReady"/>.
/// </summary>
public sealed class VoiceCompanionListener : IVoiceListener
{
    private readonly VoicePipeline _pipeline;
    private readonly ICompanionSession _session;
    private bool _disposed;

    /// <param name="pipeline">
    /// The voice pipeline that produces <see cref="TranscribedEventArgs"/>.
    /// <see cref="VoiceCompanionListener"/> subscribes to
    /// <see cref="VoicePipeline.Transcribed"/> and owns the pipeline's lifetime
    /// — <see cref="DisposeAsync"/> disposes it.
    /// </param>
    /// <param name="session">
    /// The Companion session that receives transcribed user utterances.
    /// <see cref="VoiceCompanionListener"/> owns the session's lifetime —
    /// <see cref="DisposeAsync"/> disposes it.
    /// </param>
    public VoiceCompanionListener(VoicePipeline pipeline, ICompanionSession session)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(session);

        _pipeline = pipeline;
        _session  = session;

        _pipeline.Transcribed += OnTranscribed;
    }

    /// <inheritdoc />
    public event EventHandler<UtteranceDetectedEventArgs>? UtteranceDetected;

    /// <inheritdoc />
    public event EventHandler<ResponseReadyEventArgs>? ResponseReady;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pipeline.StartAsync(ct);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pipeline.StopAsync(ct);
    }

    private void OnTranscribed(object? sender, TranscribedEventArgs e)
    {
        if (_disposed) return;

        var text        = e.Result.Text;
        var confidence  = e.Result.Confidence;
        var detectedAt  = e.CompletedAt;

        // Notify subscribers that we received an utterance.
        UtteranceDetected?.Invoke(this, new UtteranceDetectedEventArgs
        {
            Text        = text,
            Confidence  = confidence,
            DetectedAt  = detectedAt,
        });

        // Forward to the Companion asynchronously — never block the pipeline thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await _session.SendAsync(text).ConfigureAwait(false);

                if (!_disposed)
                {
                    ResponseReady?.Invoke(this, new ResponseReadyEventArgs
                    {
                        Text              = reply,
                        OriginalUtterance = text,
                        CompletedAt       = DateTimeOffset.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    $"VoiceCompanionListener: session failed for utterance '{text}': {ex.Message}");
            }
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _pipeline.Transcribed -= OnTranscribed;

        await _pipeline.DisposeAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
