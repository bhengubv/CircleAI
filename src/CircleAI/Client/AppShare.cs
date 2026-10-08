// AppShare.cs
//
// A phone that has Circle AI can hand it to a phone that does not.
//
// THE MESH CANNOT BE THE FIRST HOP, which is the trap this exists to avoid. Circle
// AI is two packages and the brain is the large one; the obvious offline answer is
// "get it from a nearby phone over Aether" - and that needs AetherNetService, which
// is itself another install on a phone that has nothing. Chicken and egg.
//
// SO THE FLOOR IS ANDROID'S OWN SHARE SHEET. Handing a file to Bluetooth needs no
// store, no account, no signal and no other app of ours. Nearby, Quick Share,
// Wi-Fi Direct and the Aether mesh all appear on that same sheet when the phone
// has them, so the mesh is a route it can take rather than a thing it requires.
// This is the standard every Geek app carries: an existing user can hand over the
// installer itself, which is something WhatsApp and Signal cannot do.
//
// BOTH HALVES, OR THE RECEIVER IS STUCK WHERE THE SENDER WAS. Sharing only the
// screens leaves somebody with an app that cannot answer anything; sharing only the
// brain leaves them with a package that has no icon. They travel together.
//
// WHAT IS SHARED IS WHAT IS INSTALLED. The APK comes from the sending phone's own
// ApplicationInfo.SourceDir, so a debug build shares a debug build and a release
// shares a release - there is no second artifact to go stale, and no version skew
// between what one phone is running and what the next one receives.

using Android.Content;
using Android.Content.PM;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>One installed package, ready to hand to somebody.</summary>
/// <param name="Package">Its Android package name.</param>
/// <param name="Label">What to call it when a person sees the file.</param>
/// <param name="Path">A readable copy, in this app's cache.</param>
/// <param name="Bytes">How big it is, so a screen can say so before sending.</param>
public sealed record ShareableApp(string Package, string Label, string Path, long Bytes);

/// <summary>Hands Circle AI's own installers to the next phone.</summary>
public static class AppShare
{
    /// <summary>Where the copies go, under this app's cache.</summary>
    /// <remarks>
    /// CACHE, NOT FILES. These are large - the brain is tens of megabytes - and they
    /// are disposable the moment the share sheet is done with them. Android may
    /// reclaim this directory under pressure, which is exactly the right behaviour
    /// for a copy that can be made again in a second.
    /// </remarks>
    private const string Folder = "share";

    /// <summary>Both halves of Circle AI, as far as this phone has them.</summary>
    /// <param name="context">Any context.</param>
    /// <remarks>
    /// THE SERVICE FIRST, because it is the one the receiver cannot do without and
    /// the one they will be asked to approve the hard way. A share sheet that lists
    /// the screens first invites somebody to send the useless half on its own.
    ///
    /// SILENTLY SHORT RATHER THAN THROWING: a phone missing one half still has
    /// something worth passing on, and a person sharing should not meet an error
    /// about a package they never knew existed.
    /// </remarks>
    public static IReadOnlyList<ShareableApp> Available(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var found = new List<ShareableApp>(2);
        foreach (var (package, label) in new[]
                 {
                     (LinkIpc.HostPackage, "Circle AI brain"),
                     (context.PackageName ?? string.Empty, "Circle AI"),
                 })
        {
            if (string.IsNullOrEmpty(package)) continue;
            var one = Copy(context, package, label);
            if (one is not null) found.Add(one);
        }

        return found;
    }

    /// <summary>Copy one installed package's APK somewhere a share sheet can read it.</summary>
    /// <remarks>
    /// A COPY, BECAUSE THE ORIGINAL IS NOT OURS TO OFFER. base.apk lives under
    /// /data/app in a directory owned by the system; handing out a path into it
    /// would be a path another app cannot open even when the file itself is
    /// readable. Copying it into our own cache makes it something we can legally
    /// grant read access to.
    ///
    /// NAMED FOR A HUMAN AND VERSIONED. The receiver sees this filename in a
    /// notification and again in the installer, and "base.apk" tells them nothing
    /// about what they are about to allow onto their phone.
    /// </remarks>
    public static ShareableApp? Copy(Context context, string package, string label)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(package)) return null;

        try
        {
            var pm = context.PackageManager;
            if (pm is null) return null;

            var info = pm.GetApplicationInfo(package, 0);
            var source = info?.SourceDir;
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) return null;

            var version = Version(pm, package);
            var dir = Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), Folder);
            Directory.CreateDirectory(dir);

            var name = Safe(label) + (version is null ? "" : " " + version) + ".apk";
            var target = Path.Combine(dir, name);

            // COPIED EVERY TIME, NOT CACHED ON LENGTH. An update that keeps the same
            // size would otherwise be shared as the previous build forever, and the
            // whole point is that what travels is what this phone is running.
            File.Copy(source!, target, overwrite: true);

            return new ShareableApp(package, label, target, new FileInfo(target).Length);
        }
        catch (PackageManager.NameNotFoundException) { return null; }
        catch (Exception)
        {
            // No space, no permission, a store that will not copy. Sharing is an
            // offer, never a thing that can fail somebody's actual task.
            return null;
        }
    }

    /// <summary>Delete the copies once the share sheet is finished with them.</summary>
    /// <remarks>
    /// WORTH DOING RATHER THAN LEAVING TO ANDROID. Two APKs is tens of megabytes on
    /// a phone chosen for being cheap, and the cache is only reclaimed under
    /// pressure - which is to say, later than the person would like.
    /// </remarks>
    public static void Clean(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var dir = Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), Folder);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* it is a cache; the OS will get it eventually */ }
    }

    private static string? Version(PackageManager pm, string package)
    {
        try { return pm.GetPackageInfo(package, (PackageInfoFlags)0)?.VersionName; }
        catch { return null; }
    }

    /// <summary>A filename a person can read and a filesystem will accept.</summary>
    private static string Safe(string label)
    {
        var clean = new System.Text.StringBuilder(label.Length);
        foreach (var c in label)
            clean.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : '-');

        return clean.ToString().Trim();
    }
}
