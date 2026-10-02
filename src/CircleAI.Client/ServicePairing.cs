// ServicePairing.cs
//
// Circle AI is two apps, and only one of them has an icon.
//
// THE THIN APP CANNOT THINK. After the split the app holds no model at all - every
// question travels app -> link -> CircleAIService, which is where the brain, the
// models, the voices and the skill library live. Installed on its own, the app is a
// set of screens that cannot answer anything, and nothing on it says why. The link
// simply reports that Circle AI is not installed, which is confusing wording for
// somebody looking straight at the Circle AI they just installed.
//
// SO THE PAIR HAS TO EXPLAIN ITSELF. This is the product half of that: whether the
// other half is here, whether this phone will even allow it to be installed, and
// the hand-off to Android's own installer. The app renders it; none of the
// reasoning lives in a screen.
//
// WHERE THE PACKAGE COMES FROM IS NOT DECIDED HERE, deliberately. A store listing,
// a signed download, and a copy from a phone already carrying it over the mesh are
// three different products with three different answers about what happens with no
// signal and no account - and that is a decision, not a detail. Source is a seam;
// everything around it is the same whichever way it is answered.
//
// ANDROID ASKS AGAIN, WHATEVER WE DO. Installing a package needs
// REQUEST_INSTALL_PACKAGES, and even holding it the system shows its own
// confirmation with the app's name and permissions on it. That is the right
// behaviour and this does not try to get around it: the prompt here is about
// CONSENT TO FETCH, and the system's is about consent to install.

using Android.Content;
using Android.Content.PM;
using Android.OS;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>What is missing, if anything, before this phone can think.</summary>
/// <param name="ServiceInstalled">Is the other half of Circle AI here?</param>
/// <param name="MayInstall">
/// Will this phone let this app install a package? Always true below Android 8,
/// where the permission did not exist.
/// </param>
public readonly record struct PairingState(bool ServiceInstalled, bool MayInstall)
{
    /// <summary>Nothing to do - the pair is complete.</summary>
    public bool IsPaired => ServiceInstalled;
}

/// <summary>The missing half of Circle AI, and how to go and get it.</summary>
public static class ServicePairing
{
    /// <summary>What the person is being asked to add, in their words.</summary>
    /// <remarks>
    /// NOT "THE SERVICE", WHICH MEANS NOTHING TO ANYBODY. It is the part that does
    /// the thinking, it is large because the models are in it, and it has no icon -
    /// all three are things somebody would reasonably want to know before agreeing
    /// to a download, and all three are true.
    /// </remarks>
    public const string Name = "Circle AI's brain";

    /// <summary>Why it is a separate download, in one sentence somebody can act on.</summary>
    public const string Why =
        "Circle AI comes in two parts. This one is the screens; the other is the brain " +
        "that answers you, and it is a large download because the models live in it. " +
        "It runs on the phone with no signal and no account, and it has no icon of its own.";

    /// <summary>Is the other half here, and will this phone let us fetch it?</summary>
    public static PairingState Check(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new PairingState(
            ServiceInstalled: CircleAiLinkClient.IsInstalled(context),
            MayInstall:       MayInstallPackages(context));
    }

    /// <summary>
    /// Whether this app holds the permission Android needs to install a package.
    /// </summary>
    /// <remarks>
    /// IT IS NOT A RUNTIME PERMISSION AND THERE IS NO DIALOG FOR IT. From Android 8
    /// it is a per-app setting on a Settings screen the person has to visit, which
    /// is why this is a question with its own answer rather than something that can
    /// be requested inline. Below 8 there was no such gate.
    /// </remarks>
    public static bool MayInstallPackages(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return true;

        try { return context.PackageManager?.CanRequestPackageInstalls() ?? false; }
        catch { return false; }
    }

    /// <summary>The Settings screen where that permission is granted.</summary>
    /// <remarks>
    /// PER-APP, NOT THE GLOBAL LIST. The targeted action drops somebody on this
    /// app's own switch; without the package URI they land on a list of every app
    /// on the phone and have to find us, which is how an instruction that is
    /// technically correct still fails.
    /// </remarks>
    public static Intent? InstallPermissionSettings(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return null;

        var intent = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources);
        intent.SetData(Android.Net.Uri.Parse("package:" + context.PackageName));
        intent.AddFlags(ActivityFlags.NewTask);
        return intent;
    }

    /// <summary>Hand a downloaded package to Android's installer.</summary>
    /// <param name="context">Any context; the activity flag is set either way.</param>
    /// <param name="apk">A content:// URI from this app's FileProvider.</param>
    /// <remarks>
    /// A CONTENT URI, NOT A FILE PATH. Since Android 7 a file:// URI handed to
    /// another app throws FileUriExposedException, and the installer is another
    /// app. The caller exports the download through its own FileProvider and passes
    /// what that returns.
    ///
    /// THE SYSTEM CONFIRMS, NOT US. This opens Android's installer, which shows the
    /// package's real name, signature and permissions. Whatever a screen in this app
    /// said a moment ago, that is the screen somebody actually agrees on.
    /// </remarks>
    public static Intent InstallIntent(Context context, Android.Net.Uri apk)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(apk);

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(apk, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
        return intent;
    }

    /// <summary>The package this app is looking for.</summary>
    /// <remarks>One owner: the same constant the link binds to, never a second copy.</remarks>
    public static string ServicePackage => LinkIpc.HostPackage;
}
