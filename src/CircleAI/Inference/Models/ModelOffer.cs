// ModelOffer.cs
//
// NEVER OFFER SOMEBODY A DOWNLOAD THIS PHONE CANNOT USE.
//
// A Circle OS device was offered Qwen3.6-35B-A3B-MNN at 22.8 GB. The size was
// honest and the person accepted it. Three days later it finished, and loading it
// aborted the process and set lowmemorykiller on a dozen system apps. The offer was
// the defect, not the number next to it.
//
// THE DECISION ALREADY EXISTS; WHAT WAS MISSING WAS ANYONE ASKING IT. The catalogue
// assesses every row against this device and writes `compatible`, and CrashVerdict
// writes a refusal when a load actually kills the phone. The setup plan consulted
// neither. FirstRun.Plan has had a `declined` predicate the whole time for "what the
// owner turned off" - one seam, and this is the same shape: three reasons a model
// should not be in front of somebody, asked as one question.
//
// AND AN UNKNOWN IS NOT A NO, WHICH IS THE TRAP IN THIS. On a first launch the
// catalogue is empty or unassessed, so every row reads compatible = 0. A predicate
// that treated that as "cannot run" would offer NOTHING on the one run where the
// whole point is to offer everything. So only a row that has actually been assessed
// can be excluded, and `assessed_at` is what says it has. The same principle as
// FitsStorage's "0 free = unknown, so skip" and CouldEverHold's "unmeasured, so
// admit": the absence of a measurement is not a measurement.

using System;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>Whether a model is worth putting in front of somebody on this device.</summary>
public static class ModelOffer
{
    /// <summary>Should <paramref name="modelName"/> be offered here?</summary>
    /// <param name="catalog">This device's verdicts. Null offers everything.</param>
    /// <param name="modelName">The catalogue id.</param>
    /// <remarks>
    /// NEVER THROWS, and answers YES when it cannot tell. This gates a download
    /// offer: being wrong towards "offer it" costs somebody data they can refuse,
    /// and being wrong towards "hide it" costs them an assistant that says it can
    /// do nothing on a phone that works.
    /// </remarks>
    public static bool Worth(IModelCatalog? catalog, string? modelName)
    {
        try
        {
            if (catalog is null || string.IsNullOrWhiteSpace(modelName)) return true;

            var a = catalog.Assessment(modelName!);
            if (a is null) return true;              // not catalogued here: not our call

            // A refusal is evidence and stands whether or not anything was assessed.
            if (a.Refused) return false;

            // And a retired model is below the floor - "nothing less than Qwen3.5 is
            // the LLM" - so it is never offered at all.
            if (a.Entry.Retired) return false;

            // THE BRAIN'S FIT IS NOT THIS BIT'S TO JUDGE. `compatible` is the
            // assessor's reading of FREE RAM at boot, and on the P30 - 0.44 GB free,
            // swap near full, on 2026-10-16 - it said no to every Qwen3.5 there is. As
            // a veto that meant setup planned no brain and every screen named none,
            // while the selector, correctly, wanted Qwen3.5-0.8B. The brain is chosen
            // by one rule everywhere - the best that fits now, else the smallest this
            // phone could EVER hold (ModelChoice) - and that rule still keeps a 22.8 GB
            // model off a 7.6 GB phone: it neither fits now nor ever could.
            if (a.Entry.Modality == CircleAI.Core.ModelModality.Chat) return true;

            // UNASSESSED IS UNKNOWN. See the header: treating it as "no" empties the
            // first-run offer, which is the one run that has to work.
            if (a.AssessedAt is null) return true;

            return a.Compatible;
        }
        catch { return true; }
    }

    /// <summary>
    /// A predicate for <c>FirstRun.Plan</c>'s <c>declined</c> argument: true when a
    /// model should be left out.
    /// </summary>
    /// <param name="catalog">This device's verdicts.</param>
    /// <param name="alsoDeclined">
    /// What the owner turned off, which is a separate and equally binding reason.
    /// </param>
    /// <remarks>
    /// ONE SEAM, THREE REASONS. The owner turned it off; this phone was killed by it;
    /// this phone cannot run it. A screen does not need to tell those apart to know
    /// not to offer the download, and composing them here means a caller cannot
    /// honour one and forget the others - which is exactly what happened when only
    /// the native sample passed `declined` and the shipping head quietly
    /// re-downloaded whatever somebody had switched off.
    /// </remarks>
    public static Func<string, bool> NotWorthOffering(
        IModelCatalog? catalog, Func<string, bool>? alsoDeclined = null)
        => name => (alsoDeclined?.Invoke(name) ?? false) || !Worth(catalog, name);
}
