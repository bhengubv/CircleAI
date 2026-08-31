// WakeWordListTests.cs
//
// The wake list is an ACCESS control: an unlisted phrase must not wake the
// assistant. Two things here are easy to break and impossible to notice on a
// phone — an empty list (matches nothing, reads as a dead mic) and phrase
// normalisation (the product phrase is written "Hey B!" but no transcriber ever
// emits the exclamation mark, so a naive match would never fire).

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class WakeWordListTests
{
    // ── normalisation ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Hey B!", "hey b")]
    [InlineData("hey b", "hey b")]
    [InlineData("  Hey,  B.  ", "hey b")]
    [InlineData("HEY B!!!", "hey b")]
    [InlineData("Hey B2", "hey b2")]          // digits survive
    public void Normalise_StripsPunctuationAndCase(string input, string expected)
        => Assert.Equal(expected, EnergyWakeWordDetector.Normalise(input));

    [Fact]
    public void Normalise_TheProductPhraseMatchesWhatAsrActuallyEmits()
    {
        // The bug this guards: default was changed to "Hey B!" for branding, but
        // Whisper emits "Hey B," or "hey b". A raw Contains would never fire and
        // the assistant would simply never wake.
        var configured = EnergyWakeWordDetector.Normalise(EnergyWakeWordDetector.DefaultWakeWord);

        foreach (var heard in new[] { "Hey B.", "hey b", "Hey B,", "  HEY  B!  " })
            Assert.Contains(configured, EnergyWakeWordDetector.Normalise(heard), StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_EmptyAndBlankCollapseToEmpty()
    {
        Assert.Equal(string.Empty, EnergyWakeWordDetector.Normalise(""));
        Assert.Equal(string.Empty, EnergyWakeWordDetector.Normalise("   "));
        Assert.Equal(string.Empty, EnergyWakeWordDetector.Normalise("!!!"));
    }

    // ── the access list ──────────────────────────────────────────────────────

    [Fact]
    public void EmptyList_IsRejected_NotSilentlyAcceptedAsMatchEverything()
    {
        // An all-blank list must throw. If it were accepted, the normalised
        // phrase would be "" and Contains("") is true for every utterance —
        // the detector would wake on ANY sound, which is the exact inverse of
        // what an access list is for.
        var ex = Assert.Throws<ArgumentException>(() =>
            new EnergyWakeWordDetector(
                new SilentCapture(), new NullTranscriber(), new[] { "  ", "", "\t" }));

        Assert.Contains("wake phrase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultsToTheProductPhrase()
    {
        await using var d = new EnergyWakeWordDetector(new SilentCapture(), new NullTranscriber());
        Assert.Equal("Hey B!", d.WakeWord);
        Assert.Single(d.WakeWords);
    }

    [Fact]
    public async Task ListIsDeduplicatedCaseInsensitively_AndPrimaryIsFirst()
    {
        await using var d = new EnergyWakeWordDetector(
            new SilentCapture(), new NullTranscriber(),
            new[] { "Hey B!", "hey b!", "Hey Thabo", " Hey Thabo " });

        Assert.Equal("Hey B!", d.WakeWord);
        Assert.Equal(2, d.WakeWords.Count);
    }

    [Fact]
    public void InterfaceDefault_ExposesThePrimaryWhenAnImplementerHasNoList()
    {
        // WakeWords is a default interface member so older implementers keep
        // compiling — it must still return something non-empty for them.
        IWakeWordDetector legacy = new LegacySinglePhraseDetector();
        Assert.Single(legacy.WakeWords);
        Assert.Equal("Hey B!", legacy.WakeWords[0]);
    }

    // ── disposal ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DoesNotThrow_OnADetectorThatNeverStarted()
    {
        // REGRESSION. DisposeAsync set _disposed = true BEFORE calling
        // StopAsync, which guards with ObjectDisposedException.ThrowIf(_disposed)
        // — so every dispose threw, and the catch only covered cancellation.
        // On the phone that meant stopping voice threw instead of releasing the
        // microphone. It stayed hidden because VoiceLoop wraps _ears.StopAsync()
        // in a swallowing catch; only a direct dispose exposed it.
        var d = new EnergyWakeWordDetector(new SilentCapture(), new NullTranscriber());
        await d.DisposeAsync();          // must not throw
        await d.DisposeAsync();          // idempotent
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow_AfterStartAndStop()
    {
        var d = new EnergyWakeWordDetector(new SilentCapture(), new NullTranscriber());
        await d.StartAsync();
        await d.StopAsync();
        await d.DisposeAsync();
    }

    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class SilentCapture : IAudioCapture
    {
        public AudioFormat Format { get; } = AudioFormat.Pcm16Mono16k;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullTranscriber : IVoiceTranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct = default, string? language = null)
            => Task.FromResult(new TranscriptionResult(string.Empty, 0f, "und"));

        public async IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Implements only the pre-list surface, to prove the DIM covers it.</summary>
    private sealed class LegacySinglePhraseDetector : IWakeWordDetector
    {
        public string WakeWord => "Hey B!";
        public bool IsListening => false;
        public event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
