// AndroidAudioDecoder.cs
//
// The .m4a, .mp3 and .ogg a phone is actually full of.
//
// CircleAI.Voice reads RIFF/WAVE in managed code because that is what a
// microphone hands back and what a fixture is written as. It is not what anybody
// HAS. A voice memo on Android is .m4a, a podcast is .mp3, the audio of a video
// is inside an .mp4 - and every one of those needs a codec.
//
// THE CODEC IS ALREADY ON THE PHONE. MediaExtractor and MediaCodec are platform
// classes backed by the same hardware decoders the music player uses, so this
// costs no library, no licence and no megabytes - and it decodes formats a
// bundled decoder would not, because the phone shipped with them.
//
// IAudioDecoder EXISTS SO THIS FILE CAN BE ANDROID-ONLY. CircleAI.Voice ships to
// every platform CircleAI targets and must not contain Android types; a build
// with no decoder wired says so in a sentence instead of throwing a parse error
// about RIFF headers at somebody holding a perfectly good recording.

using Android.Media;
using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

/// <summary>Decodes compressed audio with the phone's own codecs.</summary>
public sealed class AndroidAudioDecoder : IAudioDecoder
{
    /// <summary>The shared instance. Holds no state between calls.</summary>
    public static readonly AndroidAudioDecoder Instance = new();

    /// <summary>How long to wait on a codec buffer, in microseconds.</summary>
    /// <remarks>
    /// Not zero and not infinite. Zero spins the CPU on a loop that is mostly
    /// waiting; infinite hands the process to a codec that has decided to stop,
    /// with no cancellation and no way back. Ten milliseconds costs nothing and
    /// keeps the loop answerable.
    /// </remarks>
    private const int TimeoutUs = 10_000;

    /// <inheritdoc />
    /// <remarks>
    /// WAV IS DELIBERATELY NOT CLAIMED. WavIo reads it in managed code, on every
    /// platform, without starting a codec - and MediaExtractor on some devices
    /// refuses a WAV that WavIo reads perfectly well. Saying no here means the
    /// managed path stays the one that runs.
    /// </remarks>
    public bool CanRead(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (WavIo.IsWave(path)) return false;

        // ASKED OF THE PHONE, NOT OF A LIST OF EXTENSIONS. Which codecs exist
        // varies by device and by Android version, and a hardcoded list is
        // wrong in both directions - it refuses a format this handset can play
        // and accepts one it cannot.
        MediaExtractor? extractor = null;
        try
        {
            extractor = new MediaExtractor();
            extractor.SetDataSource(path);
            return AudioTrack(extractor) >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { extractor?.Release(); } catch { /* nothing left to do */ }
        }
    }

    /// <inheritdoc />
    public Task<float[]> DecodeAsync(string path, int targetRate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);

        // Task.Run because MediaCodec's loop is blocking start to finish, and
        // every caller of this is a screen.
        return Task.Run(() => Decode(path, targetRate, ct), ct);
    }

    private static float[] Decode(string path, int targetRate, CancellationToken ct)
    {
        MediaExtractor? extractor = null;
        MediaCodec? codec = null;

        try
        {
            extractor = new MediaExtractor();
            extractor.SetDataSource(path);

            var track = AudioTrack(extractor);
            if (track < 0)
                throw new InvalidDataException($"'{Path.GetFileName(path)}' has no audio track.");

            extractor.SelectTrack(track);
            var format = extractor.GetTrackFormat(track);
            var mime = format.GetString(MediaFormat.KeyMime)
                       ?? throw new InvalidDataException("The audio track declares no MIME type.");

            codec = MediaCodec.CreateDecoderByType(mime)
                    ?? throw new InvalidDataException($"This phone has no decoder for {mime}.");
            codec.Configure(format, surface: null, crypto: null, flags: MediaCodecConfigFlags.None);
            codec.Start();

            // READ FROM THE OUTPUT FORMAT, NOT THE INPUT ONE. A decoder is
            // entitled to report the real sample rate and channel count only
            // once it has produced a buffer - and for some AAC streams the input
            // format's numbers are the container's guess. Reading them too early
            // is how a 44,1 kHz stereo file gets resampled as if it were 48 kHz
            // mono, which sounds like a slightly wrong-speed recording and
            // transcribes as nonsense.
            var rate = format.ContainsKey(MediaFormat.KeySampleRate)
                ? format.GetInteger(MediaFormat.KeySampleRate) : targetRate;
            var channels = format.ContainsKey(MediaFormat.KeyChannelCount)
                ? format.GetInteger(MediaFormat.KeyChannelCount) : 1;

            var pcm = new List<short>(capacity: 1 << 20);
            var info = new MediaCodec.BufferInfo();
            var fedEverything = false;
            var drained = false;

            while (!drained)
            {
                ct.ThrowIfCancellationRequested();

                if (!fedEverything)
                {
                    var inIndex = codec.DequeueInputBuffer(TimeoutUs);
                    if (inIndex >= 0)
                    {
                        var input = codec.GetInputBuffer(inIndex);
                        var read = input is null ? -1 : extractor.ReadSampleData(input, 0);

                        if (read < 0)
                        {
                            codec.QueueInputBuffer(inIndex, 0, 0, 0,
                                MediaCodecBufferFlags.EndOfStream);
                            fedEverything = true;
                        }
                        else
                        {
                            codec.QueueInputBuffer(inIndex, 0, read,
                                extractor.SampleTime, MediaCodecBufferFlags.None);
                            extractor.Advance();
                        }
                    }
                }

                var outIndex = codec.DequeueOutputBuffer(info, TimeoutUs);
                if (outIndex >= 0)
                {
                    var output = codec.GetOutputBuffer(outIndex);
                    if (output is not null && info.Size > 0)
                    {
                        // 16-bit little-endian, which is what a decoder produces
                        // unless asked for something else, and nothing here asks.
                        var bytes = new byte[info.Size];
                        output.Position(info.Offset);
                        output.Get(bytes, 0, info.Size);

                        for (var i = 0; i + 1 < bytes.Length; i += 2)
                            pcm.Add((short)(bytes[i] | (bytes[i + 1] << 8)));
                    }

                    codec.ReleaseOutputBuffer(outIndex, render: false);

                    if (info.Flags.HasFlag(MediaCodecBufferFlags.EndOfStream)) drained = true;
                }
                else if (outIndex == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    var actual = codec.OutputFormat;
                    if (actual is not null)
                    {
                        if (actual.ContainsKey(MediaFormat.KeySampleRate))
                            rate = actual.GetInteger(MediaFormat.KeySampleRate);
                        if (actual.ContainsKey(MediaFormat.KeyChannelCount))
                            channels = actual.GetInteger(MediaFormat.KeyChannelCount);
                    }
                }
            }

            return Shape(pcm, rate, Math.Max(1, channels), targetRate);
        }
        finally
        {
            try { codec?.Stop(); }   catch { /* already gone */ }
            try { codec?.Release(); } catch { /* already gone */ }
            try { extractor?.Release(); } catch { /* already gone */ }
        }
    }

    /// <summary>Interleaved shorts to mono float at the rate the caller wants.</summary>
    private static float[] Shape(List<short> interleaved, int rate, int channels, int targetRate)
    {
        if (interleaved.Count == 0) return [];

        var frames = interleaved.Count / channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels / 32768f;
        }

        if (rate == targetRate) return mono;

        // Linear, like WavIo's. The target is a speech recogniser working at
        // 16 kHz on audio that was at least 16 kHz to begin with, so this is a
        // downsample and the artefacts sit above what whisper listens to.
        var count = (int)Math.Round((double)mono.Length * targetRate / rate);
        var output = new float[Math.Max(count, 1)];
        var step = (double)(mono.Length - 1) / Math.Max(output.Length - 1, 1);

        for (var i = 0; i < output.Length; i++)
        {
            var x = i * step;
            var lo = (int)x;
            var hi = Math.Min(lo + 1, mono.Length - 1);
            output[i] = (float)(mono[lo] + (mono[hi] - mono[lo]) * (x - lo));
        }

        return output;
    }

    /// <summary>The index of the first audio track, or -1.</summary>
    private static int AudioTrack(MediaExtractor extractor)
    {
        for (var i = 0; i < extractor.TrackCount; i++)
        {
            var mime = extractor.GetTrackFormat(i).GetString(MediaFormat.KeyMime);
            if (mime is not null && mime.StartsWith("audio/", StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
