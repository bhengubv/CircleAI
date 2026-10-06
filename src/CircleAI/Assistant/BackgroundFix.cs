// BackgroundFix.cs
//
// Where the setting that actually stops this phone killing us lives.
//
// "ALLOW IT TO RUN IN THE BACKGROUND" IS NOT A SETTING ON THESE PHONES. The warning
// sent people to Android's own battery-optimisation list, which on a Huawei is
// necessary and not sufficient: EMUI's killer is a separate screen called App launch,
// and an app left on "Manage automatically" there is closed whatever the battery list
// says. Xiaomi has Autostart, ColorOS and Funtouch have their own.
//
// So somebody follows the instruction, does exactly what it asked, and the assistant
// still stops listening overnight. They do not conclude that their phone has an
// undocumented second killer - they conclude the software is broken, and they are
// being reasonable.
//
// A LIST OF CANDIDATES, TRIED IN ORDER, FALLING BACK TO THE GENERIC SCREEN. Vendors
// rename and move these between firmware versions, so this cannot be a single
// component name and a hope. Whatever opens first wins; if none does, the stock
// battery screen still opens and the person is no worse off than before.
//
// AND THE SETTING IS NAMED, because "open App launch and allow Circle AI" is an
// instruction somebody can follow on the phone in their hand. "Allow it to run in
// the background" is not, when no such words appear anywhere on that phone.
//
// VERIFIED WHERE IT SAYS SO. The Huawei entries were read off a P30 on 2026-10-02
// with `dumpsys package com.huawei.systemmanager`; the rest are the well-known
// components for those makes and are unverified here, which is exactly why they are
// a best-effort list with a fallback rather than a promise.

namespace CircleAI.Assistant;

/// <summary>One screen on this phone that decides whether an app keeps running.</summary>
/// <param name="Package">The app that owns the screen.</param>
/// <param name="Activity">The screen itself, fully qualified or leading-dot.</param>
public sealed record BackgroundSetting(string Package, string Activity);

/// <summary>Where to send somebody so the assistant stops being closed.</summary>
public static class BackgroundFix
{
    /// <summary>What this make calls the setting, in its own words.</summary>
    /// <remarks>
    /// THEIR WORDS, NOT OURS. The point of naming it is that somebody can find it;
    /// a name we invented would be worse than no name, because they would look for
    /// it and not find it and conclude the instruction was wrong.
    /// </remarks>
    public static string? SettingName(string? manufacturer) => Make(manufacturer) switch
    {
        // THE WHOLE PATH FOR HUAWEI, BECAUSE THE BUTTON CANNOT TAKE THEM THERE.
        // Measured on a P30 on 2026-10-02: opening App launch needs
        // com.huawei.permission.external_app_settings.USE_COMPONENT, and
        // `dumpsys package permission` gives its protection as signature|privileged
        // with sourcePackage=com.android.settings - so it is grantable only to an app
        // signed with Huawei's platform key. Declaring it changes nothing; the start
        // is still refused. No third-party app can deep-link to that screen, which
        // means the sentence has to be followable on its own.
        "huawei" => "Settings › Battery › App launch",

        // NAMES ONLY FOR THE REST, which is the honest limit of what is known here.
        // These were not read off hardware, and a path invented for a phone nobody
        // has held would send somebody looking for a screen that is not there -
        // worse than the name alone, which they can search their settings for.
        "xiaomi" => "Autostart",
        "oppo"   => "Startup manager",
        "vivo"   => "Background app refresh",
        _        => null,
    };

    /// <summary>The screens to try, best first. Empty when none is known.</summary>
    /// <remarks>
    /// ORDERED NEWEST FIRST within a make, because a phone that has both will have
    /// the newer one wired and the older one left behind as a stub.
    /// </remarks>
    public static IReadOnlyList<BackgroundSetting> Screens(string? manufacturer) => Make(manufacturer) switch
    {
        // PRESENT ON A P30 AND NOT OPENABLE BY US, measured 2026-10-02. Both exist;
        // starting either needs com.huawei.permission.external_app_settings
        // .USE_COMPONENT, whose protection is signature|privileged - so only an app
        // signed with Huawei's platform key can hold it, and declaring it in a
        // third-party manifest changes nothing.
        //
        // KEPT ANYWAY, because the cost is one refused start that is caught and
        // logged, and EMUI varies by build and region; where it does open, somebody
        // lands exactly where they need to be. Where it does not, the fallback opens
        // the stock battery screen and the sentence carries the rest.
        "huawei" =>
        [
            new("com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"),
            new("com.huawei.systemmanager", "com.huawei.systemmanager.appcontrol.activity.StartupAppControlActivity"),
            new("com.huawei.systemmanager", "com.huawei.systemmanager.optimize.process.ProtectActivity"),
        ],

        "xiaomi" =>
        [
            new("com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity"),
        ],

        "oppo" =>
        [
            new("com.coloros.safecenter", "com.coloros.safecenter.permission.startup.StartupAppListActivity"),
            new("com.coloros.safecenter", "com.coloros.safecenter.startupapp.StartupAppListActivity"),
            new("com.oppo.safe", "com.oppo.safe.permission.startup.StartupAppListActivity"),
        ],

        "vivo" =>
        [
            new("com.vivo.permissionmanager", "com.vivo.permissionmanager.activity.BgStartUpManagerActivity"),
            new("com.iqoo.secure", "com.iqoo.secure.ui.phoneoptimize.BgStartUpManager"),
        ],

        _ => [],
    };

    /// <summary>Which family this make belongs to, or null.</summary>
    /// <remarks>
    /// GROUPED BY THE SKIN, NOT THE BRAND. Honor shipped EMUI, Redmi and Poco ship
    /// MIUI, realme and recent OnePlus ship ColorOS, iQOO ships Funtouch - so the
    /// screen to open follows the software rather than the name on the back.
    /// </remarks>
    private static string? Make(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;
        var make = manufacturer.Trim();

        if (Has(make, "huawei", "honor"))                      return "huawei";
        if (Has(make, "xiaomi", "redmi", "poco"))              return "xiaomi";
        if (Has(make, "oppo", "realme", "oneplus"))            return "oppo";
        if (Has(make, "vivo", "iqoo"))                         return "vivo";

        return null;
    }

    private static bool Has(string make, params string[] names)
    {
        foreach (var name in names)
            if (make.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
