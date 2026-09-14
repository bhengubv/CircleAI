// DeviceMusic.cs
//
// IMakesMusic on a phone: the real generator, on a worker, into the cache.
//
// The native sample did exactly this inline in MusicActivity and was the only
// consumer CircleAI.Music ever had. This is that code, moved to where both this
// head and the web head can be honest about whether they can do it.

using CircleAI.Assistant;
using CircleAI.Music;

// Same namespace as every other service in this folder - CareerInterviewHost,
// DeviceSettings, DeviceProfile - so MauiProgram's existing using finds it.
namespace CircleAI.Assistant.Device;

/// <inheritdoc />
public sealed class DeviceMusic : IMakesMusic
{
    /// <inheritdoc />
    public bool Available => true;

    /// <inheritdoc />
    /// <remarks>
    /// EVERY MOOD THE LIBRARY HAS, IN ITS OWN ORDER. Picking a subset here would
    /// be this file having an opinion the library already has, and the day a
    /// mood is added the screen would silently not offer it.
    /// </remarks>
    public IReadOnlyList<MusicMood> Moods { get; } =
        [.. Enum.GetValues<MusicMood>()];

    /// <inheritdoc />
    /// <remarks>
    /// THE SAME ROUTE THE CV TAKES. MAUI's Share resolves the app-private file
    /// through its own FileProvider, which is what makes a path under Cache
    /// reachable by another app at all - handing out the raw path would give
    /// every receiving app a FileUriExposedException.
    /// </remarks>
    public async Task<bool> ShareAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            await Microsoft.Maui.ApplicationModel.DataTransfer.Share.RequestAsync(
                new Microsoft.Maui.ApplicationModel.DataTransfer.ShareFileRequest
                {
                    Title = "Your music",
                    File = new Microsoft.Maui.ApplicationModel.DataTransfer.ShareFile(path),
                }).ConfigureAwait(false);

            return true;
        }
        catch
        {
            // A phone with nothing that takes a WAV still has the file. The sheet
            // failing is not the making failing, and the screen still says where
            // it is.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> MakeAsync(
        MusicMood mood, TimeSpan length, CancellationToken ct = default)
    {
        try
        {
            // PARSED, NOT MAPPED BY HAND. The two enums carry the same names on
            // purpose - see MusicMood - so this stays correct when either gains a
            // member, where a switch would quietly fall through to Neutral.
            if (!Enum.TryParse<Mood>(mood.ToString(), ignoreCase: false, out var real))
                real = Mood.Neutral;

            // OFF THE UI THREAD ON PURPOSE. Synthesis is CPU-bound and
            // synchronous - the generator's own remarks say so - and thirty
            // seconds of audio on a P30 is not instant.
            var bed = await Task.Run(
                () => new ProceduralMusicBedGenerator()
                          .GenerateAsync(MusicSpec.ForMood(real, length), ct),
                ct).ConfigureAwait(false);

            // THE CACHE, NOT APP DATA. A generated bed is regenerable in seconds
            // and nobody is going to miss one - which is the definition Android
            // uses for the directory it may reclaim when the phone needs room.
            var path = Path.Combine(AppPaths.Cache, $"bed-{mood}.wav");
            await File.WriteAllBytesAsync(path, bed.ToWav(), ct).ConfigureAwait(false);

            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A piece of music is never worth taking a screen down for. The page
            // says it could not make one.
            return null;
        }
    }
}
