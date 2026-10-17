// TheEarsAreWiredToTheMouthTests.cs
//
// THAT OutLoud HAS CALLERS ON BOTH SIDES, WHICH IS THE ONLY THING THAT MATTERS.
//
// OutLoudTests proves the class is correct. Correct and unreached is this
// repo's signature defect, and on THIS ONE FEATURE it has now happened six
// times: the cold-brain handling in WakeTurn, the near-miss scoring in
// TakeBestProgress, DeviceWakePhrases.EnsureCurrent - whose own documentation
// said it was "called at every start" while having zero callers - SpeechGain's
// only caller being dead code, NearlySaid sitting beside the exact matcher that
// was wired instead, and the self-hearing this class exists to stop, which
// AndroidAudioPlayer already knew about in a comment and never told the wake
// loop.
//
// A green OutLoudTests with no callers would be the seventh. So these read the
// source and refuse to pass if either end of the wire is missing.
//
// WHY SOURCE-READING RATHER THAN A UNIT TEST. The two call sites live on an
// Android Service and an AudioTrack - neither can be constructed off a phone,
// which is precisely why every one of those six defects survived a green suite.
// Reading the file is crude and it is the only thing here that would have
// caught them.

using System;
using System.IO;
using Xunit;

namespace CircleAI.Tests;

public sealed class TheEarsAreWiredToTheMouthTests
{
    private static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CircleAI.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Source(params string[] parts)
    {
        var path = Path.Combine(Repo(), Path.Combine(parts));
        Assert.True(File.Exists(path), path + " has moved - this guard is now pointing at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>The file with its comments removed.</summary>
    /// <remarks>
    /// BOTH FILES QUOTE THE DEFECT IN THEIR OWN HEADERS, on purpose, so the
    /// next person reads why instead of rediscovering it on a phone. A guard
    /// that cannot tell code from the warning about the code passes on the
    /// warning alone - which would be satisfied by a file that explains the
    /// problem beautifully and does nothing about it.
    /// </remarks>
    private static string Code(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)) continue;
            if (t.StartsWith("///", StringComparison.Ordinal)) continue;
            if (t.StartsWith("*", StringComparison.Ordinal)) continue;
            kept.Append(line).Append('\n');
        }
        return kept.ToString();
    }

    [Fact]
    public void EVERY_MOUTH_SAYS_WHEN_IT_IS_OPEN()
    {
        // THERE ARE TWO, WHICH IS THE WHOLE REASON THIS TEST NAMES THEM BOTH.
        //
        // The fix was first wired into AndroidAudioPlayer alone, which plays
        // audio and is not what plays the assistant's speech on this device.
        // The phone promptly transcribed itself again while reporting "0
        // skipped while it spoke (0 sentences)" - one wire, to the wrong end,
        // and a suite that was green for it.
        //
        // PlatformAudio is the one DeviceVoiceHost and AckBank actually use.
        var track = Code(Source("src", "CircleAI", "Client", "AndroidAudio.cs"));
        var media = Code(Source("src", "CircleAI", "Assistant.Device", "PlatformAudio.cs"));

        Assert.True(track.Contains("OutLoud.While()", StringComparison.Ordinal),
            "AndroidAudioPlayer must say so while it plays");
        Assert.True(media.Contains("OutLoud.While()", StringComparison.Ordinal),
            "PlatformAudio is what actually speaks on the device - it must say so too");
    }

    [Fact]
    public void AND_THE_WAKE_LOOP_ASKS_BEFORE_IT_LISTENS()
    {
        // The other end. Measured on the P30 on 2026-10-12: without this the
        // wake loop recorded the phone's own text-to-speech and transcribed it
        // perfectly - "I AM USING THE SMALLER BRAIN THE BIG ONE STOPPED THIS
        // PHONE SO I LEFT IT ALONE".
        var wake = Code(Source("src", "CircleAI", "Assistant.Device", "ScreenUpWakeWord.cs"));

        Assert.True(wake.Contains("OutLoud.Now", StringComparison.Ordinal),
            "the wake loop must not decode audio while the assistant is speaking");
    }

    [Fact]
    public void AND_IT_REPORTS_WHAT_IT_SKIPPED_RATHER_THAN_SKIPPING_SILENTLY()
    {
        // A SILENT ZERO IS NOT A QUIET ROOM. "0 skipped while it spoke" means
        // either the assistant never spoke or this is not wired, and those read
        // identically - which is how the defect lasted a day. The sentence
        // count is what tells them apart.
        var wake = Code(Source("src", "CircleAI", "Assistant.Device", "ScreenUpWakeWord.cs"));

        Assert.Contains("skipped while it spoke", wake);
        Assert.True(wake.Contains("OutLoud.Spoken", StringComparison.Ordinal),
            "report what it SAID too, or a zero cannot be told from a gap in the wiring");
    }

    [Fact]
    public void THE_WAKE_LOOP_ASKS_THE_RECOGNISER_TO_FINISH_WHEN_AN_UTTERANCE_ENDS()
    {
        // A transducer holds its last token until something tells it the audio
        // has stopped. The loop fed it for a day and never once asked it to
        // finish, so the final token of every phrase stayed uncommitted - and
        // "Hey B" without its "B" matches nothing.
        //
        // The same shape of failure is already on the record here: the keyword
        // spotter this path replaced reached two of "Hey B"'s three tokens
        // 1,796 times and completed zero.
        var wake = Code(Source("src", "CircleAI", "Assistant.Device", "ScreenUpWakeWord.cs"));

        Assert.True(wake.Contains("gate.Closed", StringComparison.Ordinal),
            "the loop must notice when an utterance ends, not only when one starts");
        Assert.True(wake.Contains("asr.Flush()", StringComparison.Ordinal),
            "and must ask the recogniser to commit its last token there");
    }

    [Fact]
    public void AND_WRITES_DOWN_EVERY_UTTERANCE_RATHER_THAN_A_SNAPSHOT()
    {
        // WHY A SNAPSHOT IS NOT A LOG. This loop used to print, every thirty
        // seconds, whatever the transcript happened to hold at that instant -
        // so a phrase spoken twenty seconds earlier was already overwritten,
        // and this phone's logcat buffer rolls about every four minutes on top
        // of that. Asked "did he say it and we missed it, or did he not say
        // it", the log could not tell the difference, and a line that cannot
        // separate two causes is not a line.
        var wake = Code(Source("src", "CircleAI", "Assistant.Device", "ScreenUpWakeWord.cs"));

        Assert.True(wake.Contains("heard #", StringComparison.Ordinal),
            "every utterance must be reported as it ends");

        // AND THE RATE MUST BE FOR THE WINDOW, NOT FOR EVER. A lifetime average
        // read a steady 35 % across several reports while the app had taken the
        // microphone and the service was being handed silence.
        Assert.True(wake.Contains("last 30 s", StringComparison.Ordinal),
            "the duty cycle must describe the last window, or it cannot show anything going wrong");
    }

    [Fact]
    public void THE_QUESTION_AFTER_A_WAKE_IS_KEPT_WHOLE_BY_THE_CALIBRATED_GATE()
    {
        // FOUND FROM THE ONE WAKE THAT FIRED IN FOUR DAYS ON THE P30:
        //
        //     06:43:50  HEARD "Hey B" in "HAY B"
        //     06:44:15  captured 20480 bytes in 15180 ms
        //     06:44:17  stt: 0,6 s audio ... 0 chars
        //     06:44:17  nothing was said
        //
        // Fifteen seconds of listening kept 0,64 seconds. WakeTurn used
        // EnergyVadDetector at its default 0,02 RMS - a close-talking
        // microphone's number - on a capture path where the owner's voice reads
        // 0,002 to 0,016, and it kept only the frames that cleared that bar, and
        // it ended the question at the first 300 ms gap between two words.
        var turn = Code(Source("src", "CircleAI", "Assistant.Device", "WakeTurn.cs"));

        Assert.False(turn.Contains("new EnergyVadDetector()", StringComparison.Ordinal),
            "the question must not be judged by a fixed 0,02 threshold from a different microphone");
        Assert.True(turn.Contains("SpeechGate", StringComparison.Ordinal),
            "it must use the gate that learns the room and was calibrated on this capture path");

        // AND THE ACKNOWLEDGEMENT'S OWN ECHO IS NOT THE QUESTION. The turn
        // listens straight after the phone has spoken.
        Assert.True(turn.Contains("OutLoud.Now", StringComparison.Ordinal),
            "the turn must not open on the tail of the phone's own acknowledgement");
    }

    [Fact]
    public void SPEECH_RENDERED_FOR_THE_APP_DEAFENS_THE_SERVICE_WHILE_IT_PLAYS()
    {
        // The other process's mouth. The app plays every answer, so its player
        // marks only the APP's OutLoud and the service's wake loop - measured on
        // the P30 on 2026-10-16 - transcribed a whole answer back. The service
        // is the one that rendered it, so that is where the length is known.
        var speech = Code(Source("src", "CircleAI", "Assistant.Device", "ServiceSpeech.cs"));

        Assert.True(speech.Contains("OutLoud.For(", StringComparison.Ordinal),
            "speech handed to the app must keep the service's wake loop deaf for its length");
    }

    [Fact]
    public void THE_APPS_QUESTION_ENDS_WHEN_THE_PERSON_STOPS_NOT_AT_THE_LINK_LIMIT()
    {
        // MEASURED ON THE P30 ON 2026-10-16: "stt: 25,6 s audio | decode=5800
        // ms" for one sentence. The app recorded until LinkAudio.MaxAudioBytes -
        // 800 KB, exactly 25,6 s - every time, with no idea whether anybody was
        // still speaking. VoiceTurn, the SDK's endpointer, was never wired to it.
        var app = Code(Source("samples", "CircleAI.Samples.Hybrid", "CircleAI.Samples.App",
                              "Services", "ServiceConversation.cs"));

        Assert.True(app.Contains("new VoiceTurn().ListenAsync(", StringComparison.Ordinal),
            "the app's question must end on silence after speech, through the SDK's endpointer");
        Assert.False(app.Contains("if (buffer.Count >= LinkAudio.MaxAudioBytes) break;", StringComparison.Ordinal),
            "recording to the link's byte limit is what made every turn 25,6 seconds long");
    }

    [Fact]
    public void THE_APPS_QUESTION_DOES_NOT_END_BEFORE_THE_PERSON_HAS_STARTED()
    {
        // MEASURED ON THE P30 ON 2026-10-16. The turn opened at 17:10:47, one
        // sound peaked at 0,008 and counted as speech, and the person's breath
        // before asking became "silence after speech" - ended at 17:10:49 with
        // 1,7 s kept, Whisper wrote ",", and the brain answered a comma. The
        // question itself, asked from 17:10:51, was heard in full by the wake
        // loop and by nothing that was going to answer it.
        var turn = Code(Source("src", "CircleAI", "Client", "VoiceTurn.cs"));

        Assert.True(turn.Contains("speechSoFar >= MinSpeech", StringComparison.Ordinal),
            "one loud block must not count as somebody having spoken");

        // AND THE DEVICE'S OWN SOUNDS ARE SKIPPED BEFORE THEY ARE KEPT - a skip
        // after the frame is captured would still send the chime to Whisper.
        var skip = turn.IndexOf("if (OutLoud.Now) continue;", StringComparison.Ordinal);
        var keep = turn.IndexOf("captured.AddRange(", StringComparison.Ordinal);
        Assert.True(skip > 0, "the turn must not listen to the app's own sounds");
        Assert.True(keep > 0, "the turn no longer keeps audio - this guard has moved");
        Assert.True(skip < keep, "the app's own sounds must be skipped before they are captured");
    }

    [Fact]
    public void THE_RECOGNISER_IS_RESTARTED_RATHER_THAN_JUST_PAUSED()
    {
        // A transducer holds state across everything it is fed. Skipping the
        // assistant's sentence without resetting leaves the recogniser
        // continuing an utterance from before it - and the gap it cannot see is
        // the whole sentence long. This is the same defect as gating a stream
        // without restarting it, which produced "TABY A B" over six minutes.
        var wake = Code(Source("src", "CircleAI", "Assistant.Device", "ScreenUpWakeWord.cs"));

        var at = wake.IndexOf("OutLoud.Now", StringComparison.Ordinal);
        Assert.True(at > 0);

        var near = wake.Substring(at, Math.Min(400, wake.Length - at));
        Assert.True(near.Contains("asr.Reset()", StringComparison.Ordinal),
            "resume must restart the stream, not continue an utterance across the gap");
    }
}
