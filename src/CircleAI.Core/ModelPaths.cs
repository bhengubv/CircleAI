// ModelPaths.cs
//
// The one place the model directory is decided.
//
// IT WAS DECIDED IN FOUR PLACES AND THEY DISAGREED ON A PHONE. Three loaders in
// this library defaulted to SpecialFolder.ApplicationData, and the MAUI head
// used FileSystem.AppDataDirectory. On a desktop those are the same folder and
// nothing was ever wrong. On Android they are not:
//
//     FileSystem.AppDataDirectory          ->  /data/user/0/<pkg>/files
//     SpecialFolder.ApplicationData        ->  /data/user/0/<pkg>/files/.config
//
// The second is a SUBDIRECTORY of the first, which is why nothing failed and
// nothing was noticed. Both paths existed, both were writable, both looked
// right in a log. What happened instead is that a 523 MB chat model was
// downloaded twice onto a phone with 890 MB of app data - one copy where the
// app looks for it, one copy where a caller that forgot to pass a path put it.
//
// FOUND BY LOOKING AT THE DISK, not by anything failing. That is the shape of
// this bug: two owners of one fact, agreeing everywhere it is cheap to check
// and disagreeing on the device the product is for.

using System;
using System.IO;

namespace CircleAI.Core;

/// <summary>Where models live on this device.</summary>
public static class ModelPaths
{
    /// <summary>
    /// The model directory, unless a caller names one.
    /// </summary>
    /// <remarks>
    /// PERSONAL, NOT APPLICATIONDATA, and the difference only shows on a phone.
    /// On .NET for Android SpecialFolder.Personal is the app's files directory -
    /// the same one MAUI calls AppDataDirectory - while ApplicationData is
    /// ".config" underneath it. Everywhere else the two agree, so this changes
    /// nothing on desktop or server and stops a phone keeping two copies of
    /// every model.
    /// </remarks>
    public static string Default => Path.Combine(Root, "CircleAI", "Models");

    /// <summary>The app's own storage, wherever this is running.</summary>
    public static string Root =>
        Environment.GetFolderPath(
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
                ? Environment.SpecialFolder.Personal
                : Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// The directory a caller asked for, or the default, created.
    /// </summary>
    /// <remarks>
    /// Every loader takes an optional directory and every one of them had its
    /// own answer for what null meant. This is that answer, once.
    /// </remarks>
    public static string Resolve(string? requested)
    {
        var dir = string.IsNullOrWhiteSpace(requested) ? Default : requested!;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
