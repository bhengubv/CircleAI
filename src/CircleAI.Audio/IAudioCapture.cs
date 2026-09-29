// IAudioCapture.cs
//
// The microphone, as a contract.
//
// EXTRACTED FROM VoicePipeline.cs, WHICH IS WHY THIS FILE IS SO SMALL. The contract sat
// inside the file that composes the whole pipeline, so a head that only wanted to
// open the microphone - or only to play PCM somebody else synthesised - had to
// reference the assembly holding Whisper and the ONNX voices to see it. That cost
// a thin client 18 MB of ONNX Runtime it could not call.
//
// The namespace has not changed, so no call site has. See CircleAI.Audio.csproj.

using System.Runtime.CompilerServices;

namespace CircleAI.Voice;

/// <summary>
/// Captures raw audio from a platform input (microphone) and exposes it as
/// an asynchronous stream of PCM byte chunks. Implementations are expected
/// to produce data in the format reported by <see cref="Format"/>.
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    /// <summary>The PCM format produced by <see cref="CaptureAsync"/>.</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Begin capturing audio. The returned sequence yields PCM chunks until
    /// the cancellation token is signalled or the underlying capture stops.
    /// </summary>
    /// <param name="ct">Cancellation token used to stop capture.</param>
    IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(CancellationToken ct);
}

/// <summary>
/// No-op <see cref="IAudioCapture"/> that yields no audio. Used as a safe
/// default when no platform microphone backend is available.
/// </summary>
public sealed class NullAudioCapture : IAudioCapture
{
    /// <inheritdoc />
    public AudioFormat Format { get; } = AudioFormat.Pcm16Mono16k;

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
