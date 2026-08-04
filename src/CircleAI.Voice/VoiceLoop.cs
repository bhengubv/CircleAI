#nullable enable

// VoiceLoop.cs
//
// The full hands-free conversation, assembled:
//
//   wake word -> VAD -> ASR -> BRAIN -> TTS -> audio out -> back to listening
//
// VoicePipeline already composed the EARS (wake -> VAD -> ASR) and raised a
// Transcribed event. Nothing ever joined that to a brain or a mouth, so the
// hands-free loop did not exist end to end anywhere in the codebase — each half
// worked in isolation and no code closed the circle.
//
// The brain is a DELEGATE, not IAIService: CircleAI.Voice must not depend on
// CircleAI.Hosting (Hosting depends on the speech contracts, not the reverse).
// The host supplies `text -> reply`, which is trivially IAIService.ChatAsync.

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Voice;

/// <summary>Audio sink — plays synthesised PCM. Hosts back this with a platform player.</summary>
public interface IAudioPlayer : IAsyncDisposable
{
    /// <summary>Plays one PCM buffer to completion.</summary>
    Task PlayAsync(ReadOnlyMemory<byte> pcm, int sampleRate, int channels, int bitsPerSample, CancellationToken ct = default);
}

/// <summary>Discards audio. Lets the loop run headless (tests, servers) without a speaker.</summary>
public sealed class NullAudioPlayer : IAudioPlayer
{
    public Task PlayAsync(ReadOnlyMemory<byte> pcm, int sampleRate, int channels, int bitsPerSample, CancellationToken ct = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>One completed hands-free exchange.</summary>
public sealed class VoiceExchangeEventArgs : EventArgs
{
    public required string Heard { get; init; }
    public required string Replied { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Closes the loop: listens via <see cref="VoicePipeline"/>, sends the
/// transcription to a brain delegate, and speaks the reply through
/// <see cref="ITtsEngine"/>.
/// </summary>
public sealed class VoiceLoop : IAsyncDisposable
{
    private readonly VoicePipeline _ears;
    private readonly Func<string, CancellationToken, Task<string>> _brain;
    private readonly ITtsEngine _mouth;
    private readonly IAudioPlayer _speaker;

    private CancellationTokenSource? _cts;
    private Task? _run;
    private Channel<TranscriptionResult>? _turns;
    private bool _disposed;

    /// <summary>Cancels the reply currently being spoken. Null when nothing is.</summary>
    private CancellationTokenSource? _speaking;

    /// <summary>Let someone cut the assistant off mid-answer by saying the wake word.</summary>
    /// <remarks>
    /// THE DIFFERENCE BETWEEN A CONVERSATION AND A BROADCAST. Without this, asking
    /// for something and getting a long answer means waiting the answer out — you
    /// cannot correct it, redirect it, or shut it up. Everyone who has shouted at a
    /// speaker that would not stop knows the feeling, and it is the moment a person
    /// decides the thing is not listening to them.
    /// <para>
    /// It needs two things that only just became true: the wake detector has to
    /// stay armed while the speaker is playing, and the microphone has to not hear
    /// the reply as a new wake. The second is what AcousticEchoCanceler is for, and
    /// it is now attached on the Android capture path. Without echo cancellation
    /// leave this OFF, or the assistant will interrupt itself the first time its
    /// own voice says something the spotter likes.
    /// </para>
    /// </remarks>
    public bool AllowBargeIn { get; init; } = true;

    /// <summary>Raised when a reply was cut short because someone spoke over it.</summary>
    public event EventHandler? BargedIn;

    /// <summary>Raised after each exchange — for transcript UI and logging.</summary>
    public event EventHandler<VoiceExchangeEventArgs>? Exchanged;

    /// <summary>Raised when a turn fails. The loop KEEPS LISTENING; one bad turn must not deafen the assistant.</summary>
    public event EventHandler<Exception>? Faulted;

    /// <param name="brain">
    /// <c>text -> reply</c>. Typically <c>(t, ct) =&gt; aiService.ChatAsync(t, ct)</c>.
    /// </param>
    public VoiceLoop(
        VoicePipeline ears,
        Func<string, CancellationToken, Task<string>> brain,
        ITtsEngine mouth,
        IAudioPlayer? speaker = null)
    {
        _ears = ears ?? throw new ArgumentNullException(nameof(ears));
        _brain = brain ?? throw new ArgumentNullException(nameof(brain));
        _mouth = mouth ?? throw new ArgumentNullException(nameof(mouth));
        _speaker = speaker ?? new NullAudioPlayer();
    }

    /// <summary>Starts listening. Returns once the wake detector is armed; turns run in the background.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_run is not null) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // VoicePipeline is EVENT-based (Transcribed), not an async stream. Hand
        // each activation to a channel so turns are processed one at a time by a
        // single consumer: a synchronous event handler cannot await the brain,
        // and letting turns overlap would interleave two replies through one
        // speaker.
        _turns = Channel.CreateUnbounded<TranscriptionResult>(
            new UnboundedChannelOptions { SingleReader = true });

        _ears.Transcribed += OnTranscribed;
        if (AllowBargeIn) _ears.WakeDetector.WakeWordDetected += OnWakeWhileSpeaking;
        _run = ConsumeAsync(_cts.Token);

        await _ears.StartAsync(_cts.Token).ConfigureAwait(false);
    }

    private void OnTranscribed(object? sender, TranscribedEventArgs e)
        => _turns?.Writer.TryWrite(e.Result);

    /// <summary>A wake during playback means "stop talking and listen to me".</summary>
    /// <remarks>
    /// Deliberately does NOT drop the turn that is already queued behind it: the
    /// wake will be followed by whatever the person actually wants, and the
    /// pipeline captures it as normal. All this does is stop the speaker.
    /// </remarks>
    private void OnWakeWhileSpeaking(object? sender, WakeWordDetectedEventArgs e)
    {
        var speaking = _speaking;
        if (speaking is null || speaking.IsCancellationRequested) return;

        try { speaking.Cancel(); } catch (ObjectDisposedException) { return; }
        BargedIn?.Invoke(this, EventArgs.Empty);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var reader = _turns!.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        while (reader.TryRead(out var heard))
        {
            if (ct.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(heard.Text)) continue;

            try
            {
                var reply = await _brain(heard.Text, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    var audio = await _mouth.SynthesiseAsync(reply, ct).ConfigureAwait(false);
                    if (audio.AudioData.Length > 0)
                    {
                        // Playback gets its own token so a barge-in cancels ONLY the
                        // speaking, not the loop. Cancelling the loop's token here
                        // would make interrupting the assistant also switch it off,
                        // which is the opposite of what the person wanted.
                        using var speech = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _speaking = speech;
                        try
                        {
                            await _speaker.PlayAsync(audio.AudioData, audio.SampleRate,
                                audio.Channels, audio.BitsPerSample, speech.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Barged in. Not an error, and not a reason to stop.
                        }
                        finally { _speaking = null; }
                    }
                }

                Exchanged?.Invoke(this, new VoiceExchangeEventArgs
                {
                    Heard = heard.Text,
                    Replied = reply ?? string.Empty,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed turn (model hiccup, TTS fault) must not kill the loop —
                // going permanently deaf is far worse than dropping one reply.
                Faulted?.Invoke(this, ex);
            }
        }
    }

    /// <summary>Stops listening.</summary>
    public async Task StopAsync()
    {
        _ears.Transcribed -= OnTranscribed;
        if (AllowBargeIn) _ears.WakeDetector.WakeWordDetected -= OnWakeWhileSpeaking;
        _turns?.Writer.TryComplete();

        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);

        try { await _ears.StopAsync().ConfigureAwait(false); } catch { /* already stopping */ }

        if (_run is not null)
        {
            try { await _run.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _run = null;
        _cts.Dispose();
        _cts = null;
        _turns = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }
}
