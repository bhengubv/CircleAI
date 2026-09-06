#nullable enable

// WhisperNetTranscriber.cs
//
// The IVoiceTranscriber that actually runs. The hand-rolled WhisperInterop
// P/Invokes whisper.dll — a native library that ships NOWHERE in this repo or
// via NuGet, so it throws DllNotFoundException on every platform (the same
// defect class the MNN packaging gap was). This implementation rides Whisper.net
// instead, which bundles the whisper.cpp native library per-RID as a NuGet
// package — so ASR works out of the box, no hand-built native to ABI-match.
//
// Proven end to end by tools/stt-hear: ggml-tiny transcribed the JFK sample
// verbatim on Windows, 2026-07-21. whisper.cpp is MIT / de-Googled and reads the
// exact ggml model the registry catalogues.

using System.Runtime.CompilerServices;
using Whisper.net;

namespace CircleAI.Voice;

/// <summary>
/// <see cref="IVoiceTranscriber"/> backed by Whisper.net (whisper.cpp). Consumes
/// PCM 16-bit 16 kHz mono, as the interface specifies.
/// </summary>
public sealed class WhisperNetTranscriber : IVoiceTranscriber
{
    /// <summary>PCM sample rate whisper expects, and the only one it accepts.</summary>
    private const int SampleRate = 16_000;

    /// <summary>
    /// Encoder states per second of audio: whisper's 30 s window is 1500 states.
    /// </summary>
    private const int StatesPerSecond = 1500 / 30;

    private readonly WhisperFactory _factory;
    private readonly string _language;

    /// <summary>Which language the cached processor was built for.</summary>
    private string? _processorLanguage;

    /// <summary>
    /// One processor, kept between calls, and the window it was built for.
    /// </summary>
    /// <remarks>
    /// A PROCESSOR WAS BUILT AND THROWN AWAY ON EVERY UTTERANCE. Building one
    /// allocates whisper's decode state — the self- and cross-attention KV
    /// buffers — so every turn paid for an allocation the previous turn had
    /// already made and discarded. Kept here, the second utterance onward finds
    /// it ready.
    /// <para>
    /// Rebuilt only when the window size changes, because the window is fixed
    /// at build time. In practice spoken questions cluster in length, so the
    /// bucketing below means most turns reuse and only an unusually long or
    /// short one pays a rebuild.
    /// </para>
    /// </remarks>
    private WhisperProcessor? _processor;
    private int _processorContext = -1;

    /// <summary>
    /// Serialises access to <see cref="_processor"/>.
    /// </summary>
    /// <remarks>
    /// Whisper's state is not re-entrant, and reusing one processor makes that
    /// this class's problem rather than the caller's — building a fresh one per
    /// call used to hide it. StreamTranscribeAsync calls straight back into
    /// TranscribeAsync, so this is a real path, not a theoretical one.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Threads whisper decodes on. Defaults to half the cores, capped at four.
    /// </summary>
    /// <remarks>
    /// NEVER LEFT TO THE DEFAULT AGAIN, because the default was invisible. Big/
    /// little phones report every core through <see cref="Environment.ProcessorCount"/>
    /// — the P30 Lite says eight and has four slow A53s among them — so counting
    /// cores and believing the number puts half the work on the cores least able
    /// to do it. Half, capped at four, keeps to the fast cluster on the phones
    /// this has to run on, and being an init property it can be overridden by a
    /// host that has measured its own.
    /// </remarks>
    public int Threads { get; init; } =
        Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>
    /// Widest encoder window, in states. 1500 is whisper's full 30 seconds.
    /// </summary>
    public int MaxAudioContext { get; init; } = 1500;

    /// <summary>Words the speaker is likely to use, to bias the decoder toward.</summary>
    /// <remarks>
    /// WHAT A SMALL MODEL GETS WRONG IS NAMES AND MONEY, NOT GRAMMAR. Measured on
    /// a P30 on 2026-09-07, a meeting played through a speaker came back with
    /// seventy-five of seventy-eight words exact - and the three it missed were
    /// "Thandi" as "Tandy", "Sipho" as "Saifo", and "rand" as "rent". Whole
    /// sentences of ordinary English were perfect; the currency of the country
    /// this app is built for was not.
    /// <para>
    /// whisper's initial_prompt exists for this: it primes the decoder with text
    /// it should consider likely, which pulls a borderline decision toward a word
    /// that belongs in the domain rather than one that merely sounds nearer.
    /// </para>
    /// <para>
    /// IT IS A BIAS AND IT CUTS BOTH WAYS - whisper will occasionally emit a
    /// primed word that was never said, which is why this is a property with no
    /// default rather than a list baked in here. A caller that knows the domain
    /// sets it; a caller that does not gets the model unprompted.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// SETTABLE, NOT INIT-ONLY, because the transcriber is built once and shared
    /// while what is being spoken about is not. A session knows its domain and
    /// the thing that opened the model does not, so the alternative is a second
    /// WhisperFactory - another copy of the model in memory on a phone that has
    /// 3,7 GB - to change one string.
    /// <para>
    /// Changing it drops the cached processor, because the prompt is fixed when
    /// the processor is built: a caller that set this and saw no change would
    /// have no way of knowing why.
    /// </para>
    /// </remarks>
    public string? Vocabulary
    {
        get => _vocabulary;
        set
        {
            if (string.Equals(_vocabulary, value, StringComparison.Ordinal)) return;
            _vocabulary = value;
            _processor?.Dispose();
            _processor = null;
            _processorContext = -1;
        }
    }

    private string? _vocabulary;

    /// <summary>
    /// Encoder window wide enough for <paramref name="seconds"/> of audio.
    /// </summary>
    /// <remarks>
    /// THE ENCODER RAN OVER 30 SECONDS NO MATTER HOW LONG ANYBODY SPOKE.
    /// Whisper pads its input to a fixed 30 s window and, left alone, attends
    /// over the whole thing — so a 5.3 second question cost exactly what half a
    /// minute of speech costs. Measured on a P30 Lite: 6 834 ms to transcribe
    /// 5.3 s, the single largest wait between someone finishing a sentence and
    /// hearing an answer.
    /// <para>
    /// Sizing the window to the audio is whisper.cpp's own <c>audio_ctx</c>
    /// knob. The cost is not free of consequence: too narrow a window truncates
    /// the tail of what was said, so this asks for a fifth more than the audio
    /// needs and never goes below 256 states — about five seconds — however
    /// short the clip.
    /// </para>
    /// <para>
    /// Rounded to 128s so that utterances of similar length land on the same
    /// window and reuse the same processor instead of rebuilding for every
    /// slightly-different question.
    /// </para>
    /// </remarks>
    internal static int AudioContextFor(double seconds, int max = 1500)
    {
        var needed = (int)Math.Ceiling(seconds * StatesPerSecond * 1.2);
        var bucketed = (needed + 127) / 128 * 128;
        return Math.Clamp(bucketed, 256, max);
    }

    /// <param name="modelPath">Path to a whisper.cpp ggml model (e.g. ggml-tiny.bin).</param>
    /// <param name="language">
    /// BCP-47 language, or <c>"auto"</c> to detect. Default <c>"auto"</c>.
    /// </param>
    public WhisperNetTranscriber(string modelPath, string language = "auto")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Whisper ggml model not found at '{modelPath}'. The registry catalogues " +
                "Whisper-tiny-ggml (Source=HuggingFace) — download it first.", modelPath);

        // Loads the model once; reused across calls. Native lib comes from the
        // Whisper.net.Runtime NuGet package, so there is no DllNotFoundException.
        _factory = WhisperFactory.FromPath(modelPath);
        _language = string.IsNullOrWhiteSpace(language) ? "auto" : language;
    }

    /// <summary>
    /// The primary subtag Whisper wants: "ja" from "ja", "ja-JP" or "  ja  ".
    /// </summary>
    /// <remarks>
    /// Returns null for null, blank or "auto", so the caller's value falls back
    /// to the constructor's rather than pinning the engine to nonsense.
    /// </remarks>
    private static string? Primary(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        var dash = t.IndexOf('-');
        if (dash > 0) t = t[..dash];
        return t.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : t.ToLowerInvariant();
    }

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcmAudio, CancellationToken ct = default, string? language = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // THE CALLER'S LANGUAGE BEATS THE CONSTRUCTOR'S, and the constructor's
        // default is "auto". Detection is a guess from a few seconds of audio,
        // and tiny makes a poor one that leans to English - so an interpreter
        // that knows which half is speaking should never be made to rely on it.
        var effective = Primary(language) ?? _language;

        var samples = Pcm16ToFloat(pcmAudio.Span);
        if (samples.Length == 0)
            return new TranscriptionResult(string.Empty, 0f, "und");

        var seconds = samples.Length / (double)SampleRate;
        var window  = AudioContextFor(seconds, MaxAudioContext);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            var processor = Rent(window, effective, out var built);
            var buildMs = total.ElapsedMilliseconds;

            var text = new System.Text.StringBuilder();
            double probSum = 0;
            int segCount = 0;
            string lang = effective == "auto" ? "und" : effective;

            await foreach (var seg in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                text.Append(seg.Text);
                probSum += seg.Probability;
                segCount++;
                if (!string.IsNullOrWhiteSpace(seg.Language)) lang = seg.Language;
            }

            var result = text.ToString().Trim();

            // WHAT THE CALLER COULD NOT SEE. A stopwatch around this method gives
            // one number; these are the four that say what to do about it —
            // whether the window was sized to the audio, whether the processor
            // was reused, how many threads did the work, and how much of the
            // time was setup rather than decoding.
            //
            // AND THE RATIO, because milliseconds alone do not say whether it is
            // acceptable. Speech is answered in real time or it is not: x1 means
            // the phone finished thinking as you stopped talking, and x9 — which
            // is what 2,8 s of audio decoding for 24,7 s actually was — means
            // nobody waits for the end of it. One number, and it is the number
            // that decides whether this can be demonstrated.
            var decodeMs = total.ElapsedMilliseconds - buildMs;
            var realtime = seconds > 0 ? decodeMs / (seconds * 1000) : 0;

            VoiceTrace.Write(
                $"stt: {seconds:F1} s audio | window={window}/{MaxAudioContext} " +
                $"({window * 100 / MaxAudioContext}%) | threads={Threads} | " +
                $"{(built ? $"built={buildMs} ms" : "reused")} | " +
                $"decode={decodeMs} ms (x{realtime:0.0} realtime) | " +
                $"{result.Length} chars | {segCount} seg");

            var confidence = segCount > 0 ? (float)(probSum / segCount) : 0f;
            return new TranscriptionResult(result, confidence, lang);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The processor for <paramref name="window"/>, building one only when the
    /// window it was made for no longer fits.
    /// </summary>
    /// <remarks>
    /// Caller must hold <see cref="_gate"/>.
    /// </remarks>
    private WhisperProcessor Rent(int window, string language, out bool built)
    {
        // Keyed on the language as well as the window: a cached processor built
        // for English will keep transcribing Japanese as English, which is
        // exactly the bug this parameter exists to fix.
        if (_processor is not null && _processorContext == window && _processorLanguage == language)
        {
            built = false;
            return _processor;
        }

        // Disposed BEFORE the replacement is built, not after. Two whisper
        // states on a phone with 3.7 GB — where the language model already
        // holds hundreds of MB — is how a rebuild turns into an out-of-memory
        // kill, and the old one is worthless the moment the window changes.
        _processor?.Dispose();
        _processor = null;

        _processorLanguage = language;
        var builder = _factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Threads)
            .WithAudioContextSize(window)
            // NOTHING CARRIES OVER FROM THE LAST QUESTION. Whisper will feed a
            // previous transcript in as a prompt to help it stay consistent,
            // which is right for one long recording and wrong for a series of
            // unrelated questions — it costs decode time and lets an earlier
            // question colour the next one's wording.
            .WithNoContext()

            // ONE PASS, NOT SIX.
            //
            // whisper.cpp does not decode once. When a pass fails either of its
            // confidence checks — average log-probability too low, or the output
            // too repetitive — it throws the result away and decodes the whole
            // thing again at a higher temperature, stepping by temperature_inc
            // until it passes or runs out of temperatures. Six passes is the
            // default ceiling and NOTHING IN THE LOG SAYS IT HAPPENED.
            //
            // Measured on a P30 Lite, on the warm processor:
            //
            //     stt: 2,8 s audio | window=256/1500 | built=22 ms
            //          | decode=24704 ms | 6 chars | 1 seg
            //     stt: built=15 ms | decode=11648 ms
            //     stt: built=53 ms | decode=29541 ms
            //
            // Twenty-five seconds to produce SIX CHARACTERS. Tiny's decoder
            // emits a handful of tokens for that, which is milliseconds of work,
            // and the encoder was already cut to a fifth of its window by
            // AudioContextFor. The wild spread across clips of the same length is
            // the tell: it is not the audio that varies, it is how many times the
            // same audio got decoded and rejected.
            //
            // Zero disables the retry entirely, so a decode is a decode.
            //
            // WHAT IT COSTS, HONESTLY. The retries exist to rescue a pass that
            // fell into a repetition loop or produced low-confidence noise, and
            // without them a bad decode is returned rather than re-attempted. On
            // a phone that is the right trade by a wide margin: a rare garbled
            // sentence is recoverable — the person says it again — and a
            // twenty-five second wait is not, because nobody waits through it
            // twice. Confidence still comes back on the result for a caller that
            // wants to judge.
            .WithTemperatureInc(0f)

            ;

        // NAMES AND MONEY, WHICH IS WHAT A SMALL MODEL ACTUALLY GETS WRONG. See
        // Vocabulary: a prompt only when the caller has one, because priming with
        // words nobody is going to say invites the model to produce them.
        if (!string.IsNullOrWhiteSpace(Vocabulary)) builder.WithPrompt(Vocabulary);

        // PINNED, NOT INHERITED, and set apart from the chain because
        // WithGreedySamplingStrategy returns a builder for the STRATEGY rather
        // than the processor - the fluent line cannot carry on through it.
        //
        // Greedy is what this was already getting from the library's default,
        // and beam search would multiply the decode by the beam width, which is
        // exactly the cost just removed. Stating it means a library default
        // cannot quietly reintroduce it.
        //
        // best_of 1 for the same reason: whisper.cpp's own default draws several
        // candidate samples, which buys nothing once the temperature is fixed at
        // zero - the samples are identical by construction - and is another
        // multiplier waiting to be paid if it ever is not. Guarded rather than
        // cast outright: a library that changes the concrete type should cost a
        // lost optimisation, not a crash on the one path that hears people.
        if (builder.WithGreedySamplingStrategy() is GreedySamplingStrategyBuilder greedy)
            greedy.WithBestOf(1);

        _processor = builder.Build();

        _processorContext = window;
        built = true;
        return _processor;
    }

    /// <summary>
    /// How much new audio must arrive before a partial re-decode. Lower feels
    /// more live but costs a full decode each time; the whole buffer is
    /// re-decoded, so cost grows with utterance length.
    /// </summary>
    public double PartialIntervalSeconds { get; init; } = 1.0;

    /// <summary>
    /// Longest audio a partial will re-decode. Past this only the final decode
    /// runs, so a long monologue cannot drag the device into decoding a
    /// minute of audio every second.
    /// </summary>
    public double MaxPartialSeconds { get; init; } = 30.0;

    /// <inheritdoc />
    /// <remarks>
    /// Whisper is not natively a streaming recogniser — it decodes a whole
    /// utterance. This emits real partials anyway, by re-decoding the buffer so
    /// far each time roughly <see cref="PartialIntervalSeconds"/> of new audio
    /// arrives, then one authoritative <c>IsFinal</c> result at end of stream.
    /// <para>
    /// Two honest consequences. Partials can CHANGE as more context arrives —
    /// Whisper may revise earlier words — so a UI must replace the displayed
    /// text, never append to it. And this trades CPU for latency: each partial
    /// is a full decode of everything heard so far, which is why
    /// <see cref="MaxPartialSeconds"/> exists. Callers that cannot afford that
    /// should segment with a VAD and call <see cref="TranscribeAsync"/> per
    /// utterance instead.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioChunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        const int bytesPerSecond = 16_000 * 2;          // PCM16 16 kHz mono
        var partialEvery = (long)(PartialIntervalSeconds * bytesPerSecond);
        var partialCeiling = (long)(MaxPartialSeconds * bytesPerSecond);

        using var buffer = new MemoryStream();
        var lastPartialAt = 0L;
        var lastText = string.Empty;

        await foreach (var chunk in audioChunks.WithCancellation(ct).ConfigureAwait(false))
        {
            buffer.Write(chunk.Span);

            if (buffer.Length - lastPartialAt < partialEvery) continue;
            if (buffer.Length > partialCeiling) continue;

            lastPartialAt = buffer.Length;

            TranscriptionResult interim;
            try
            {
                interim = await TranscribeAsync(buffer.ToArray(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A failed partial is cosmetic — keep buffering so the FINAL
                // decode still gets every sample. Dropping the utterance here
                // would lose audio the user already spoke.
                continue;
            }

            // Suppress no-change partials: Whisper often returns identical text
            // for a second of trailing silence, and a UI that redraws on every
            // emission would flicker for no reason.
            if (string.IsNullOrWhiteSpace(interim.Text) || interim.Text == lastText) continue;

            lastText = interim.Text;
            yield return new PartialTranscription(interim.Text, IsFinal: false, interim.Confidence);
        }

        var result = await TranscribeAsync(buffer.ToArray(), ct).ConfigureAwait(false);
        yield return new PartialTranscription(result.Text, IsFinal: true, result.Confidence);
    }

    /// <summary>little-endian PCM16 → float[-1,1].</summary>
    internal static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        int n = pcm.Length / 2;
        var f = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            f[i] = s / 32768f;
        }
        return f;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        // THE GATE IS DELIBERATELY NOT DISPOSED. Disposing a SemaphoreSlim that
        // still has a waiter throws, and this runs from Activity.OnDestroy — which
        // Android calls while a transcription may be mid-flight. Observed on the
        // phone as a hard process kill:
        //
        //   AndroidRuntime: at WhisperNetTranscriber.DisposeAsync
        //                   at ItListener.DisposeAsync
        //                   at HomeActivity.OnDestroy
        //
        // Every Japanese turn died there, which read as a slow turn rather than a
        // crash because the log simply stopped after "listened=".
        //
        // A SemaphoreSlim with no AvailableWaitHandle holds nothing unmanaged, so
        // not disposing it leaks nothing. Correctness beats tidiness here.
        //
        // The processor still goes before the factory: it holds state belonging to
        // the factory, and freeing the factory underneath a live processor is a
        // native use-after-free that surfaces somewhere else entirely.
        try
        {
            _processor?.Dispose();
            _processor = null;
            _processorContext = -1;
            _factory.Dispose();
        }
        catch (Exception ex)
        {
            // Teardown must not take the process with it. A transcriber that will
            // not release cleanly is a leak until the process ends; a throw here
            // is a crash the person sees.
            VoiceTrace.Write($"stt: dispose failed, continuing — {ex.GetType().Name}: {ex.Message}");
        }

        return ValueTask.CompletedTask;
    }
}
