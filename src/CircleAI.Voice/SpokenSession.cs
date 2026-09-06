// SpokenSession.cs
//
// A conversation, a meeting or a dictation, taken down as it happens - and then
// read again properly at the end.
//
// WHAT THIS REPLACES. The streaming path re-decoded the WHOLE buffer every
// second, so the cost of the next update grew with everything already said: a
// twenty-minute meeting spent its last minutes decoding twenty minutes of audio,
// once a second, on a phone. It is a design that cannot reach the length it is
// for.
//
// THE SHAPE, WHICH IS THE OWNER'S. Listening opens a session. Speech is cut into
// pieces at the silences between sentences, each piece is transcribed once, and
// the text is APPENDED to what came before. Talk again and it opens the next
// piece and carries on. Cost per update is the length of one sentence and stops
// growing; a meeting costs the same at minute twenty as at minute one.
//
// AND THEN IT READS IT AGAIN. The session keeps its own audio, so when it ends
// the whole recording can be put through in one pass. The live text is what you
// watch; the final pass is what you keep. This matters because the two are not
// the same quality and cannot be: a piece cut at a silence is decoded with no
// idea what follows it, and a word at the join has nothing after it to be
// disambiguated by. Read whole, it has both sides.
//
// NOTHING IS KEPT AFTERWARDS. The audio lives for the length of the session and
// is dropped when it is disposed, which is what the screens now promise - held
// while it is being used, never stored, never sent.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Voice;

/// <summary>One piece of a session, as it lands.</summary>
/// <param name="Text">What this piece said.</param>
/// <param name="All">Everything the session has heard so far, this piece included.</param>
/// <param name="Seconds">How long this piece of speech was.</param>
/// <param name="Final">
/// True for the single result of the closing pass over the whole recording,
/// false for the live pieces.
/// </param>
public sealed record SpokenPiece(string Text, string All, double Seconds, bool Final = false);

/// <summary>
/// Listens for as long as it is asked to, writing down each sentence as it ends.
/// </summary>
public sealed class SpokenSession : IAsyncDisposable
{
    private const int SampleRate = 16_000;
    private const int BytesPerSample = 2;

    private readonly IAudioCapture _capture;
    private readonly IVoiceTranscriber _transcriber;
    private readonly string? _language;

    private readonly List<byte> _session = [];
    private readonly List<byte> _piece = [];
    private readonly List<byte> _quiet = [];
    private readonly StringBuilder _all = new();

    private readonly SpeechGain _gain = new();
    private double _floor = 0.02;
    private double _quietMs;
    private double _speechMs;
    private bool _speaking;
    private bool _disposed;

    /// <param name="language">
    /// What is being spoken, when it is known. Worth passing: detection on a tiny
    /// model leans to English, and a session is many decodes rather than one, so
    /// a wrong guess is wrong repeatedly.
    /// </param>
    public SpokenSession(
        IAudioCapture capture, IVoiceTranscriber transcriber, string? language = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _language = language;
    }

    /// <summary>How long a gap ends a piece.</summary>
    /// <remarks>
    /// IT CANNOT BE ONE NUMBER FOR BOTH JOBS, which is why it is settable rather
    /// than a constant. Five seconds is right for a meeting: people pause to
    /// think, and cutting at every breath would shred a sentence across three
    /// decodes and three chances to get its punctuation wrong. It is far too long
    /// for a question, where the same five seconds is five seconds of somebody
    /// waiting for an answer with nothing on screen.
    /// </remarks>
    public double SilenceToEndMs { get; init; } = 5_000;

    /// <summary>Speech shorter than this is not a piece.</summary>
    /// <remarks>
    /// A door, a cough, a chair. Without it every knock in a meeting room opens
    /// and closes a piece, and whisper answers a third of a second of nothing
    /// with a word it invented.
    /// </remarks>
    public double MinSpeechMs { get; init; } = 400;

    /// <summary>Longest piece before it is cut regardless of silence.</summary>
    /// <remarks>
    /// Whisper's window is thirty seconds and anything past it is not read at
    /// all. Somebody who talks for two minutes without a real pause must still
    /// get their words, so the piece is closed on length as well - a worse cut
    /// than a silence, and far better than silently losing the tail.
    /// </remarks>
    public double MaxPieceSeconds { get; init; } = 25;

    /// <summary>How loud speech has to be, relative to the quiet in the room.</summary>
    /// <remarks>
    /// RELATIVE, NOT ABSOLUTE. An absolute level is a promise about the room, the
    /// microphone and the distance, and it breaks the first time any of the three
    /// changes - which on a phone is constantly. The floor tracks the quietest
    /// recent audio and speech is what stands above it.
    /// </remarks>
    public double SpeechOverFloor { get; init; } = 2.5;

    /// <summary>How much of the closing silence stays on the end of a piece.</summary>
    /// <remarks>
    /// A MOMENT, NOT THE WHOLE TIMEOUT. Whisper reads the end of a sentence
    /// better with a little quiet after it — the last word is not left hard
    /// against the edge of the window — but a piece that carried the ENTIRE
    /// silence timeout would be decoded with five seconds of room tone stapled to
    /// it. That widens the encoder window, costs real time on a phone, and hands
    /// the model a stretch of nothing to invent words for.
    /// </remarks>
    public double TailMs { get; init; } = 250;

    /// <summary>Everything heard so far.</summary>
    public string Text => _all.ToString();

    /// <summary>How much audio the session is holding, in seconds.</summary>
    public double RecordedSeconds => _session.Count / (double)(SampleRate * BytesPerSample);

    /// <summary>Raised as each piece is written down, and once more at the end.</summary>
    public event EventHandler<SpokenPiece>? Heard;

    /// <summary>
    /// Listen until cancelled, writing down each piece of speech as it ends.
    /// </summary>
    /// <remarks>
    /// Returns when the capture stops or the token is signalled. Cancelling is
    /// the normal way to end a session, not an error - so the last piece still in
    /// hand is flushed rather than thrown away, which is the difference between
    /// stopping a recording and losing the end of it.
    /// </remarks>
    public async Task ListenAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await foreach (var chunk in _capture.CaptureAsync(ct).ConfigureAwait(false))
                await AcceptAsync(chunk, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The ordinary end of a session.
        }

        // WHAT WAS STILL BEING SAID WHEN IT STOPPED. Without this, pressing stop
        // mid-sentence loses that sentence - the piece is only ever written down
        // by a silence that, by definition, never came.
        await ClosePieceAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Feeds one block of PCM in, as a microphone would.</summary>
    /// <remarks>
    /// PUBLIC BECAUSE A MICROPHONE IS NOT THE ONLY SOURCE. A session can be
    /// driven from a file, from a recording being replayed, or from a test with
    /// synthetic blocks - and the endpointing is the part worth exercising
    /// without a phone in the room. <see cref="ListenAsync"/> is this in a loop.
    /// </remarks>
    public async Task AcceptAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        if (chunk.Length < BytesPerSample) return;

        var ms = chunk.Length / (double)(SampleRate * BytesPerSample) * 1000;
        var rms = Rms(chunk.Span);

        // The floor falls quickly to a new quiet and rises slowly, so a room that
        // goes quiet is believed at once and one loud sentence does not convince
        // it that the room is loud.
        _floor = rms < _floor ? rms : _floor + (rms - _floor) * 0.002;

        var loud = rms > Math.Max(_floor * SpeechOverFloor, 0.006);

        if (loud)
        {
            // A GAP THAT TURNED OUT TO BE MID-SENTENCE. It was held back rather
            // than written into the piece, because most gaps end it; this one did
            // not, so it belongs in the middle of the sentence where it was said.
            if (_quiet.Count > 0)
            {
                _piece.AddRange(_quiet);
                _quiet.Clear();
            }

            _speaking = true;
            _speechMs += ms;
            _quietMs = 0;
            _piece.AddRange(chunk.Span);
        }
        else if (_speaking)
        {
            // HELD, NOT APPENDED. Writing silence into the piece as it arrives
            // means every piece carries the whole timeout: at the five-second
            // dictation setting, each sentence would be decoded with five seconds
            // of nothing stapled to it - which widens the encoder window, costs
            // real time on a phone, and hands whisper a stretch of room tone to
            // invent words for.
            _quietMs += ms;
            _quiet.AddRange(chunk.Span);
        }

        var longEnough = _piece.Count / (double)(SampleRate * BytesPerSample) >= MaxPieceSeconds;
        if (_speaking && (_quietMs >= SilenceToEndMs || longEnough))
            await ClosePieceAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Transcribes the piece in hand and appends it.</summary>
    private async Task ClosePieceAsync(CancellationToken ct)
    {
        // A LITTLE OF THE SILENCE, NOT ALL OF IT. Whisper reads the end of a
        // sentence better with a moment of quiet after it - the last word is not
        // left hanging at the edge of the window - but it only needs a moment.
        var tail = (int)(TailMs / 1000 * SampleRate) * BytesPerSample;
        if (_quiet.Count > 0)
            _piece.AddRange(_quiet.GetRange(0, Math.Min(tail, _quiet.Count)));
        _quiet.Clear();

        var audio = _piece.ToArray();
        _piece.Clear();

        var wasSpeaking = _speaking;
        var spoke = _speechMs;
        _speaking = false;
        _speechMs = 0;
        _quietMs = 0;

        // A cough is not a sentence. Dropped before it costs a decode, and before
        // whisper is given a third of a second of nothing to name.
        if (!wasSpeaking || audio.Length == 0 || spoke < MinSpeechMs) return;

        // THE RECORDING GETS IT EITHER WAY. What the closing pass reads is the
        // session as it was spoken, pauses included, so it has the same evidence
        // a person listening back would.
        _session.AddRange(audio);

        SpeechGain.Normalise(audio);

        var seconds = audio.Length / (double)(SampleRate * BytesPerSample);
        var said = await Read(audio, ct).ConfigureAwait(false);
        if (said.Length == 0) return;

        Append(said);
        Heard?.Invoke(this, new SpokenPiece(said, Text, seconds));
    }

    /// <summary>
    /// Reads the WHOLE recording again, in one pass, and replaces the text.
    /// </summary>
    /// <remarks>
    /// THE REASON THE AUDIO IS KEPT AT ALL. The live text is written a sentence
    /// at a time, and a sentence decoded alone has nothing after it: a word at
    /// the join is guessed with only its left-hand side, punctuation restarts at
    /// every piece, and a name said once at the beginning cannot inform the
    /// spelling of the same name at the end.
    /// <para>
    /// Read whole, all of that is available at once. It costs one more pass over
    /// audio that is already in hand, at the moment somebody has stopped talking
    /// and is not waiting on a word - which is the only moment in the whole
    /// session when a long decode is free.
    /// </para>
    /// <para>
    /// The live text is not discarded until this succeeds. A final pass that
    /// throws must not take the meeting with it.
    /// </para>
    /// </remarks>
    public async Task<string> ReadAgainAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session.Count == 0) return Text;

        var whole = _session.ToArray();
        SpeechGain.Normalise(whole);

        var said = await Read(whole, ct).ConfigureAwait(false);
        if (said.Length == 0) return Text;

        _all.Clear();
        _all.Append(said);

        Heard?.Invoke(this, new SpokenPiece(said, said, RecordedSeconds, Final: true));
        return said;
    }

    private async Task<string> Read(byte[] audio, CancellationToken ct)
    {
        try
        {
            var result = await _transcriber
                .TranscribeAsync(audio, ct, _language).ConfigureAwait(false);
            return (result?.Text ?? "").Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // ONE BAD PIECE IS NOT A BAD MEETING. A decode that throws loses its
            // own sentence and nothing else; taking the session down would lose
            // the nineteen minutes before it.
            VoiceTrace.Write($"session: could not read a piece: {ex.Message}");
            return "";
        }
    }

    /// <summary>Joins a piece to what is already written.</summary>
    /// <remarks>
    /// A space unless there is punctuation to sit against, because whisper
    /// returns each piece already trimmed and joining them bare produces
    /// "the meetingis at three".
    /// </remarks>
    private void Append(string piece)
    {
        if (_all.Length > 0 && _all[^1] is not (' ' or '\n')) _all.Append(' ');
        _all.Append(piece);
    }

    private static double Rms(ReadOnlySpan<byte> pcm16)
    {
        var count = pcm16.Length / BytesPerSample;
        if (count == 0) return 0;

        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            double v = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            sum += v * v;
        }
        return Math.Sqrt(sum / count) / 32768.0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE AUDIO GOES HERE AND NOWHERE ELSE. It was held for the length of the
    /// session so the closing pass could read it; the session is over, so it is
    /// dropped. That is the whole of what "nothing is kept" means.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _session.Clear();
        _piece.Clear();
        _quiet.Clear();
        return ValueTask.CompletedTask;
    }
}
