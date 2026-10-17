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

using System;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>Turning a crash during a model load into a verdict this device keeps.</summary>
public static class CrashVerdict
{
    /// <summary>What a breadcrumb for a model load looks like.</summary>
    private const string Prefix = "loading ";

    /// <summary>The reason a person is told, and the catalogue stores.</summary>
    /// <remarks>
    /// KEPT AS IT WAS, BECAUSE IT IS ALSO HOW THE OLD RULE'S REFUSALS ARE FOUND. Those
    /// carry this text and no strikes; see <see cref="Reconsider"/>.
    /// </remarks>
    public const string Reason = "it stopped this phone the last time";

    /// <summary>How many deaths in a row, while loading one model, refuse it.</summary>
    /// <remarks>
    /// TWO, AND THE OLD ANSWER WAS ONE.
    /// <para>
    /// One was right for the case it was written for: a 35B whose metadata claimed
    /// 2.5 GB aborted a Circle OS phone and took a dozen system apps with it, and a
    /// native abort does not get better on the second try. It was wrong for the
    /// commoner case. A surviving breadcrumb proves only that the process died
    /// somewhere inside a load, not that the model killed it - and on a P30 at
    /// 95-100 % swap the OS kills this service for memory all the time, during a
    /// cold load that takes 13 to 23 seconds. Measured 2026-10-10: after a run of
    /// those kills, every chat model on that phone was refused, Qwen3.5-0.8B among
    /// them, and the assistant ran a weaker model it did not need to.
    /// </para>
    /// <para>
    /// A model that genuinely cannot load dies EVERY time, so it is still refused,
    /// one attempt later. An OS kill that happens to land mid-load rarely lands
    /// twice in a row. The cost of the change is said plainly: a model that really
    /// does kill the phone gets one more try before it is stopped. The structural
    /// fit check refuses models that could never fit before any of this runs, so
    /// this is the backstop, not the gate.
    /// </para>
    /// </remarks>
    public const int StrikesToRefuse = 2;

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
    /// Record a refusal when <paramref name="crumb"/> shows the last load died, and
    /// report what was decided.
    /// </summary>
    /// <param name="catalog">Where the verdict is kept. Null does nothing.</param>
    /// <param name="crumb">What survived from the last run, or null.</param>
    /// <param name="say">Where to report, in words safe to show a person.</param>
    /// <returns>The model refused, or null when there was nothing to refuse.</returns>
    /// <remarks>
    /// A STRIKE, AND A REFUSAL ONLY AT <see cref="StrikesToRefuse"/> IN A ROW. This
    /// used to refuse on the first death, which turned the OS killing the service
    /// for memory into a permanent verdict on whatever model happened to be loading
    /// - see <see cref="StrikesToRefuse"/> for what that cost on the P30. The reason
    /// is stored so a human can read it and reverse it with
    /// <see cref="IModelCatalog.Pardon"/>.
    ///
    /// NEVER THROWS. This runs on the path that brings the brain up, and a
    /// diagnostic that can take that down is worse than no diagnostic.
    /// </remarks>
    /// <param name="killedByTheSystem">
    /// True when the platform has said the last death was plainly not the model's -
    /// the person stopped it, the app was updated mid-load, a process it depended
    /// on died. Such a death is not counted against the model at all.
    /// <para>
    /// A LOW-MEMORY KILL IS DELIBERATELY NOT ONE OF THESE. A model too big for the
    /// phone dies by exactly that kill, on every load; exempting it would let that
    /// model loop for ever and never be refused. Ambiguous deaths are what the
    /// two-strike rule is for.
    /// </para>
    /// </param>
    public static string? Record(IModelCatalog? catalog, string? crumb, Action<string>? say = null,
                                 bool killedByTheSystem = false)
    {
        try
        {
            if (catalog is null) return null;
            if (ModelFrom(crumb) is not { } model) return null;

            // THE PLATFORM'S WORD BEATS OUR INFERENCE. Where Android says why the
            // process died, a memory kill is the phone's, not the model's.
            if (killedByTheSystem)
            {
                say?.Invoke($"the system stopped this service while {model} was loading; not held against the model");
                return null;
            }

            var strikes = catalog.Strike(model);
            if (strikes == 0)
            {
                // NOT CATALOGUED: there is no row to hold a verdict, and nothing that
                // selects from the catalogue can pick it again. "0 of 2" would be a lie.
                say?.Invoke($"{model} stopped while loading, and it is not in this phone's catalogue");
                return null;
            }

            if (strikes < StrikesToRefuse)
            {
                say?.Invoke($"{model} stopped while loading ({strikes} of {StrikesToRefuse}); trying it again before refusing it");
                return null;
            }

            catalog.Refuse(model, Reason);
            say?.Invoke($"{model} stopped this phone {strikes} times in a row while loading; not using it again");
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
    /// Give every model the old one-strike rule refused a single strike instead.
    /// </summary>
    /// <returns>How many came back.</returns>
    /// <remarks>
    /// THE MODELS THAT RULE GOT WRONG ARE STILL SITTING THERE REFUSED. Changing the
    /// rule does nothing for them: their refusal is already written, and nothing
    /// re-reads it. On the P30 that is Qwen3.5-0.8B - the model this product targets,
    /// complete on disk, refused after the OS killed the service during loads.
    /// <para>
    /// So each comes back on ONE strike, which is exactly what the new rule would
    /// have recorded for the death that refused it. Not a pardon: a model that
    /// really cannot load dies again and is refused again. Safe to run on every
    /// start - it only ever matches refusals written before strikes existed.
    /// </para>
    /// </remarks>
    public static int Reconsider(IModelCatalog? catalog, Action<string>? say = null)
    {
        try
        {
            if (catalog is null) return 0;
            var n = catalog.ForgiveFirstStrikes(Reason);
            if (n > 0)
                say?.Invoke($"{n} model(s) refused after a single stop are back on one strike of {StrikesToRefuse}");
            return n;
        }
        catch (Exception ex)
        {
            say?.Invoke("could not reconsider earlier refusals: " + ex.Message);
            return 0;
        }
    }

    /// <summary>Has this device already refused <paramref name="modelName"/>?</summary>
    /// <remarks>
    /// Asked on the load path, so it must be cheap and must never throw. An absent
    /// catalogue answers false: a device with no verdicts refuses nothing, which is
    /// the behaviour that existed before any of this.
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
