// AppPaths.cs
//
// The two directories this app keeps things in.
//
// WHY THIS TINY FILE EXISTS. Twelve call sites across eleven classes reached for
// MAUI's FileSystem helper to answer one of two questions - where does app data
// go, and where does scratch go. That is all they wanted, and on Android MAUI's
// answer IS the answer below: FileSystem.AppDataDirectory returns
// Context.FilesDir, FileSystem.CacheDirectory returns Context.CacheDir.
//
// So those twelve calls were not using MAUI for anything. They were just the
// reason the turn loop, the model store, the voice host and the wake word could
// only be compiled inside a MAUI application - which is why none of it could be
// referenced by the repo's own native head, and why none of it could be handed
// to a developer as a library.
//
// A shared constant would have done. It is a class because the directories are
// resolved from a Context that does not exist until the app is running.

namespace CircleAI.Assistant.Device;

/// <summary>Where this app keeps what it downloads and what it throws away.</summary>
public static class AppPaths
{
    /// <summary>Private app storage. Survives restarts; removed when the app is uninstalled.</summary>
    /// <remarks>
    /// The fallback is not expected to be reached - an Android app always has a
    /// files directory - but returning null here would turn a missing directory
    /// into a NullReferenceException several frames away from the cause.
    /// </remarks>
    public static string Data =>
        global::Android.App.Application.Context.FilesDir?.AbsolutePath
        ?? System.IO.Path.GetTempPath();

    /// <summary>Scratch. Android may delete this when the phone needs room.</summary>
    public static string Cache =>
        global::Android.App.Application.Context.CacheDir?.AbsolutePath
        ?? System.IO.Path.GetTempPath();
}
