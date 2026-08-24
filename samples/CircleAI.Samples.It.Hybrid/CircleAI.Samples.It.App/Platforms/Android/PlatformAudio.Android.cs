// PlatformAudio.Android.cs
//
// Android playback.

using Android.Media;

namespace CircleAI.Samples.It.App.Services;

internal static partial class PlatformAudio
{
    /// <summary>
    /// Play through the MUSIC stream, and wait for completion.
    /// </summary>
    /// <remarks>
    /// THE STREAM CHOICE IS THE BUG THAT ALREADY HAPPENED. Synthesised speech sent
    /// to <c>Stream.VoiceCall</c> comes out of the EARPIECE, not the speaker, and
    /// the symptom is "volume is at maximum and I can hear nothing" - which reads
    /// as a broken synthesiser rather than a routing mistake. Usage.Media with
    /// ContentType.Music is what puts it on the loudspeaker at the volume the
    /// person has already set for everything else.
    /// <para>
    /// Awaited to completion so the caller can return the mark to Idle when the
    /// sound stops rather than when the file was written.
    /// </para>
    /// </remarks>
    public static partial async Task PlayAsync(string wavPath, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        MediaPlayer? player = null;

        try
        {
            player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder()!
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!);

            player.Completion += (_, _) => done.TrySetResult();
            // An error must complete the wait too, or a bad file hangs the caller
            // forever on a task nothing will ever finish.
            player.Error += (_, _) => done.TrySetResult();

            await player.SetDataSourceAsync(wavPath).ConfigureAwait(false);
            player.Prepare();
            player.Start();

            using var cancel = ct.Register(() => done.TrySetResult());
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            try { player?.Stop(); } catch { /* already stopped */ }
            player?.Release();
            player?.Dispose();
        }
    }
}
