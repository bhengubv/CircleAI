// CrashVerdict.cs
//
// THE DEVICE REMEMBERS WHAT IT LEARNED BY TRYING.
//
// Every guard against loading a model that will kill the phone was, until now, a
// prediction recomputed from declared metadata on every launch. On a Circle OS
// device (Tensor G2, 7.6 GB) on 2026-10-04 that prediction was wrong, because the
// metadata was: Qwen3.6-35B-A3B-MNN declared 2.5 GB of RAM against 22.8 GB of
// weights, the fit gate let it through, and loading it did this -
//
//   Process com.bhengubv.circleai.service (pid 28525) has died: signal 6 (Aborted)
//   lowmemorykiller: Kill 'com.android.printspooler' ... 'com.android.contacts'
//     ... 'com.android.camera2' ... 'za.co.circleos.settings'      (ten more)
//
// A native abort runs no managed handler. The `catch` around the load never fires,
// Status is never written, nothing is logged, and the next launch - reading the same
// 2.5 GB - picks the same model and does it again. There is no number anywhere in
// the catalogue that would stop it, because the number is the bug.
//
// So the attempt itself has to leave a mark. A breadcrumb written to disk and
// flushed BEFORE the risky call, deleted after it returns: a file that survives is
// proof the process died in there, and it names which model was being loaded.
//
// WHY A STRING AND NOT A STRUCTURED RECORD. DeviceDiagnostics.BeginRisky already
// exists, already takes free text, and already does the hard part properly -
// FileMode.Create into one slot, fs.Flush(true) to get it past the OS buffer, every
// call swallowed so diagnostics can never be the thing that breaks. It had no model
// name in it and no reader; both are one line each. Writing a second mechanism
// beside a correct one is how a repo ends up with two.
//
// AND THE MARK IS A REASON, NOT A VERDICT (the owner, 2026-11-15: "no models should be
// blocked - if it fails then check why it failed. clean up and try again, everything
// depends on the reason why"). Two deaths in a row with evidence used to refuse the
// model on this phone for good, until a person pardoned it. Now a death is counted, in a
// row, and the count is what the next tries are paced by (FailurePolicy.HoldOff) and why
// the model's files are checked again before its next load (the supervisor): damaged
// files are repaired, and a whole model is tried again, each try waiting longer than the
// last. Nothing here refuses a model any more, and every start lifts a refusal an
// earlier rule wrote (LiftRefusals).

using System;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>Turning a crash during a model's work into a reason this device keeps, and the model tried again.</summary>
public static class CrashVerdict
{
    /// <summary>What a breadcrumb for a model load looks like.</summary>
    private const string Prefix = "loading ";

    /// <summary>The reason the earlier rules refused a model with, and the catalogue stored.</summary>
    /// <remarks>
    /// KEPT, BECAUSE A PHONE UPDATING FROM AN EARLIER BUILD MAY STILL HOLD REFUSALS THAT CARRY IT. Nothing writes it
    /// now; <see cref="LiftRefusals"/> lifts every refusal, this reason's and any other's.
    /// </remarks>
    public const string Reason = "it stopped this phone the last time";

    /// <summary>The breadcrumb to write before loading <paramref name="modelName"/>.</summary>
    public static string Breadcrumb(string modelName)
        => Prefix + (modelName ?? string.Empty);

    /// <summary>Which model a surviving breadcrumb names, or null when it names none.</summary>
    /// <remarks>
    /// The two breadcrumbs this codebase already writes are "loading the model" and
    /// "generating a response (…)". Neither names a model, and neither should be
    /// read as one — "the model" is not an id. Only the prefixed form counts.
    /// </remarks>
    public static string? ModelFrom(string? crumb)
    {
        if (string.IsNullOrWhiteSpace(crumb)) return null;
        if (!crumb.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var name = crumb[Prefix.Length..].Trim();
        return name.Length == 0 || name.Equals("the model", StringComparison.Ordinal) ? null : name;
    }

    /// <summary>
    /// Count the death <paramref name="crumb"/> shows the last load died of against the model it names, when it is the
    /// model's to count, and report what is done about it.
    /// </summary>
    /// <param name="catalog">Where the count is kept. Null does nothing.</param>
    /// <param name="crumb">What survived from the last run, or null.</param>
    /// <param name="say">Where to report, in words safe to show a person.</param>
    /// <returns>The model the death was counted against, or null when none was.</returns>
    /// <remarks>
    /// COUNTED, NEVER REFUSED. Two in a row used to refuse the model here until a person pardoned it - for the case it was
    /// written for, a 35B whose metadata claimed 2.5 GB aborting a Circle OS phone. The count still says how many deaths
    /// in a row: what the next tries are paced by (FailurePolicy.HoldOff) and why the model's files are checked again
    /// before its next load. A clean load clears it (<see cref="Loaded"/>). The structural fit check keeps a model that
    /// could never fit off the phone before any of this runs.
    ///
    /// NEVER THROWS. This runs on the path that brings the brain up, and a
    /// diagnostic that can take that down is worse than no diagnostic.
    /// </remarks>
    /// <param name="killedByTheSystem">
    /// True when the platform has said the last death was plainly not the model's -
    /// the person stopped it, the app was updated mid-load, a process it depended
    /// on died. Such a death is not counted against the model at all.
    /// <para>
    /// THE SUPERVISOR PASSES <c>!failure.CountsAgainstModel</c>, so since 2026-10-23 a
    /// death nothing can explain is not counted either - only a crash with evidence is
    /// (see FailurePolicy.CountsAgainstModel). A model too big for the phone, which dies
    /// by the low-memory killer on every load, no longer loops for ever because of
    /// that: each further load waits longer (FailurePolicy.HoldOff), and the fit check
    /// keeps the ones that could never fit off the phone before any of this runs.
    /// </para>
    /// </param>
    /// <param name="doing">
    /// What the model was doing when it stopped, for the words. A load's breadcrumb is "loading"; the
    /// supervisor names a turn and its slot - "reading its turn as the Vision specialist" - because
    /// on the P30 on 2026-11-02 a turn's death was said as the main brain's load (OPEN-GAPS E33).
    /// </param>
    /// <param name="why">
    /// How the death was classified, when the caller knows it - said in place of "the system stopped
    /// this service", which a watchdog's own kill is not.
    /// </param>
    public static string? Record(IModelCatalog? catalog, string? crumb, Action<string>? say = null,
                                 bool killedByTheSystem = false, string doing = "loading", string? why = null)
    {
        try
        {
            if (catalog is null) return null;
            if (ModelFrom(crumb) is not { } model) return null;

            // THE PLATFORM'S WORD BEATS OUR INFERENCE. Where Android says why the
            // process died, a memory kill is the phone's, not the model's.
            if (killedByTheSystem)
            {
                say?.Invoke(why is null
                    ? $"the system stopped this service while {model} was {doing}; not held against the model"
                    : $"{model} stopped while {doing} ({why}); not held against the model");
                return null;
            }

            var strikes = catalog.Strike(model);
            if (strikes == 0)
            {
                // NOT CATALOGUED: there is no row to hold the count.
                say?.Invoke($"{model} stopped while {doing}, and it is not in this phone's catalogue");
                return null;
            }

            say?.Invoke($"{model} stopped while {doing}, {strikes} time{(strikes == 1 ? "" : "s")} in a row: its files are checked before it is tried again, and each try waits longer than the last");
            return model;
        }
        catch (Exception ex)
        {
            say?.Invoke("could not record the last crash: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// A load of <paramref name="modelName"/> completed: forget its strikes and
    /// stamp the success.
    /// </summary>
    /// <remarks>
    /// The strikes count deaths IN A ROW, so this is what makes them consecutive. A
    /// model that loads cleanly has shown that an earlier death was the phone's. The
    /// stamp is what housekeeping waits for before it removes the model this one
    /// replaces - see <see cref="IModelCatalog.RecordLoad"/>.
    /// NEVER THROWS, for the same reason as <see cref="Record"/>.
    /// </remarks>
    public static void Loaded(IModelCatalog? catalog, string? modelName)
    {
        try
        {
            if (catalog is null || string.IsNullOrWhiteSpace(modelName)) return;
            catalog.RecordLoad(modelName!);
        }
        catch { /* a diagnostic never stops the brain */ }
    }

    /// <summary>
    /// Lift every refusal this device holds, whatever its reason: no model is refused any more, so one an earlier rule
    /// wrote is tried again like any other.
    /// </summary>
    /// <returns>How many were lifted; -1 when it could not finish, so the caller tries again at the next start.</returns>
    /// <remarks>
    /// THE REFUSALS THE EARLIER RULES WROTE ARE STILL THERE, AND NOTHING RE-READS ONE (the owner, 2026-11-15: "no models
    /// should be blocked"). Changing the rule alone would leave every model a phone refused before refused for good. Run
    /// on every start: nothing writes a refusal now, so after the first it finds none. A pardon starts the model's count
    /// of deaths afresh (IModelCatalog.Pardon); a hold-off the supervisor keeps for it is its own and still applies
    /// (FailurePolicy.HoldOff). NEVER THROWS, for the same reason as <see cref="Record"/>.
    /// </remarks>
    public static int LiftRefusals(IModelCatalog? catalog, Action<string>? say = null)
    {
        try
        {
            if (catalog is null) return 0;
            var n = 0;
            foreach (var a in catalog.AllAssessed())
            {
                if (!a.Refused) continue;
                catalog.Pardon(a.Entry.Name);
                n++;
            }
            if (n > 0)
                say?.Invoke($"{n} model{(n == 1 ? "" : "s")} this phone had refused {(n == 1 ? "is" : "are")} tried again: a stop is a reason to look into, never a verdict");
            return n;
        }
        catch (Exception ex)
        {
            say?.Invoke("could not lift earlier refusals: " + ex.Message);
            return -1;
        }
    }

    /// <summary>Does this device hold a refusal of <paramref name="modelName"/>?</summary>
    /// <remarks>
    /// Asked on the load path, so it must be cheap and must never throw. An absent
    /// catalogue answers false. Nothing writes a refusal any more and every start lifts
    /// the ones an earlier rule wrote (<see cref="LiftRefusals"/>), so after that this is
    /// false for every model.
    /// </remarks>
    public static bool IsRefused(IModelCatalog? catalog, string? modelName)
    {
        try
        {
            if (catalog is null || string.IsNullOrWhiteSpace(modelName)) return false;
            return catalog.Assessment(modelName!)?.Refused == true;
        }
        catch { return false; }
    }
}
