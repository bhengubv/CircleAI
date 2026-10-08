// DeclinedModels.cs
//
// Remembering that somebody said no.
//
// SETUP FINISHES ITSELF. If parts are missing and the phone already has others,
// the app quietly fetches the rest, because a half-provisioned assistant is not
// a state anyone chose - it is where an interrupted download leaves you. Nobody
// should have to know that "Waking" exists in order to end up with it.
//
// BUT "FINISHES ITSELF" AND "TURN THIS OFF" ARE THE SAME ACTION FROM OPPOSITE
// SIDES. Removing an ability promises "turning it back on downloads it again",
// which says plainly that off stays off. Auto-finish with no memory re-downloads
// it the next time the app opens, and the only thing the person learns is that
// the switch does not work.
//
// IT EXISTED ONLY IN THE NATIVE SAMPLE. `SetupPrefs` there is 61 lines of
// SharedPreferences, and `HomeActivity` passed its contents into
// `FirstRun.Plan(..., declined)`. The Blazor head calls the same Plan at
// DeviceSetup:162 and :202 and passes NO declined argument at all - so on the
// head that actually ships, turning an ability off has never stuck.
//
// Audited 2026-09-13. The parameter has been on Plan the whole time; one caller
// used it.
//
// HERE RATHER THAN IN A SAMPLE, because both heads need it and a sample that
// owns a behaviour is how the other head came to be missing it.

using Android.Content;

namespace CircleAI.Assistant.Device;

/// <summary>The models the owner has explicitly turned off.</summary>
/// <remarks>
/// MODEL NAMES, NOT MODALITIES. The removal is per-model and so is the memory:
/// declining a 311 MB vision model says nothing about whether they want the
/// 78 MB one, and a modality-wide refusal would silently answer for both.
/// </remarks>
public static class DeclinedModels
{
    private const string File = "circleai.setup";
    private const string Key = "declined";

    private static ISharedPreferences? Prefs()
    {
        try
        {
            var app = global::Android.App.Application.Context;
            return app.GetSharedPreferences(File, FileCreationMode.Private);
        }
        catch
        {
            // No context yet is not a reason to fail a setup plan; it only means
            // nothing has been declined, which is the safe answer.
            return null;
        }
    }

    /// <summary>The set of model names the owner removed.</summary>
    public static IReadOnlySet<string> All()
    {
        try
        {
            var raw = Prefs()?.GetStringSet(Key, null);
            return raw is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(raw, StringComparer.Ordinal);
        }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }

    /// <summary>Whether this model was turned off on purpose.</summary>
    /// <remarks>
    /// THE SHAPE FirstRun.Plan WANTS. Its `declined` parameter is a
    /// <c>Func&lt;string, bool&gt;</c>, so handing it this method directly is
    /// what stops a caller from reading the set and then forgetting to consult it.
    /// </remarks>
    public static bool Is(string modelName) => All().Contains(modelName);

    /// <summary>Records that this model was turned off on purpose.</summary>
    public static void Decline(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;
        try
        {
            var set = new HashSet<string>(All(), StringComparer.Ordinal) { modelName };
            Prefs()?.Edit()?.PutStringSet(Key, set)?.Apply();
        }
        catch { /* a preference that will not write is not worth a crash */ }
    }

    /// <summary>
    /// Forgets a decline, because they just asked for it again.
    /// </summary>
    /// <remarks>
    /// ASKING FOR A THING IS THE CLEAREST POSSIBLE WITHDRAWAL OF A REFUSAL, and
    /// without this the ability could be turned on and then removed again by the
    /// next auto-finish that still believed the old no.
    /// </remarks>
    public static void Allow(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;
        try
        {
            var set = new HashSet<string>(All(), StringComparer.Ordinal);
            if (!set.Remove(modelName)) return;
            Prefs()?.Edit()?.PutStringSet(Key, set)?.Apply();
        }
        catch { /* as above */ }
    }
}
