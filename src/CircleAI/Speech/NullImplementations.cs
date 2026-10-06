// NullImplementations.cs
//
// (2.3.0) Fail-closed defaults for each Speech contract. Lets hosting
// layers wire the Speech pack optionally; absence of a real backend
// degrades to deterministic empty answers.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Speech;

public sealed class NullSpeechRecognizer : ISpeechRecognizer
{
    public static readonly NullSpeechRecognizer Instance = new();
    public string BackendId => "null";
    public ValueTask<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audioPcm16Mono, int sampleRateHz,
        string? languageHint = null, CancellationToken ct = default)
        => ValueTask.FromResult(new TranscriptionResult(
            Text:          "",
            Language:      languageHint,
            Segments:      Array.Empty<TranscribedSegment>(),
            TotalDuration: TimeSpan.Zero));
}

public sealed class NullSpeechSynthesizer : ISpeechSynthesizer
{
    public static readonly NullSpeechSynthesizer Instance = new();
    public string BackendId => "null";
    public ValueTask<SynthesisResult> SynthesizeAsync(
        string text, string? voiceId = null, string? languageHint = null,
        CancellationToken ct = default)
        => ValueTask.FromResult(new SynthesisResult(
            AudioPcm16Mono: ReadOnlyMemory<byte>.Empty,
            SampleRateHz:   16_000,
            Duration:       TimeSpan.Zero));
}

public sealed class NullWakeWordDetector : IWakeWordDetector
{
    public string BackendId => "null";
    public IDisposable Subscribe(Func<WakeWordEvent, ValueTask> handler) => EmptyDisposable.Instance;
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

public sealed class NullOpticalCharacterRecognizer : IOpticalCharacterRecognizer
{
    public static readonly NullOpticalCharacterRecognizer Instance = new();
    public string BackendId => "null";
    public ValueTask<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> imageBytes, string? languageHint = "auto",
        CancellationToken ct = default)
        => ValueTask.FromResult(new OcrResult(
            Text:   "",
            Blocks: Array.Empty<OcrTextBlock>()));
}
