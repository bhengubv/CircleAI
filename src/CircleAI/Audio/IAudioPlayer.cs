// IAudioPlayer.cs
//
// The speaker, as a contract.
//
// EXTRACTED FROM VoiceLoop.cs, WHICH IS WHY THIS FILE IS SO SMALL. The contract sat
// inside the file that composes the whole pipeline, so a head that only wanted to
// open the microphone - or only to play PCM somebody else synthesised - had to
// reference the assembly holding Whisper and the ONNX voices to see it. That cost
// a thin client 18 MB of ONNX Runtime it could not call.
//
// The namespace has not changed, so no call site has. See CircleAI.Audio.csproj.

using System;
using System.Threading;
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
