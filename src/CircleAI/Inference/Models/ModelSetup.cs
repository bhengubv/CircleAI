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
    /// THIS METHOD still never STARTS a download - it only finishes what has bytes
    /// on disk - but IT IS NO LONGER THE WHOLE RULE, and this paragraph used to say
    /// it was. <see cref="FetchNotStartedAsync"/> now begins downloads at zero
    /// bytes, by the owner's decision, so that every ability is live from first run
    /// instead of reading "Nothing for this yet" until somebody finds a button.
    /// Read that method's remarks for what the change costs and what replaced the
    /// setup screen as the disclosure.
    ///
    /// The split is still worth keeping: resuming is safe in any circumstance,
    /// starting is not, so the two stay separate methods and a caller has to mean
    /// it.
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

    /// <summary>
    /// Everything the first-run plan wants that this device does not have a single
    /// byte of yet.
    /// </summary>
    /// <remarks>
    /// THE RULE THIS FILE USED TO ENFORCE HAS BEEN CHANGED DELIBERATELY, and the
    /// comment on ResumeUnfinishedAsync no longer describes the behaviour, so read
    /// this one. That rule was: "it never STARTS a download. Bytes already on disk
    /// are the owner's prior consent - they chose this model on the setup screen
    /// with the size written next to it. A model at zero bytes is left alone."
    ///
    /// The owner has decided otherwise: every ability is meant to be ON from first
    /// run, and an ability that reads "Nothing for this yet" until somebody finds a
    /// Start button is not on by anything. So the fetch now begins by itself.
    ///
    /// WHAT THAT COSTS, STATED PLAINLY BECAUSE IT IS SOMEBODY ELSE'S MONEY. On the
    /// P30 the first-run plan is 3.4 GB - a 2.8 GB brain, 311 MB of eyes, 78 MB of
    /// ears, 122 MB of South African voices, 63 MB of English voice, 7 MB of wake
    /// word. On a metered South African connection that is a real amount of money
    /// spent before anyone agreed to it. Setup.razor shows the "Altogether" total
    /// precisely so a person on a metered link can decide, and that screen is now
    /// no longer the gate.
    ///
    /// SO THE DISCLOSURE HAS TO CARRY THE WEIGHT THE GATE USED TO. The fetch is a
    /// foreground service with a notification naming what is being fetched, and it
    /// is cancellable. That is the whole of what stands between this and a surprise
    /// on someone's bill, which is why it must not be made quieter.
    ///
    /// Ordered smallest first, the reverse of Unfinished: the small ones are the
    /// wake word, a voice and the ears, so a few minutes in, the phone can hear its
    /// name and answer - rather than a person waiting out a 2.8 GB brain before
    /// anything at all works.
    /// </remarks>
    public static IReadOnlyList<(string Name, long Need)> NotStarted(
        BundleModelLoader loader, IEnumerable<(string Name, long Bytes)> planned)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(planned);

        try
        {
            return planned
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Where(p => !loader.ModelPresent(p.Name) && !loader.ModelPartial(p.Name))
                .Select(p => (p.Name, Need: p.Bytes))
                .OrderBy(p => p.Need)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Fetches everything on the plan that has not been started, smallest first.
    /// </summary>
    /// <remarks>
    /// ONE AT A TIME, for the same reason ResumeUnfinishedAsync does it: these are
    /// gigabytes on a phone and two at once is how both end up half done.
    ///
    /// NEVER THROWS. A network that goes away must not take the service with it,
    /// and whatever did land is resumable on the next launch by
    /// ResumeUnfinishedAsync - which is unchanged, and is still the right thing for
    /// bytes already on disk.
    /// </remarks>
    public static async Task FetchNotStartedAsync(
        BundleModelLoader loader,
        IEnumerable<(string Name, long Bytes)> planned,
        Action<string> say,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(say);

        try
        {
            var todo = NotStarted(loader, planned);
            if (todo.Count == 0) { say("setup: nothing left to start"); return; }

            say($"setup: starting {todo.Count} download(s), smallest first");

            foreach (var (name, need) in todo)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    say($"fetching {name} ({need} bytes)");

                    // THE CANCELLATION TOKEN IS NOT OPTIONAL HERE, and the first
                    // version of this line dropped it by calling the two-argument
                    // overload. That left the ct checked only BETWEEN models, so
                    // "Stop" during the 2.8 GB brain did nothing for the rest of
                    // the download. Since this fetch starts WITHOUT anybody
                    // tapping anything, being able to stop it is the whole of
                    // what replaced the setup screen as the consent - an
                    // uncancellable unasked-for 3.4 GB is not a disclosure.
                    await loader.DownloadModelAsync(name, progress: null, ct)
                                .ConfigureAwait(false);
                    say($"{name} complete");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Named, and then on to the next: one unreachable model must not
                    // deny a person the other five.
                    say($"{name} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { say("first-run fetch failed: " + ex.Message); }
    }
}
