// CaptureDumpTests.cs
//
// A DIAGNOSTIC THAT WRITES A BROKEN FILE IS WORSE THAN NONE.
//
// This exists because every bench run so far used somebody else's recording.
// The owner's own 55,6 s "Hey B!" fires EIGHT times through the real decoder in
// the device's exact configuration, and zero times on the phone - so the one
// untested thing is the audio our own capture produces. This keeps it.
//
// Which makes the header the risky part: a wav with wrong lengths opens in
// nothing, and on a diagnostic that is indistinguishable from the capture
// having failed - sending us back round the loop this was built to end. So the
// bytes are asserted here rather than discovered on a phone.

using System;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class CaptureDumpTests
{
    private static string Fresh()
    {
        var d = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void It_does_nothing_at_all_without_the_marker()
    {
        // THE DEFAULT, AND IT RECORDS A MICROPHONE, so the default matters more
        // than the feature. No marker, no object, no file, no line.
        var dir = Fresh();
        try
        {
            var said = 0;
            var dump = CaptureDump.IfAsked(dir, "x.wav", _ => said++);

            Assert.Null(dump);
            Assert.Empty(Directory.GetFiles(dir));
            Assert.Equal(0, said);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void It_starts_when_the_marker_is_there_and_says_so()
    {
        var dir = Fresh();
        try
        {
            File.WriteAllText(Path.Combine(dir, CaptureDump.MarkerName), string.Empty);

            string? told = null;
            using var dump = CaptureDump.IfAsked(dir, "x.wav", line => told = line);

            Assert.NotNull(dump);
            Assert.True(dump!.Recording);

            // IT SAYS IT IS RECORDING AND HOW TO STOP IT. A microphone that is
            // being written to disk silently is the wrong kind of quiet.
            Assert.NotNull(told);
            Assert.Contains("capture dump ON", told!, StringComparison.Ordinal);
            Assert.Contains("Delete the marker", told!, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_missing_directory_is_silence_and_not_a_crash()
    {
        // This runs inside the wake loop's startup. A diagnostic that can stop
        // the wake word from starting is worse than no diagnostic.
        Assert.Null(CaptureDump.IfAsked(string.Empty, "x.wav"));
        Assert.Null(CaptureDump.IfAsked(Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid()), "x.wav"));
    }

    [Fact]
    public void What_it_writes_is_a_wav_that_actually_opens()
    {
        var dir = Fresh();
        try
        {
            File.WriteAllText(Path.Combine(dir, CaptureDump.MarkerName), string.Empty);
            var path = Path.Combine(dir, "x.wav");

            var tone = new float[16_000];
            for (var i = 0; i < tone.Length; i++)
                tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 16_000.0));

            using (var dump = CaptureDump.IfAsked(dir, "x.wav")!)
            {
                dump.Keep(tone);
                Assert.Equal(1.0, dump.Seconds, 3);
            }

            var bytes = File.ReadAllBytes(path);

            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));

            var data = BitConverter.ToInt32(bytes, 40);
            Assert.Equal(16_000 * 2, data);
            Assert.Equal(36 + data, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(44 + data, bytes.Length);

            Assert.Equal(1, BitConverter.ToInt16(bytes, 20));         // PCM
            Assert.Equal(1, BitConverter.ToInt16(bytes, 22));         // mono
            Assert.Equal(16_000, BitConverter.ToInt32(bytes, 24));    // rate
            Assert.Equal(16, BitConverter.ToInt16(bytes, 34));        // bits

            // AND THE AUDIO SURVIVED THE ROUND TRIP. A correct header over
            // silence would pass every assertion above and tell us nothing.
            var loudest = 0;
            for (var i = 44; i + 1 < bytes.Length; i += 2)
                loudest = Math.Max(loudest, Math.Abs(BitConverter.ToInt16(bytes, i)));

            Assert.InRange(loudest, 16_000, 16_500);   // 0,5 of full scale
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IT_ROLLS_RATHER_THAN_STOPPING_AND_STAYS_BOUNDED_AT_TWO_WINDOWS()
    {
        // IT USED TO STOP AFTER ONE WINDOW, AND THAT MADE IT USELESS.
        //
        // A dump that keeps the FIRST minute after the service starts records
        // the minute before anybody has been asked to speak. Measured on the
        // P30 on 2026-10-12: armed at 07:17, full at 07:18, sixty seconds of an
        // empty room, microphone shut for the rest of the day - while every
        // question it exists to answer is about something a person does later.
        //
        // So it rolls, and the pair of files always holds the last one to two
        // minutes. Bounded is still the requirement: TWO windows, never a
        // recording that grows until the phone is full.
        var dir = Fresh();
        try
        {
            File.WriteAllText(Path.Combine(dir, CaptureDump.MarkerName), string.Empty);
            using var dump = CaptureDump.IfAsked(dir, "x.wav")!;

            var second = new float[16_000];
            for (var s = 0; s < CaptureDump.MostSeconds * 3 + 5; s++) dump.Keep(second);

            // STILL RECORDING, which is the whole change - a phrase spoken in
            // the third minute has somewhere to land.
            Assert.True(dump.Recording, "it must keep listening past the first window");

            var live = new FileInfo(Path.Combine(dir, "x.wav"));
            var prev = new FileInfo(Path.Combine(dir, "x.wav.prev"));

            Assert.True(prev.Exists, "the window just finished must be kept beside the live one");

            // NEITHER FILE MAY EXCEED ONE WINDOW, and there are only ever two.
            var window = 44 + CaptureDump.MostSeconds * 16_000 * 2;
            Assert.Equal(window, prev.Length);
            Assert.True(live.Length <= window, $"the live window is {live.Length}, over {window}");
            Assert.Equal(2, Directory.GetFiles(dir, "x.wav*").Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Keeping_after_it_has_closed_is_ignored_rather_than_thrown()
    {
        var dir = Fresh();
        try
        {
            File.WriteAllText(Path.Combine(dir, CaptureDump.MarkerName), string.Empty);
            var dump = CaptureDump.IfAsked(dir, "x.wav")!;
            dump.Close();

            dump.Keep(new float[1600]);        // must not throw
            dump.Close();                       // nor must a second close
            Assert.False(dump.Recording);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_header_is_right_for_an_empty_file_too()
    {
        var h = CaptureDump.Header(0);
        Assert.Equal(44, h.Length);
        Assert.Equal(36, BitConverter.ToInt32(h, 4));
        Assert.Equal(0, BitConverter.ToInt32(h, 40));
    }
}
