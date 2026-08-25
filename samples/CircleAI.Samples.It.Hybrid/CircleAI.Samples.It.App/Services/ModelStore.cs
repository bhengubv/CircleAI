// ModelStore.cs
//
// Where the models live, and why it is not the app's private directory.
//
// THE PROBLEM THIS SOLVES: a reinstall was costing about 250 MB and most of an
// hour on a P30. Android deletes FileSystem.AppDataDirectory on uninstall, and it
// deletes getExternalFilesDir with it, so every install started from nothing -
// and with two CircleAI apps on one phone, the same voices were downloaded twice.
//
// Models are not app data. They are more like media: large, expensive, and worth
// exactly the same to whichever app opens them. So they go in shared storage
// under one folder, and both apps find the same copy.
//
// FALLS BACK RATHER THAN FAILS. Shared storage is not writable on every device or
// every Android version, and an app that cannot write its models is worse than
// one that re-downloads them. When the shared path cannot be used, this returns
// the private one and the old behaviour applies.

namespace CircleAI.Samples.It.App.Services;

/// <summary>Resolves the directory models are kept in.</summary>
public static class ModelStore
{
    /// <summary>
    /// One folder, shared by every CircleAI app on the phone.
    /// </summary>
    /// <remarks>
    /// Not under Android/data: that path is app-scoped and removed with the app.
    /// A plain top-level folder is what survives an uninstall and what a person
    /// can see, copy to an SD card, or hand to somebody over a cable - which is
    /// the same thing the one-APK sideload rule needs.
    /// </remarks>
    private const string SharedFolder = "CircleAI";

    private static string? _resolved;

    /// <summary>Where models are, creating it if it can be created.</summary>
    public static string Path
    {
        get
        {
            if (_resolved is not null) return _resolved;
            return _resolved = Resolve();
        }
    }

    /// <summary>True when models are in shared storage and survive a reinstall.</summary>
    public static bool IsShared { get; private set; }

    private static string Resolve()
    {
        var priv = System.IO.Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "Models");

#if ANDROID
        try
        {
            var root = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(root))
            {
                var shared = System.IO.Path.Combine(root!, SharedFolder, "Models");
                System.IO.Directory.CreateDirectory(shared);

                // PROVE IT IS WRITABLE, do not assume. On scoped storage the
                // directory can be created and every write then fails, which
                // turns a download into a silent zero-byte file rather than an
                // error somebody can act on.
                var probe = System.IO.Path.Combine(shared, ".writable");
                System.IO.File.WriteAllText(probe, "1");
                System.IO.File.Delete(probe);

                IsShared = true;
                return shared;
            }
        }
        catch
        {
            // Not writable here. The private directory still works; it just does
            // not survive an uninstall.
        }
#endif

        IsShared = false;
        System.IO.Directory.CreateDirectory(priv);
        return priv;
    }
}
