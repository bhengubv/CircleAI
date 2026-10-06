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
    /// Record a refusal when <paramref name="crumb"/> shows the last load died, and
    /// report what was decided.
    /// </summary>
    /// <param name="catalog">Where the verdict is kept. Null does nothing.</param>
    /// <param name="crumb">What survived from the last run, or null.</param>
    /// <param name="say">Where to report, in words safe to show a person.</param>
    /// <returns>The model refused, or null when there was nothing to refuse.</returns>
    /// <remarks>
    /// ONE STRIKE, and that is a decision rather than an oversight. A native abort
    /// during a model load is not a transient failure - the weights do not get
    /// smaller on the second attempt - and retrying one costs a dozen system apps
    /// on the way down. The reason is stored so a human can read it and reverse it
    /// with <see cref="IModelCatalog.Pardon"/>; nothing here ever retries.
    ///
    /// NEVER THROWS. This runs on the path that brings the brain up, and a
    /// diagnostic that can take that down is worse than no diagnostic.
    /// </remarks>
    public static string? Record(IModelCatalog? catalog, string? crumb, Action<string>? say = null)
    {
        try
        {
            if (catalog is null) return null;
            if (ModelFrom(crumb) is not { } model) return null;

            catalog.Refuse(model, Reason);
            say?.Invoke($"{model} stopped this phone the last time it was loaded; not using it again");
            return model;
        }
        catch (Exception ex)
        {
            say?.Invoke("could not record the last crash: " + ex.Message);
            return null;
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
