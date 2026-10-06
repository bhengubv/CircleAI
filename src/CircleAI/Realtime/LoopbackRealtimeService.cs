// LoopbackRealtimeService.cs
//
// (3.3.0) Built-in, in-process IRealtimeService — connects audio in to
// audio out (loopback), surfaces speech-started/ended events from
// silence detection, and replies to SendTextAsync with a TTS-shaped
// PCM stream of constant-frequency tone. Concrete vendor sessions
// (OpenAI / Gemini / etc.) ship in their own packages; this one makes
// CircleAI.Realtime usable end-to-end out of the box for tests + dev.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Realtime;

/// <summary>(3.3.0) Synthesise outbound audio for text. Default produces real silence
/// frames matching the text's expected speech duration (~80ms per word). Hosts that have
/// a real TTS engine plug it in via <see cref="LoopbackRealtimeService"/>'s constructor.</summary>
public delegate ValueTask<ReadOnlyMemory<byte>> LoopbackTextToAudio(string text, RealtimeAudioFormat format, CancellationToken ct);

public sealed class LoopbackRealtimeService : IRealtimeService
{
    private readonly LoopbackTextToAudio _textToAudio;

    public LoopbackRealtimeService() : this(SilenceTextToAudio) { }
    public LoopbackRealtimeService(LoopbackTextToAudio textToAudio)
        => _textToAudio = textToAudio ?? throw new ArgumentNullException(nameof(textToAudio));

    public string ProviderId => "loopback";
    public bool   IsConfigured => true;

    public ValueTask<IRealtimeSession> StartSessionAsync(RealtimeSessionConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var session = new LoopbackRealtimeSession(config, _textToAudio);
        return ValueTask.FromResult<IRealtimeSession>(session);
    }

    /// <summary>(3.3.0) Default: emit real silence frames sized to ~80ms per word.
    /// Real audio bytes (just zero amplitude) so downstream code that does signal
    /// processing or duration accounting works correctly.</summary>
    internal static ValueTask<ReadOnlyMemory<byte>> SilenceTextToAudio(string text, RealtimeAudioFormat format, CancellationToken ct)
    {
        var sr = LoopbackRealtimeSession.SampleRateOf(format);
        var wordCount = string.IsNullOrWhiteSpace(text) ? 0 : text.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var durationMs = Math.Max(50, wordCount * 80);
        var sampleCount = sr * durationMs / 1000;
        var bytes = new byte[sampleCount * 2];  // 16-bit silence (already zeros)
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
    }
}

public sealed class LoopbackRealtimeSession : IRealtimeSession
{
    private readonly RealtimeSessionConfig _config;
    private readonly LoopbackTextToAudio _textToAudio;
    private readonly Channel<RealtimeAudioFrame> _audio = Channel.CreateUnbounded<RealtimeAudioFrame>();
    private readonly Channel<RealtimeEvent>      _events = Channel.CreateUnbounded<RealtimeEvent>();
    private TimeSpan _offset;
    private bool _speaking;

    public LoopbackRealtimeSession(RealtimeSessionConfig config)
        : this(config, LoopbackRealtimeService.SilenceTextToAudio) { }

    public LoopbackRealtimeSession(RealtimeSessionConfig config, LoopbackTextToAudio textToAudio)
    {
        _config      = config;
        _textToAudio = textToAudio ?? throw new ArgumentNullException(nameof(textToAudio));
        SessionId    = $"loop-{Guid.NewGuid():N}";
    }

    public string SessionId { get; }

    public async IAsyncEnumerable<RealtimeAudioFrame> ReceiveAudioAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _audio.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (_audio.Reader.TryRead(out var f)) yield return f;
    }

    public ValueTask SendAudioAsync(RealtimeAudioFrame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var nowSpeaking = !IsSilent(frame.Pcm.Span);
        if (nowSpeaking != _speaking)
        {
            _events.Writer.TryWrite(nowSpeaking
                ? new SpeechStartedEvent(DateTimeOffset.UtcNow)
                : new SpeechEndedEvent(DateTimeOffset.UtcNow));
            _speaking = nowSpeaking;
        }
        // Loopback: echo received audio back as outbound.
        _audio.Writer.TryWrite(frame);
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendTextAsync(string text, CancellationToken ct = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        _events.Writer.TryWrite(new TranscriptDeltaEvent(DateTimeOffset.UtcNow, text, RealtimeDirection.Outbound));
        var pcm = await _textToAudio(text, _config.AudioFormat, ct).ConfigureAwait(false);
        if (!pcm.IsEmpty)
        {
            _audio.Writer.TryWrite(new RealtimeAudioFrame(pcm, _config.AudioFormat, _offset));
            _offset += TimeSpan.FromMilliseconds(pcm.Length / 2.0 / SampleRateOf(_config.AudioFormat) * 1000.0);
        }
        _events.Writer.TryWrite(new TranscriptFinalEvent(DateTimeOffset.UtcNow, text, RealtimeDirection.Outbound));
        _events.Writer.TryWrite(new TurnCompleteEvent(DateTimeOffset.UtcNow));
    }

    public ValueTask SendToolResultAsync(string callId, string resultJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callId))   throw new ArgumentException("callId required");
        if (resultJson is null)                  throw new ArgumentNullException(nameof(resultJson));
        _events.Writer.TryWrite(new TranscriptDeltaEvent(DateTimeOffset.UtcNow, $"[tool {callId}: {Truncate(resultJson, 60)}]", RealtimeDirection.Outbound));
        return ValueTask.CompletedTask;
    }

    public ValueTask CancelResponseAsync(CancellationToken ct = default)
    {
        _events.Writer.TryWrite(new TurnCompleteEvent(DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<RealtimeEvent> ReceiveEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _events.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (_events.Reader.TryRead(out var e)) yield return e;
    }

    public ValueTask DisposeAsync()
    {
        _audio.Writer.TryComplete();
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    internal static int SampleRateOf(RealtimeAudioFormat f) => f switch
    {
        RealtimeAudioFormat.Pcm16k => 16_000,
        RealtimeAudioFormat.Pcm24k => 24_000,
        RealtimeAudioFormat.Mulaw8k => 8_000,
        _ => 16_000,
    };

    private static bool IsSilent(ReadOnlySpan<byte> pcm)
    {
        // RMS-based silence detector over 16-bit linear PCM.
        if (pcm.Length < 64) return true;
        long sumSq = 0;
        var samples = pcm.Length / 2;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSq += s * s;
        }
        var rms = Math.Sqrt(sumSq / (double)samples);
        return rms < 250.0;  // ~ -42 dBFS
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
