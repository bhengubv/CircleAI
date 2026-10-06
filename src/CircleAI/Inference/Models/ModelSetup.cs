// ModelSetup.cs
//
// "How is Circle AI Service not aware of its total engine component setup status?"
//
// It was not, and this type is the answer to that question. Every check the service
// made about its own components was a boolean, about ONE component, asked at the
// moment that component was wanted:
//
//   is the model present?            no       (true, and useless)
//   is there an abandoned download?  no       (the sweep had already eaten it)
//   how big is the model?            22.8 GB  (the full size, as if untouched)
//
// Nothing ever asked "what have I got, in total, and how far through is it" - so a
// Circle OS device sat at 1.1 GB of a 22.8 GB model for three days, answering from
// a 2 B instead, with every readout agreeing it was fine.
//
// IT LIVES HERE, NOT IN THE ANDROID HEAD, because it is arithmetic over a model
// store and the heads are meant to be thin. The first version of it was a private
// method on the Android service, hung off "brain ready" - and the brain only starts
// when somebody asks a question, so on a phone nobody had spoken to it never ran.
// The process that OWNS the models is the one that should finish fetching them,
// whether or not anybody is chatting.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>
/// What a device holds of each engine component, and finishing what it started.
/// </summary>
public static class ModelSetup
{
    /// <summary>
    /// One line naming every engine component in the store and how complete it is.
    /// </summary>
    /// <remarks>
    /// UNCONDITIONAL AT THE CALL SITE, which is the part that matters. A resume
    /// sweep logs only when it finds something, so a device in exactly the broken
    /// state logged nothing at all and read as healthy. A status line that only
    /// appears when there is news is not a status line.
    /// </remarks>
    public static string Describe(BundleModelLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        try
        {
            var parts = new List<string>();
            foreach (var id in Ids(loader))
            {
                if (loader.ModelPresent(id)) { parts.Add(id + " complete"); continue; }

                var (have, need) = loader.Progress(id);
                parts.Add(need > 0 && have > 0
                    ? $"{id} {have * 100 / need}% ({have} of {need})"
                    : id + " nothing yet");
            }

            return parts.Count == 0 ? "no engine components on this device" : string.Join("; ", parts);
        }
        catch (Exception ex) { return "could not read the setup: " + ex.Message; }
    }

    /// <summary>Every model with bytes down and bytes still owed, biggest first.</summary>
    public static IReadOnlyList<(string Name, long Have, long Need)> Unfinished(BundleModelLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        try
        {
            return Ids(loader)
                .Where(loader.ModelPartial)
                .Select(id => { var (have, need) = loader.Progress(id); return (Name: id, Have: have, Need: need); })
                .OrderByDescending(u => u.Need)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Carry on with any download the owner started and did not finish.</summary>
    /// <param name="loader">The model store.</param>
    /// <param name="say">Where to report; every line is safe to show a user.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// THE DISTINCTION THAT MAKES THIS SAFE: it never STARTS a download. Bytes
    /// already on disk are the owner's prior consent - they chose this model on the
    /// setup screen with the size written next to it - so finishing it completes
    /// their instruction rather than issuing a new one. A model at zero bytes is
    /// left alone, which is the rule the setup screen exists to enforce.
    ///
    /// ONE AT A TIME AND LARGEST FIRST, because these are gigabytes on a phone and
    /// two at once is how both end up half done - the state this exists to clear
    /// rather than create.
    ///
    /// NEVER THROWS. A resume is a courtesy; whatever called it must not be taken
    /// down by a network that went away.
    /// </remarks>
    public static async Task ResumeUnfinishedAsync(
        BundleModelLoader loader, Action<string> say, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(say);

        try
        {
            // SAID EVERY TIME, FOUND OR NOT. The only reason this took three days to
            // spot is that silence looked exactly like health.
            say("setup: " + Describe(loader));

            foreach (var (name, have, need) in Unfinished(loader))
            {
                ct.ThrowIfCancellationRequested();
                say($"carrying on with {name}: {have} of {need} bytes already here");

                try
                {
                    await loader.DownloadModelAsync(name, progress: null, ct).ConfigureAwait(false);
                    say("finished " + name);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Metered, offline, out of space, refused. All of them mean "not
                    // now" rather than "never" - the bytes on disk survive and the
                    // next start tries again.
                    say($"could not carry on with {name} yet: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { say("resume sweep failed: " + ex.Message); }
    }

    private static IEnumerable<string> Ids(BundleModelLoader loader) => loader.InstalledIds();
}
