// PlatformAudio.cs
//
// Android playback.
//
// WAS A PARTIAL CLASS IN TWO FILES, and it did not need to be. The split
// exists so a MAUI app can compile one implementation per platform; this
// library targets Android and nothing else, so the declaration half was pure
// ceremony - and it was the half that kept the assistant inside a MAUI
// project, because a partial cannot be completed across an assembly boundary.

using Android.Media;
using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

internal static class PlatformAudio
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
    public static async Task PlayAsync(string wavPath, CancellationToken ct)
    {
        // THIS IS THE DEVICE'S MOUTH, SO THIS IS WHERE IT SAYS SO.
        //
        // MEASURED TWICE ON 2026-10-12. First the wake loop's own microphone
        // capture, pulled off the P30, transcribed as "I AM USING THE SMALLER
        // BRAIN THE BIG ONE STOPPED THIS PHONE SO I LEFT IT ALONE" - the phone
        // reading its own text-to-speech back to itself, which is a self-wake
        // waiting for the day the assistant says its own name.
        //
        // Then the fix was wired into AndroidAudioPlayer, which also plays
        // audio and is NOT what plays this, and the phone transcribed itself
        // again - while the log read "0 skipped while it spoke (0 sentences)".
        // That zero is the only reason the mistake was caught in one round
        // instead of being declared fixed: a boolean would have printed
        // "skipping while speaking" and looked right.
        //
        // DeviceVoiceHost and AckBank both come through here, so here is the
        // one place that covers everything the assistant says.
        using var mouth = OutLoud.While();

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
