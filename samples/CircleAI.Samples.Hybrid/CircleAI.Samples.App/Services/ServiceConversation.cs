// ServiceConversation.cs
//
// The conversation, with the thinking, the hearing and the voice all in CircleAI.
//
// DeviceConversation ran the whole loop in this process: a recogniser, a model, a
// synthesiser, a translator. This one keeps only the two cheap ends — the microphone
// and the speaker — and sends everything between them across the link.
//
// WHERE IT SAYS NO, IT SAYS WHY. Three things on this interface have no transaction
// on the wire: translating, reading a picture, and transcribing a file longer than
// one call carries. They decline with the reason rather than returning something
// shaped like an answer, because this repo has already had the other version of that
// bug — a swallowed throw let the text model answer about a photograph, politely and
// wrongly, every time.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Device;
using CircleAI.Client;
using CircleAI.Linking;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceConversation(LinkedBrain brain, IRemembers memory) : IConversation
{
    /// <inheritdoc />
    public Task<BrainState> StateAsync(CancellationToken ct = default) => brain.StateAsync(ct);

    /// <inheritdoc />
    /// <remarks>
    /// Preparing is binding, not loading. There is nothing here to warm up — the
    /// model is already resident in the service, which is the point of the split.
    /// </remarks>
    public async Task<string> PrepareAsync(IProgress<string>? progress = null,
                                           CancellationToken ct = default)
    {
        progress?.Report("connecting to CircleAI…");
        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        return state.Detail;
    }

    /// <inheritdoc />
    public async Task TurnAsync(IProgress<TurnState> updates, CancellationToken ct = default)
    {
        var heard = await ListenOnceAsync(updates, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(heard)) return;

        updates.Report(new TurnState(TurnPhase.Thinking, Heard: heard));
        var reply = await brain.AskAsync(heard, null, ct).ConfigureAwait(false);

        updates.Report(new TurnState(TurnPhase.Speaking, Heard: heard, Reply: reply));
        await SayAsync(reply, null, ct).ConfigureAwait(false);
        updates.Report(new TurnState(TurnPhase.Idle, Heard: heard, Reply: reply));
    }

    /// <inheritdoc />
    public Task<string?> DictateAsync(IProgress<TurnState> updates, CancellationToken ct = default,
                                      string? language = null)
        => ListenOnceAsync(updates, ct, language);

    /// <inheritdoc />
    /// <remarks>
    /// One turn, not a session. A continuous session needs the recogniser to say when
    /// somebody stopped talking, and over a request/response link the only end-of-turn
    /// this app can detect is its own timer — see <see cref="LinkAudio.MaxSeconds"/>.
    /// </remarks>
    public async Task<string> SessionAsync(IProgress<TurnState> updates, CancellationToken ct = default,
                                           string? language = null, double silenceMs = 5000)
    {
        await TurnAsync(updates, ct).ConfigureAwait(false);
        return string.Empty;
    }

    /// <inheritdoc />
    /// <remarks>
    /// DECLINED, and the limit is the reason. One link call carries about 25 seconds
    /// of audio; a file is minutes. Doing this properly means chunking with overlap
    /// and stitching the transcripts, which is real work and not something to fake
    /// with a truncated first slice that looks like a complete transcript.
    /// </remarks>
    public Task<Transcript> TranscribeFileAsync(string path, string? language = null,
                                                IProgress<double>? progress = null,
                                                CancellationToken ct = default)
        => Task.FromResult(Transcript.Nothing);

    /// <inheritdoc />
    /// <remarks>DECLINED: the link has no translate transaction.</remarks>
    public Task<string> TranslateAsync(string text, string fromTag, string toTag,
                                       CancellationToken ct = default)
        => Task.FromResult("Translating is not available in this app yet.");

    /// <inheritdoc />
    /// <remarks>
    /// DONE HERE, because it is formatting rather than intelligence: a transcript is
    /// already words with times on them, and turning that into SubRip needs no model
    /// and no service.
    /// </remarks>
    public string AsSubtitles(Transcript transcript, SubtitleFormat format = SubtitleFormat.SubRip)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var segments = transcript.Lines
            .Select(l => new CircleAI.Voice.TranscriptSegment(l.Text, l.Start, l.End) { Speaker = l.Speaker })
            .ToList();
        return format == SubtitleFormat.WebVtt
            ? CircleAI.Voice.Subtitles.ToVtt(segments)
            : CircleAI.Voice.Subtitles.ToSrt(segments);
    }

    /// <inheritdoc />
    public async Task SayAsync(string text, string? languageTag = null, CancellationToken ct = default)
    {
        var audio = await brain.SpeakAsync(text, languageTag, ct).ConfigureAwait(false);
        if (audio.Length == 0) return;

        // PLAYBACK STAYS HERE. An audio track is cheap and belongs to the app somebody
        // is looking at — it knows when the screen went off and when to stop.
        await using var player = new AndroidAudioPlayer();
        await player.PlayAsync(audio, LinkAudioFormat.SampleRate, LinkAudioFormat.Channels,
                               LinkAudioFormat.BitsPerSample, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// IT WRITES THE WORDS DOWN. THAT IS ALL IT DOES, and getting that wrong cost a
    /// working app on the phone. This asked the brain and then spoke the answer -
    /// two round trips over the link, the second needing a voice model - while the
    /// screen sat awaiting it BEFORE making its own real ask. The contract says, in
    /// as many words, that it "never throws and never keeps the caller waiting", and
    /// the typed screen calls it on the way in precisely because it is supposed to be
    /// free. What a person saw was their question sitting on screen with no reply, no
    /// error and nothing in logcat.
    /// <para>
    /// NOT AWAITED, and it cannot throw out of here: a memory that could take a
    /// conversation down with it would deserve to be turned off. Same shape as
    /// DeviceConversation.HeardAsync, which had it right all along.
    /// </para>
    /// <para>
    /// CancellationToken.None deliberately: the token belongs to the turn, and a turn
    /// that ends must not cancel the note about what was said in it.
    /// </para>
    /// </remarks>
    public Task HeardAsync(string said, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(said)) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try { await memory.LearnAsync(said, CancellationToken.None).ConfigureAwait(false); }
            catch { /* a memory is never worth an answer */ }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// DECLINED: the wire carries text and audio, and has no transaction for an image.
    /// Answering anyway would mean the text model replying about a picture it never
    /// saw — which is a bug this repo has already shipped once.
    /// </remarks>
    public Task<string> SeeAsync(string question, byte[] image, Action<string>? token = null,
                                 CancellationToken ct = default)
        => Task.FromResult("Looking at a picture is not available in this app yet.");

    /// <summary>Record until the link's limit, then ask the service for the words.</summary>
    /// <remarks>
    /// THE MICROPHONE IS THIS APP'S, the recogniser is not. Capture stops at
    /// <see cref="LinkAudio.MaxAudioBytes"/> rather than running on and failing the
    /// transact: the limit is known before a single sample is taken, so it is a stop
    /// condition and not an error.
    /// </remarks>
    private async Task<string?> ListenOnceAsync(IProgress<TurnState> updates, CancellationToken ct,
                                                string? language = null)
    {
        updates.Report(new TurnState(TurnPhase.Listening));

        var buffer = new List<byte>(LinkAudio.MaxAudioBytes);
        await using (var mic = new AndroidAudioCapture())
        {
            await foreach (var chunk in mic.CaptureAsync(ct).ConfigureAwait(false))
            {
                buffer.AddRange(chunk.ToArray());
                if (buffer.Count >= LinkAudio.MaxAudioBytes) break;
            }
        }

        if (buffer.Count == 0) return null;

        updates.Report(new TurnState(TurnPhase.Thinking, Detail: "hearing…"));
        var heard = await brain.TranscribeAsync(buffer.ToArray(), language, ct).ConfigureAwait(false);

        // An empty transcript is a RESULT — a quiet room — so it comes back as null
        // rather than an error a screen would show.
        return string.IsNullOrWhiteSpace(heard) ? null : heard;
    }
}
