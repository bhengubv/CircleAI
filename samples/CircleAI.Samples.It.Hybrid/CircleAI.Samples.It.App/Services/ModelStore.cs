// ModelStore.cs
//
// Where the models live: the app's own private storage.
//
// MODELS ARE APP DATA. Without them the app does nothing - no voice, no ears, no
// answers - so they are not media and they are not optional. That distinction
// decides where they belong.
//
// An earlier version of this file put them in shared storage so they would
// survive an uninstall, on the reasoning that they were "large, expensive, and
// worth the same to whichever app opens them". Both halves of that were wrong:
//
//   They are ESSENTIAL, not optional. Putting the thing the app cannot run
//   without into a world-visible folder means a file manager, a cleaner app, or
//   an owner freeing space can break the app, and the folder looks like 250 MB of
//   junk to anybody who does not know what it is.
//
//   And it ASSUMED shared storage exists. Android.OS.Environment
//   .ExternalStorageDirectory is not a guarantee - plenty of devices have no
//   removable card, and scoped storage can refuse the write on the ones that do.
//   The fallback that hid this was papering over an assumption rather than
//   removing it.
//
// Surviving a reinstall is handled where Android provides for it:
// android:hasFragileUserData="true" in the manifest, which makes the uninstall
// dialog offer to KEEP this app's data. That is the mechanism designed for data
// an app needs and cannot cheaply refetch, and it keeps it private.
//
// This class earns its place anyway: six services used to build this path
// themselves, and a model store spelled slightly differently in one of them is a
// download that lands where nothing looks for it.

namespace CircleAI.Samples.It.App.Services;

/// <summary>The one place the model directory is decided.</summary>
public static class ModelStore
{
    private static string? _resolved;

    /// <summary>Where models are, created if it does not exist yet.</summary>
    public static string Path
    {
        get
        {
            if (_resolved is not null) return _resolved;

            var dir = System.IO.Path.Combine(
                FileSystem.AppDataDirectory, "CircleAI", "Models");
            System.IO.Directory.CreateDirectory(dir);
            return _resolved = dir;
        }
    }
}
