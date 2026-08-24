// PlatformAudio.cs
//
// Play a wav and wait for it to finish. One partial per platform.

namespace CircleAI.Samples.It.App.Services;

/// <summary>Plays synthesised audio on whichever platform this head is built for.</summary>
internal static partial class PlatformAudio
{
    /// <summary>Play the file through the device speaker, completing when it ends.</summary>
    public static partial Task PlayAsync(string wavPath, CancellationToken ct);
}
