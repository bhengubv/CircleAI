#if IT_VOICE_ANDROID
#nullable enable

// AndroidAudio.cs
//
// The microphone and the speaker. VoiceLoop needs a real IAudioCapture and
// IAudioPlayer; only the Null implementations existed, so the hands-free loop
// had no ears and no mouth on a phone.
//
// AudioRecord  -> PCM16 16 kHz mono (exactly AudioFormat.Pcm16Mono16k, which is
//                 what IVoiceTranscriber and the wake detector expect).
// AudioTrack   -> plays the PCM the TTS engine produces, at ITS sample rate
//                 (Piper is 22050, not 16000 — never assume they match).
//
// Needs RECORD_AUDIO permission at runtime; MainActivity requests it.

using System.Runtime.CompilerServices;
using Android.Media;
using CircleAI.Voice;

// BOTH namespaces declare an AudioFormat, and this file genuinely needs both:
// Android.Media.AudioFormat (the AudioTrack builder) and CircleAI.Voice.AudioFormat
// (the IAudioCapture.Format contract). Unqualified it is CS0104 — alias the
// CircleAI one so the Android SDK type keeps its familiar bare name.
using VoiceFormat = CircleAI.Voice.AudioFormat;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Microphone capture as PCM16 16 kHz mono.</summary>
public sealed class AndroidAudioCapture : IAudioCapture
{
    private const int SampleRate = 16_000;
    private AudioRecord? _record;

    public VoiceFormat Format { get; } = VoiceFormat.Pcm16Mono16k;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var min = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        if (min <= 0) min = SampleRate; // conservative fallback
        var bufferSize = min * 2;

        _record = new AudioRecord(
            AudioSource.Mic, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, bufferSize);

        if (_record.State != State.Initialized)
        {
            _record.Release();
            _record = null;
            yield break; // no mic / permission denied — stay silent rather than crash
        }

        _record.StartRecording();
        var buffer = new byte[3200]; // 100 ms at 16 kHz mono 16-bit

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _record.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0) { await Task.Delay(10, ct).ConfigureAwait(false); continue; }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                yield return chunk;
            }
        }
        finally
        {
            try { _record?.Stop(); } catch { }
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _record?.Stop(); } catch { }
        _record?.Release();
        _record?.Dispose();
        _record = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Speaker playback via AudioTrack, honouring the engine's sample rate.</summary>
public sealed class AndroidAudioPlayer : IAudioPlayer
{
    public async Task PlayAsync(
        ReadOnlyMemory<byte> pcm, int sampleRate, int channels, int bitsPerSample,
        CancellationToken ct = default)
    {
        if (pcm.Length == 0) return;

        var channelOut = channels >= 2 ? ChannelOut.Stereo : ChannelOut.Mono;
        var min = AudioTrack.GetMinBufferSize(sampleRate, channelOut, Encoding.Pcm16bit);
        if (min <= 0) min = pcm.Length;

        using var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Assistant)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!)!
            .SetAudioFormat(new Android.Media.AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(sampleRate)!
                .SetChannelMask(channelOut)!
                .Build()!)!
            .SetBufferSizeInBytes(Math.Max(min, pcm.Length))!
            .SetTransferMode(AudioTrackMode.Static)!
            .Build();

        var bytes = pcm.ToArray();
        track.Write(bytes, 0, bytes.Length);
        track.Play();

        // Static mode plays the whole buffer; wait it out so the loop does not
        // start listening again while the assistant is still talking (which is
        // how a device hears its own voice and answers itself).
        var ms = (int)(pcm.Length / (double)(sampleRate * channels * bitsPerSample / 8) * 1000);
        try { await Task.Delay(ms + 150, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        try { track.Stop(); } catch { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif
