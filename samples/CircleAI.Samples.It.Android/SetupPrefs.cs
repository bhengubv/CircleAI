#nullable enable

// SetupPrefs.cs
//
// Remembering that somebody said no.
//
// Setup finishes itself: if parts are missing and the phone already has others,
// the home screen quietly fetches the rest on resume, because a half-provisioned
// assistant is not a state anyone chose — it is where an interrupted download, or
// an upgrade from the chat-only build, leaves you. Nobody should have to know that
// "Waking" exists in order to end up with it.
//
// BUT "FINISHES ITSELF" AND "TURN THIS OFF" ARE THE SAME ACTION FROM OPPOSITE
// SIDES. The abilities screen offers to remove a model and promises "Turning it
// back on downloads it again" — which says plainly that off stays off. Auto-finish
// with no memory would re-download it the next time the person opened the app,
// and the only thing they would learn is that the switch does not work.
//
// So a decline is recorded, and setup skips what was declined. Turning the same
// ability back on clears it — asking for a thing is the clearest possible signal
// that you no longer refuse it.
//
// Model names, not modalities: the removal is per-model and so is the memory.

using System.Collections.Generic;
using Android.Content;

namespace CircleAI.Samples.It.Mobile;

/// <summary>What the person has explicitly turned off.</summary>
public static class SetupPrefs
{
    const string File = "circleai.setup";
    const string Key  = "declined";

    static ISharedPreferences? Prefs(Context c) =>
        c.ApplicationContext?.GetSharedPreferences(File, FileCreationMode.Private)
        ?? c.GetSharedPreferences(File, FileCreationMode.Private);

    /// <summary>The set of model names the person removed.</summary>
    public static IReadOnlySet<string> Declined(Context context)
    {
        var raw = Prefs(context)?.GetStringSet(Key, null);
        return raw is null ? new HashSet<string>() : new HashSet<string>(raw);
    }

    /// <summary>Records that this model was turned off on purpose.</summary>
    public static void Decline(Context context, string modelName)
    {
        var set = new HashSet<string>(Declined(context)) { modelName };
        Prefs(context)?.Edit()?.PutStringSet(Key, set)?.Apply();
    }

    /// <summary>Forgets a decline, because they just asked for it again.</summary>
    public static void Allow(Context context, string modelName)
    {
        var set = new HashSet<string>(Declined(context));
        if (!set.Remove(modelName)) return;
        Prefs(context)?.Edit()?.PutStringSet(Key, set)?.Apply();
    }
}
