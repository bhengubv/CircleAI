// SpokenSessionTests.cs
//
// A meeting is not one utterance, and the old path treated it as one.
//
// The streaming transcriber re-decoded the WHOLE buffer roughly once a second,
// so the cost of the next update grew with everything already said. Twenty
// minutes in, it was decoding twenty minutes of audio, once a second, on a
// phone. That is not slow, it is a design that cannot reach the length it is for.
//
// SpokenSession cuts at the silences between sentences, decodes each piece once,
// and appends - so the cost of an update is one sentence and stops growing. Then
// it reads the whole recording again at the end, which is the only reason it
// keeps the audio at all: a piece cut at a silence is decoded with nothing after
// it, and a word at the join has only its left-hand side to be guessed from.
//
// These tests drive it with synthetic audio and a fake transcriber, so the
// endpointing is what is under test rather than whisper.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class SpokenSessionTests
{
    private const int Rate = 16_000;

    /// <summary>A block of "speech" or silence, 16-bit mono at 16 kHz.</summary>
    private static byte[] Block(double ms, double amplitude)
    {
        var samples = (int)(Rate * ms / 1000);
        var bytes = new byte[samples * 2];
        var rnd = new Random(5);
        for (var i = 0; i < samples; i++)
        {
            var v = (short)((Math.Sin(i * 0.05) * 0.8 + (rnd.NextDouble() - 0.5) * 0.2)
                            * amplitude * 32767);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    private static byte[] Speech(double ms) => Block(ms, 0.30);
    private static byte[] Quiet(double ms) => Block(ms, 0.002);

    /// <summary>Returns a different phrase per call, so pieces are distinguishable.</summary>
    private sealed class FakeTranscriber : IVoiceTranscriber
    {
        private readonly Queue<string> _say;
        public List<double> Lengths { get; } = [];
        public int Calls { get; private set; }

        public FakeTranscriber(params string[] say) => _say = new Queue<string>(say);

        public Task<TranscriptionResult> TranscribeAsync(
            ReadOnlyMemory<byte> pcm, CancellationToken ct = default, string? language = null)
        {
            Calls++;
            Lengths.Add(pcm.Length / (double)(Rate * 2));
            var text = _say.Count > 0 ? _say.Dequeue() : "";
            return Task.FromResult(new TranscriptionResult(text, 0.9f, "en"));
        }

        public IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingTranscriber : IVoiceTranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(
            ReadOnlyMemory<byte> pcm, CancellationToken ct = default, string? language = null) =>
            throw new InvalidOperationException("model fell over");

        public IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Feeds blocks straight in, the way a microphone would.</summary>
    private static async Task Feed(SpokenSession s, params byte[][] blocks)
    {
        foreach (var b in blocks)
            await s.AcceptAsync(b, CancellationToken.None);
    }

    private static SpokenSession Session(IVoiceTranscriber t, double silenceMs = 1000) =>
        new(new NullAudioCapture(), t, "en") { SilenceToEndMs = silenceMs };

    [Fact]
    public async Task A_sentence_then_a_pause_is_written_down()
    {
        var fake = new FakeTranscriber("the meeting is at three");
        await using var s = Session(fake);

        await Feed(s, Speech(900), Quiet(1200));

        Assert.Equal(1, fake.Calls);
        Assert.Equal("the meeting is at three", s.Text);
    }

    [Fact]
    public async Task Talking_again_appends_rather_than_replaces()
    {
        // THE OWNER'S SHAPE. Speak, pause, speak again - and the second piece
        // continues the first instead of starting a new transcript.
        var fake = new FakeTranscriber("the meeting is at three", "in the small room");
        await using var s = Session(fake);

        await Feed(s, Speech(900), Quiet(1200), Speech(900), Quiet(1200));

        Assert.Equal(2, fake.Calls);
        Assert.Equal("the meeting is at three in the small room", s.Text);
    }

    [Fact]
    public async Task Each_piece_costs_only_its_own_length()
    {
        // THE WHOLE POINT, AND THE THING THE OLD PATH GOT WRONG. It re-decoded
        // everything said so far on every update, so the tenth sentence of a
        // meeting cost ten sentences. Here the tenth costs one.
        var fake = new FakeTranscriber(Enumerable.Repeat("piece", 4).ToArray());
        await using var s = Session(fake);

        for (var i = 0; i < 4; i++) await Feed(s, Speech(900), Quiet(1200));

        Assert.Equal(4, fake.Calls);
        Assert.All(fake.Lengths, len => Assert.InRange(len, 0.5, 1.5));
    }

    [Fact]
    public async Task A_pause_shorter_than_the_gap_does_not_cut_a_sentence()
    {
        // People breathe mid-sentence. Cutting there would shred one sentence
        // across three decodes and three chances to punctuate it wrongly.
        var fake = new FakeTranscriber("one long sentence with a breath in it");
        await using var s = Session(fake, silenceMs: 1000);

        await Feed(s, Speech(700), Quiet(400), Speech(700), Quiet(1200));

        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task A_cough_is_not_a_sentence()
    {
        // A door, a chair, a knock. Without a minimum, every one of them opens a
        // piece and whisper answers a third of a second of nothing with a word it
        // invented - and in a meeting room that is most of the transcript.
        var fake = new FakeTranscriber("should never be asked for");
        await using var s = Session(fake);

        await Feed(s, Speech(150), Quiet(1200));

        Assert.Equal(0, fake.Calls);
        Assert.Equal("", s.Text);
    }

    [Fact]
    public async Task Silence_alone_is_never_transcribed()
    {
        var fake = new FakeTranscriber("invented");
        await using var s = Session(fake);

        await Feed(s, Quiet(5000));

        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task A_speaker_who_never_pauses_is_still_cut()
    {
        // Whisper's window is thirty seconds and anything past it is not read at
        // all. A cut on length is a worse cut than a silence and far better than
        // losing the tail of somebody who talks for two minutes.
        var fake = new FakeTranscriber("first part", "second part");
        var bounded = new SpokenSession(new NullAudioCapture(), fake, "en")
        {
            SilenceToEndMs = 1000,
            MaxPieceSeconds = 2,
        };

        // Four seconds of unbroken speech against a two-second ceiling.
        for (var i = 0; i < 8; i++)
            await bounded.AcceptAsync(Speech(500), CancellationToken.None);

        Assert.True(fake.Calls >= 2, $"unbroken speech was cut {fake.Calls} times");
        Assert.All(fake.Lengths, len => Assert.True(len <= 2.6, $"a piece ran to {len:0.0}s"));
        await bounded.DisposeAsync();
    }

    [Fact]
    public async Task Stopping_mid_sentence_keeps_the_sentence()
    {
        // The piece is only ever written down by a silence, and pressing stop
        // means that silence never comes. Without the flush, ending a recording
        // loses the last thing said into it.
        var fake = new FakeTranscriber("and the last thing I said");
        var s = new SpokenSession(new NullAudioCapture(), fake, "en");

        await s.AcceptAsync(Speech(900), CancellationToken.None);
        Assert.Equal(0, fake.Calls);                    // nothing yet - no silence

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await s.ListenAsync(cts.Token);                 // cancelled: must flush

        Assert.Equal(1, fake.Calls);
        Assert.Equal("and the last thing I said", s.Text);
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Reading_it_again_replaces_the_pieced_together_text()
    {
        // THE REASON THE AUDIO IS KEPT. The live text is written a sentence at a
        // time with nothing after each one; read whole, the same audio has both
        // sides of every join.
        var fake = new FakeTranscriber(
            "the meeting is at three",
            "in the small room",
            "The meeting is at three, in the small room.");
        await using var s = Session(fake);

        await Feed(s, Speech(900), Quiet(1200), Speech(900), Quiet(1200));
        Assert.Equal("the meeting is at three in the small room", s.Text);

        var whole = await s.ReadAgainAsync();

        Assert.Equal("The meeting is at three, in the small room.", whole);
        Assert.Equal(whole, s.Text);
    }

    [Fact]
    public async Task The_final_pass_sees_every_piece_at_once()
    {
        var fake = new FakeTranscriber("a", "b", "whole thing");
        await using var s = Session(fake);

        await Feed(s, Speech(900), Quiet(1200), Speech(900), Quiet(1200));
        await s.ReadAgainAsync();

        // Two pieces of ~0.9 s each, then one pass over both.
        Assert.Equal(3, fake.Calls);
        Assert.True(fake.Lengths[2] > fake.Lengths[0] + fake.Lengths[1] - 0.2,
            "the closing pass did not cover the whole recording");
    }

    [Fact]
    public async Task A_failed_final_pass_does_not_lose_the_meeting()
    {
        // Nineteen minutes of transcript must not depend on the last decode
        // succeeding.
        var fake = new FakeTranscriber("the meeting is at three");
        await using var s = Session(fake);
        await Feed(s, Speech(900), Quiet(1200));

        await using var broken = new SpokenSession(new NullAudioCapture(), new ThrowingTranscriber());
        await broken.AcceptAsync(Speech(900), CancellationToken.None);
        await broken.AcceptAsync(Quiet(1200), CancellationToken.None);

        // The throwing transcriber loses its own piece and nothing else.
        Assert.Equal("", broken.Text);
        Assert.Equal("the meeting is at three", s.Text);
    }

    [Fact]
    public async Task One_bad_piece_does_not_end_the_session()
    {
        await using var s = Session(new ThrowingTranscriber());

        await Feed(s, Speech(900), Quiet(1200), Speech(900), Quiet(1200));

        // Two pieces attempted, both lost, session still alive and honest.
        Assert.Equal("", s.Text);
    }

    [Fact]
    public async Task The_audio_is_dropped_when_the_session_ends()
    {
        // "Nothing is kept" is a promise about this line. The recording exists
        // for the length of the session so the closing pass can read it, and goes
        // when the session goes.
        var fake = new FakeTranscriber("something");
        var s = new SpokenSession(new NullAudioCapture(), fake, "en") { SilenceToEndMs = 1000 };

        await s.AcceptAsync(Speech(900), CancellationToken.None);
        await s.AcceptAsync(Quiet(1200), CancellationToken.None);
        Assert.True(s.RecordedSeconds > 0.5);

        await s.DisposeAsync();

        Assert.Equal(0, s.RecordedSeconds);
    }

    [Fact]
    public async Task A_long_meeting_stops_recording_rather_than_running_out_of_memory()
    {
        // THE ARITHMETIC THE SCREEN'S OWN COPY SIGNS UP FOR. 16 kHz at sixteen
        // bits is 32 KB of speech per second, so an hour of people actually
        // talking is about 115 MB - in a list that doubles as it grows, on a
        // phone already carrying a half-gigabyte model. Left unbounded, a long
        // meeting takes the app down at the END, having transcribed all of it.
        var fake = new FakeTranscriber(Enumerable.Repeat("piece", 20).ToArray());
        await using var s = new SpokenSession(new NullAudioCapture(), fake, "en")
        {
            SilenceToEndMs = 1000,
            MaxRecordedSeconds = 2,          // a ceiling a test can reach
        };

        for (var i = 0; i < 6; i++)
            await Feed(s, Speech(900), Quiet(1200));

        Assert.True(s.RecordingFull, "the recording grew past its ceiling");
        Assert.True(s.RecordedSeconds <= 2.5,
            $"the recording reached {s.RecordedSeconds:0.0}s against a 2s ceiling");

        // AND THE TRANSCRIPT IS UNAFFECTED. What is lost is the closing pass over
        // the earliest part, not a word of what was said.
        Assert.Equal(6, fake.Calls);
        Assert.Contains("piece", s.Text);
    }

    [Fact]
    public async Task A_partial_re_read_does_not_replace_a_complete_transcript()
    {
        // THE TRAP IN CAPPING IT. Once the recording is full the closing pass
        // covers only what was kept, so replacing the live text with it would
        // silently delete the beginning of a long meeting - which is a far worse
        // outcome than the slightly rougher wording the live pass produces.
        var fake = new FakeTranscriber("one", "two", "three", "ONLY THE TAIL");
        await using var s = new SpokenSession(new NullAudioCapture(), fake, "en")
        {
            SilenceToEndMs = 1000,
            MaxRecordedSeconds = 1,
        };

        for (var i = 0; i < 3; i++)
            await Feed(s, Speech(900), Quiet(1200));

        var live = s.Text;
        var after = await s.ReadAgainAsync();

        Assert.True(s.RecordingFull);
        Assert.Equal(live, after);
        Assert.DoesNotContain("ONLY THE TAIL", after);
    }

    [Fact]
    public async Task A_disposed_session_refuses_rather_than_pretends()
    {
        var s = new SpokenSession(new NullAudioCapture(), new FakeTranscriber());
        await s.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => s.ReadAgainAsync());
    }
}
