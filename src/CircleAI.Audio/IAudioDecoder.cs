// IAudioDecoder.cs
//
// Everything that is not a WAV.
//
// The voice stack reads RIFF/WAVE in managed code and always has, because that
// is what a microphone hands back and what a fixture is written as. What people
// actually have on their phones is none of those: a voice memo is .m4a, a
// podcast is .mp3, a video's audio is inside an .mp4. Decoding those means a
// codec, and a codec means either a platform decoder or several megabytes of
// native library.
//
// So it is a seam. Android has MediaCodec/MediaExtractor sitting in the OS and
// can implement this in a few dozen lines; a desktop tool can shell out to
// ffmpeg; a build with neither says so in a sentence instead of throwing
// "not a RIFF/WAVE file" at somebody who handed over a perfectly good recording.
//
// THE ALTERNATIVE WAS WORSE AND IT WAS THE STATUS QUO: the only way to
// transcribe anything was to already hold PCM, so every caller that had a file
// either wrote its own reader or did not exist. tools/stt-hear wrote its own.

namespace CircleAI.Voice;

/// <summary>
/// Turns a compressed audio file into the mono float samples the voice stack
/// works in. Implemented per platform; WAV needs no implementation.
/// </summary>
public interface IAudioDecoder
{
    /// <summary>Whether this decoder believes it can read the file.</summary>
    /// <remarks>
    /// Asked BEFORE <see cref="DecodeAsync"/> so a caller can tell "no decoder
    /// for .m4a on this build" from "the .m4a is corrupt", which are a missing
    /// feature and a broken file and want different sentences.
    /// </remarks>
    bool CanRead(string path);

    /// <summary>
    /// Decode to mono float samples in [-1,1] at <paramref name="targetRate"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not readable audio.</exception>
    Task<float[]> DecodeAsync(string path, int targetRate, CancellationToken ct = default);
}

/// <summary>A decoder that reads nothing, so WAV is all this build supports.</summary>
/// <remarks>
/// The honest default. <see cref="CanRead"/> says NO rather than yes-then-fail:
/// a false yes turns a missing codec into a decode error further down, and the
/// message a person reads is then about their file rather than about the build.
/// </remarks>
public sealed class NoAudioDecoder : IAudioDecoder
{
    /// <summary>The shared instance.</summary>
    public static readonly NoAudioDecoder Instance = new();

    /// <inheritdoc />
    public bool CanRead(string path) => false;

    /// <inheritdoc />
    public Task<float[]> DecodeAsync(string path, int targetRate, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"This build reads WAV only, and '{Path.GetExtension(path)}' is not WAV. " +
            "Supply an IAudioDecoder (Android: MediaExtractor + MediaCodec) to read it.");
}
