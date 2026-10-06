// NullImplementations.cs
//
// (3.3.0) Null defaults so DI containers stay green when no vendor is
// configured. NullRealtimeService throws on StartSession with a clear
// "no vendor wired" message.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Realtime;

/// <summary>(3.3.0) Throws on StartSessionAsync; reports IsConfigured=false.</summary>
public sealed class NullRealtimeService : IRealtimeService
{
    public static readonly NullRealtimeService Instance = new();

    public string ProviderId    => "null";
    public bool   IsConfigured  => false;

    public ValueTask<IRealtimeSession> StartSessionAsync(
        RealtimeSessionConfig config,
        CancellationToken     ct = default)
    {
        throw new InvalidOperationException(
            "No realtime vendor is registered. Add CircleAI.Realtime.Cloud connectors (OpenAI, Gemini, Nova, ElevenLabs, Ultravox).");
    }
}

/// <summary>(3.3.0) A session that yields nothing — fully muted.</summary>
public sealed class NullRealtimeSession : IRealtimeSession
{
    public string SessionId => "null";

    public async IAsyncEnumerable<RealtimeAudioFrame> ReceiveAudioAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask SendAudioAsync(RealtimeAudioFrame frame, CancellationToken ct = default)        => ValueTask.CompletedTask;
    public ValueTask SendTextAsync(string text, CancellationToken ct = default)                       => ValueTask.CompletedTask;
    public ValueTask SendToolResultAsync(string callId, string resultJson, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask CancelResponseAsync(CancellationToken ct = default)                              => ValueTask.CompletedTask;

    public async IAsyncEnumerable<RealtimeEvent> ReceiveEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
