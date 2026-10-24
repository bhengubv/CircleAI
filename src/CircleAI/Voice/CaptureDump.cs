// CaptureDump.cs
//
// WRITES DOWN WHAT THE WAKE LOOP ACTUALLY HEARD.
//
// EVERY BENCH RUN HAS USED SOMEBODY ELSE'S RECORDING. On 2026-10-12 a 55,6 s
// recording of the owner saying "Hey B!" - made on the P30 itself, at normal
// distance - was put through the real decoder in the device's exact
// configuration: int8 encoder, two-stage confirmer, the shipped keyword. It
// fired EIGHT times. Scaled down to the level the wake loop was reporting while
// it failed, it fired eight times again. On the phone, the same phrase fires
// zero.
//
// Everything that could be varied on the bench has been varied, and all of it
// works. The one thing never tested is the audio OUR OWN capture produces: that
// recording came from the phone's recorder app, and the wake loop opens the
// microphone differently - AudioSource.VoiceRecognition, AGC and echo
// cancellation on, and no noise suppressor, which its own log states. So the
// remaining difference is the capture, and the only way to stop guessing about
// it is to keep what it delivered and listen to it.
//
// OFF UNLESS SOMEBODY ASKS, AND ASKING IS DELIBERATE. It records a microphone,
// so it does not run on a switch somebody can trip by accident and it does not
// ship enabled. The host enables it by the presence of a marker file - a thing
// a person or an adb command creates on purpose - and it writes to a directory
// the owner can see and delete. Nothing is uploaded, by this type or any other.
//
// BOUNDED, so a diagnostic cannot fill a phone: it stops at a stated number of
// seconds and says so.

using System;
using System.IO;

namespace CircleAI.Voice;

/// <summary>Keeps what the microphone delivered, as a wav, when asked to.</summary>
public sealed class CaptureDump : IDisposable
{
    /// <summary>The file whose EXISTENCE turns this on.</summary>
    /// <remarks>
    /// A marker rather than a setting, because it has to be switchable on a
    /// release build with no debugger and no UI - `adb shell touch`, or a person
    /// creating a file - and because a thing that records a microphone should
    /// take a deliberate act to start and leave visible evidence that it did.
    /// </remarks>
    public const string MarkerName = "capture-dump.on";

    /// <summary>Longest recording this will make before stopping itself.</summary>
    /// <remarks>
    /// Sixty seconds is a dozen attempts at a wake phrase with pauses, which is
    /// what the question needs, and about 1,9 MB at 16 kHz mono 16-bit. A
    /// diagnostic that can fill a phone is a fault of its own.
    /// </remarks>
    public const int MostSeconds = 60;

    private const int Rate = 16_000;

    // NOT readonly: Rotate replaces it when a window fills. See Rotate.
    private FileStream? _file;
    private int _samples;
    private bool _closed;

    /// <summary>Where it is writing, or null when it is not.</summary>
    public string? Path { get; }

    /// <summary>True while it is still accepting audio.</summary>
    public bool Recording => _file is not null && !_closed;

    /// <summary>How many seconds have been kept.</summary>
    public double Seconds => _samples / (double)Rate;

    /// <summary>
    /// Starts a dump if the marker is present in <paramref name="directory"/>.
    /// </summary>
    /// <param name="directory">Where the marker lives and the wav is written.</param>
    /// <param name="name">File name for the wav.</param>
    /// <param name="say">Where to report what it did; never silent either way.</param>
    /// <remarks>
    /// NEVER THROWS. A diagnostic that can stop the wake word from starting is
    /// worse than no diagnostic - this is the thing that is supposed to help
    /// when everything else has failed.
    /// </remarks>
    /// <summary>Whether the marker that turns speech diagnostics on is present.</summary>
    /// <remarks>
    /// THE MARKER NOW GATES MORE THAN THE WAV, so the question is asked once, here.
    /// Two other diagnostics write down what was SAID rather than what was heard -
    /// UtteranceLog's heard.txt, and the verbatim lines the transcribing wake loop
    /// prints to logcat - and both were on for anybody who happened to have the
    /// streaming recogniser on their phone. They answer the same question as the wav
    /// and they belong behind the same deliberate act.
    /// <para>
    /// Never throws, and a directory that cannot be read is a no.
    /// </para>
    /// </remarks>
    public static bool Asked(string? directory)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(directory)
                   && File.Exists(System.IO.Path.Combine(directory, MarkerName));
        }
        catch { return false; }
    }

    public static CaptureDump? IfAsked(string directory, string name, Action<string>? say = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) return null;

            var marker = System.IO.Path.Combine(directory, MarkerName);
            if (!File.Exists(marker)) return null;

            var dump = new CaptureDump(System.IO.Path.Combine(directory, name));
            // SAYS WHAT IT NOW DOES. This line still read "stops after 60s"
            // after the dump was changed to roll, which would have had the next
            // person reading the log believe a recording had ended an hour
            // before they looked for it.
            say?.Invoke($"capture dump ON (marker present) -> {dump.Path}; "
                      + $"rolls every {MostSeconds}s, keeping the finished window as .prev. "
                      + "Delete the marker to stop recording.");
            return dump;
        }
        catch (Exception ex)
        {
            say?.Invoke("capture dump could not start: " + ex.Message);
            return null;
        }
    }

    private CaptureDump(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

        // A 44-byte RIFF header with the lengths left at zero, rewritten on
        // close. Streaming straight to disk rather than buffering a minute of
        // audio in a process the OS is already trying to kill for memory.
        _file.Write(new byte[44], 0, 44);
    }

    /// <summary>Keeps one block, exactly as the spotter was given it.</summary>
    /// <remarks>
    /// CALLED WITH THE SAME SPAN THE SPOTTER GETS, and that is the whole point:
    /// a dump taken anywhere else would answer a question nobody asked.
    /// </remarks>
    public void Keep(ReadOnlySpan<float> pcm)
    {
        if (_file is null || _closed) return;

        try
        {
            Span<byte> two = stackalloc byte[2];
            foreach (var s in pcm)
            {
                var v = (int)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f);
                two[0] = (byte)(v & 0xFF);
                two[1] = (byte)((v >> 8) & 0xFF);
                _file.Write(two);
                _samples++;
            }

            if (_samples >= MostSeconds * Rate) Rotate();
        }
        catch
        {
            // A disk that filled or a file that vanished ends the dump, never
            // the wake loop.
            _closed = true;
        }
    }

    /// <summary>Starts a new window, keeping the one just finished beside it.</summary>
    /// <remarks>
    /// IT USED TO JUST STOP, AND THAT MADE IT USELESS FOR ITS ONE JOB.
    /// <para>
    /// A dump that keeps the FIRST minute after the service starts records the
    /// minute before anybody has been asked to speak. Measured on 2026-10-12:
    /// armed at 07:17, full at 07:18, sixty seconds of an empty room, and the
    /// microphone shut off for the rest of the day. Every question this file
    /// exists to answer is about something a person does LATER.
    /// </para>
    /// <para>
    /// So it rolls. The finished window moves aside as <c>.prev</c> and a new
    /// one starts, which means the two files together always hold the last one
    /// to two minutes however long the loop has been running - and a phrase
    /// spoken at an unpredictable moment is in one of them. Two files rather
    /// than one because the live file is mid-write when it is copied off.
    /// </para>
    /// <para>
    /// Still bounded: two windows, not a recording that grows until the disk is
    /// full. The marker file is what the owner deletes to stop it.
    /// </para>
    /// </remarks>
    private void Rotate()
    {
        Close();

        try
        {
            var previous = Path + ".prev";
            File.Move(Path, previous, overwrite: true);

            _file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _file.Write(new byte[44], 0, 44);
            _samples = 0;
            _closed = false;
        }
        catch
        {
            // Rotation failing leaves the finished window on disk, which is
            // still a usable capture. It never stops the wake loop.
            _closed = true;
        }
    }

    /// <summary>Finishes the file and writes the real lengths into its header.</summary>
    public void Close()
    {
        if (_file is null || _closed) return;
        _closed = true;

        try
        {
            var data = _samples * 2;
            _file.Seek(0, SeekOrigin.Begin);
            _file.Write(Header(data));
            _file.Flush();
        }
        catch { /* the audio is on disk either way */ }
        finally { _file.Dispose(); }
    }

    /// <summary>A 44-byte RIFF/WAVE header for 16 kHz mono 16-bit.</summary>
    /// <remarks>
    /// Public and pure so the byte layout can be asserted without a microphone -
    /// a header written wrong produces a file every tool refuses to open, which
    /// on a diagnostic is indistinguishable from the capture having failed.
    /// </remarks>
    public static byte[] Header(int dataBytes)
    {
        var h = new byte[44];
        void Ascii(int at, string s) { for (var i = 0; i < s.Length; i++) h[at + i] = (byte)s[i]; }
        void I32(int at, int v) { BitConverter.TryWriteBytes(h.AsSpan(at, 4), v); }
        void I16(int at, short v) { BitConverter.TryWriteBytes(h.AsSpan(at, 2), v); }

        Ascii(0, "RIFF");
        I32(4, 36 + dataBytes);
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        I32(16, 16);            // PCM chunk size
        I16(20, 1);             // PCM
        I16(22, 1);             // mono
        I32(24, Rate);
        I32(28, Rate * 2);      // byte rate
        I16(32, 2);             // block align
        I16(34, 16);            // bits
        Ascii(36, "data");
        I32(40, dataBytes);
        return h;
    }

    /// <inheritdoc/>
    public void Dispose() => Close();
}
