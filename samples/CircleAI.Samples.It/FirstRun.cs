#nullable enable

// FirstRun.cs
//
// "Tap to start" has to actually start something.
//
// THE FIRST SCREEN A NEW PERSON SEES SAID "Let's set it up — It needs a few things
// first. Tap to start." Tapping played a greeting in isiZulu and set nothing up.
// Tap again and it greeted you in Kiswahili. There was no path from a fresh
// install to a working assistant anywhere on that screen, and the screen was
// promising one.
//
// The greeting is right when parts are still ARRIVING — it says "alive, wait a
// second". It is wrong when nothing is coming, and the home screen could not tell
// those apart because both are "cannot talk yet". Readiness already names them
// separately (Waking vs NeedsSetup); only the tap conflated them.
//
// The other half of the fix is refusing to hand the person a menu. Setup could
// have opened the abilities screen and let them pick, and that is the shape most
// apps take — but choosing between sixteen models is our filing system, not their
// problem, and the whole argument of AbilitiesActivity is that we are equipped to
// make that call and they are not. One tap, we pick, it downloads.
//
// ORDER IS THE DESIGN. The parts cost wildly different amounts and Readiness
// already stages them, so setup fetches them in the order that makes the phone
// useful soonest rather than the order that finishes soonest:
//
//     the voice        ~60 MB     it can say something back
//     the ears         ~78 MB     it can hear you
//     the wake word    ~6 MB      you can stop touching it
//     the brain       ~433 MB     it can think — the download, essentially
//
// Each finished step visibly upgrades the screen behind it, so the wait is spent
// watching it become able to do things rather than watching one bar. The brain is
// last because it is nine tenths of the bytes and the only part you can usefully
// wait for while already talking to it.
//
// There is no UI in this file, and it lives in the SHARED sample rather than the
// Android head, so both the plan and the fetch are testable off-device. The reason
// the first-run path was never tested is that it only ever existed inside a click
// handler, where nothing but a phone can reach it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Samples.It;

/// <summary>One thing first-run will fetch, in the words the screen will use.</summary>
/// <param name="Title">What this gives the person — "the voice", not "MMS TTS".</param>
/// <param name="Modality">Which ability it serves.</param>
/// <param name="Model">The chosen model. Picked for this device, not by the user.</param>
public readonly record struct SetupStep(string Title, ModelModality Modality, ModelEntry Model);

/// <summary>One thing this device either has or does not.</summary>
/// <param name="Title">What it gives the person - "the ears", not "Whisper-tiny".</param>
/// <param name="Present">Whether the bytes are on this phone.</param>
/// <param name="Bytes">How big it is, or would be.</param>
/// <param name="Detail">
/// What it means here - "eleven languages", "not on this phone". The honest
/// answer for this handset rather than what the catalogue could offer.
/// </param>
public readonly record struct CapabilityRow(string Title, bool Present, long Bytes, string Detail);

/// <summary>What this phone can do, and what it is still missing.</summary>
/// <param name="Rows">Every capability, present or not, in a fixed order.</param>
/// <param name="Present">How many are here.</param>
/// <param name="Total">How many there are.</param>
public readonly record struct Capabilities(
    IReadOnlyList<CapabilityRow> Rows, int Present, int Total);

/// <summary>How far setup has got, for showing on the home screen.</summary>
/// <param name="Index">Zero-based step being fetched.</param>
/// <param name="Count">How many steps in total.</param>
/// <param name="Title">The current step's words.</param>
/// <param name="Fraction">0..1 across the whole of setup, weighted by bytes.</param>
/// <param name="BytesPerSecond">Live rate, or 0 before one is known.</param>
/// <param name="Remaining">What is left of the WHOLE setup, not just this part.</param>
/// <remarks>
/// THE NUMBERS ARE THE POINT, because the wait is not one length. The same
/// bundle is minutes on a premium handset and most of an hour on a P30 Lite over
/// 48 Mbps — measured on the device, and unchanged by opening eight sockets
/// instead of one, so it is the link and it varies per person. A bare percentage
/// is honest on the fast phone and reads as a hang on the slow one.
/// </remarks>
/// <param name="Phase">
/// What the download service is actually doing. Carried through rather than
/// dropped: verifying a 1.3 GB file takes about a minute on a mid-range phone
/// with no bytes moving, and a screen that can only show a frozen countdown
/// reports that as a hang.
/// </param>
public readonly record struct SetupProgress(
    int Index, int Count, string Title, float Fraction,
    double BytesPerSecond = 0, TimeSpan Remaining = default,
    CircleAI.Core.DownloadPhase Phase = CircleAI.Core.DownloadPhase.Downloading)
{
    /// <summary>A line fit to put on screen: what, how fast, how long left.</summary>
    public string Describe()
    {
        var pct = $"{Fraction * 100:0}%";
        if (BytesPerSecond <= 0) return $"{Title} — {pct}";

        var mbps = $"{BytesPerSecond / (1024 * 1024):0.0} MB/s";
        if (Remaining <= TimeSpan.Zero || Remaining > TimeSpan.FromHours(12))
            return $"{Title} — {pct} · {mbps}";

        // Minutes, not hh:mm:ss. Nobody waiting on a download is counting
        // seconds, and "43 minutes left" is a decision they can act on —
        // put the phone down, or stop and come back on wifi.
        var left = Remaining.TotalMinutes >= 1
            ? $"{Remaining.TotalMinutes:0} min left"
            : "less than a minute left";
        return $"{Title} — {pct} · {mbps} · {left}";
    }
}

/// <summary>One thing this build wants on a device.</summary>
/// <param name="Title">What it gives the person — "the voice", not "MMS TTS".</param>
/// <param name="Modality">Which ability it serves.</param>
/// <param name="Named">
/// A specific catalogue entry, when the choice is not the selector's to make.
/// </param>
/// <param name="Speaks">
/// A language root the model must speak, when the entry is chosen by ability
/// rather than by name. Null lets the selector pick on quality and fit alone,
/// which is right for the ears and the brain and wrong for a voice: the point of
/// asking for a voice in somebody's own language is the language.
/// </param>
public readonly record struct Want(
    string Title, ModelModality Modality, string? Named, string? Speaks = null);

/// <summary>Getting a fresh install to a working assistant, in one tap.</summary>
public static class FirstRun
{
    /// <summary>
    /// What this device needs, in the order that makes it useful soonest.
    /// </summary>
    /// <remarks>
    /// Anything already on disk is skipped, so this doubles as "finish what was
    /// interrupted" — a setup that died halfway through the brain resumes at the
    /// brain rather than starting again from the voice.
    /// </remarks>
    /// <param name="speech">
    /// Whether this BUILD can speak and listen. The chat-only APK ships without
    /// CircleAI.Voice, whisper and the wake loop — none of that code is compiled
    /// in — so fetching a voice, ears and a wake bundle there spends somebody's
    /// data on a hundred and forty megabytes that no line of the binary can open.
    /// The models are real and the catalogue lists them either way; what differs
    /// is whether anything on the phone can use them.
    /// </param>
    /// <param name="declined">
    /// Returns true for a model the person explicitly turned off. Without this the
    /// abilities screen's "Turning it back on downloads it again" becomes a lie in
    /// the other direction: setup would quietly re-fetch, on the next resume, the
    /// thing they just chose to remove.
    /// </param>
    public static IReadOnlyList<SetupStep> Plan(
        ModelRegistryService registry, BundleModelLoader loader, DeviceProbe probe,
        bool speech, Func<string, bool>? declined = null, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loader);

        // TWO VOICES, BOTH BY NAME, AND NEITHER LEFT TO THE SELECTOR.
        //
        // Asking for "a TTS model that fits" fetched exactly one, and which one
        // depended on quality ranks — the same fit-based guessing that once put a
        // Nepali voice in an English assistant's mouth. Speech needs both, for
        // different reasons: Vits-11ZA is grapheme-driven and right for the ten
        // South African languages, and structurally wrong for English, where it
        // measured 0.17 word error rate against Piper lessac's 0.00 because
        // English spelling cannot be sounded out letter by letter.
        //
        // Named from ItSpeaker's own constants so setup cannot fetch one voice
        // while the speaker asks for another. Together they are about 185 MB, or
        // 95 MB once the quantised SA voice is published — next to a 22 GB brain,
        // the English voice is rounding error and it is the difference between an
        // assistant you can act on and one you cannot.
        var wanted = Wanted(speech, language);

        var steps = new List<SetupStep>();
        foreach (var (title, modality, named, speaks) in wanted)
        {
            ModelEntry? pick;
            if (named is not null)
            {
                // A NAMED ENTRY IS CHECKED BY NAME, not by modality. The old test
                // asked "is there any model of this modality on disk", which for
                // two voices would have seen the first one land and skipped the
                // second — fetching one voice and reporting both as done.
                if (loader.ModelPresent(named)) continue;
                if (declined is not null && declined(named)) continue;

                pick = registry.AllModels.FirstOrDefault(
                    m => string.Equals(m.Name, named, StringComparison.OrdinalIgnoreCase)
                         && Fits(m, probe));
            }
            else
            {
                // ALREADY THERE IS ALREADY DONE - IN THE LANGUAGE THAT WAS ASKED
                // FOR. Asking only "is there any model of this modality on disk"
                // is right for the ears and the brain, where one is enough, and
                // wrong for a voice requested by language: a phone that already
                // had an English voice would count that as its Japanese one and
                // fetch nothing. Note this asks the loader, not the registry:
                // what matters is bytes on disk, not what we know about.
                if (registry.AllModels.Any(m => m.Modality == modality
                                             && (speaks is null || SpeaksIt(m, speaks))
                                             && loader.ModelPresent(m.Name)))
                    continue;

                // The same choice the abilities screen makes, deliberately: best that
                // fits, smallest among equals. Two places asking "which model" and
                // disagreeing would mean setup installs one thing and the screen
                // reports another.
                pick = registry.AllModels
                    .Where(m => m.Modality == modality && Fits(m, probe))
                    .Where(m => speaks is null || SpeaksIt(m, speaks))
                    .Where(m => declined is null || !declined(m.Name))
                    .OrderByDescending(m => m.QualityRank)
                    .ThenBy(m => m.MinRamGb)
                    .FirstOrDefault();
            }

            // NO ENTRY IS NOT AN ERROR HERE. A phone too small for any chat model
            // still gets a voice and ears, and should be set up that far rather
            // than refused outright. The screen keeps saying what is missing.
            if (pick is not null) steps.Add(new SetupStep(title, modality, pick));
        }

        return steps;
    }

    /// <summary>
    /// Everything this build wants on a device, in the order that makes it
    /// useful soonest.
    /// </summary>
    /// <remarks>
    /// ONE LIST, TWO READINGS. Plan filters it down to what still has to be
    /// fetched; Census reports all of it with what is present. Written twice
    /// they would drift, and a setup screen that names a capability the census
    /// does not is the same failure as the language count and the model choice
    /// before it.
    /// </remarks>
    private static List<Want> Wanted(bool speech, string? language = null)
    {
        var wanted = new List<Want>();
        if (speech)
        {
            wanted.Add(new("the English voice", ModelModality.Tts, VoiceNames.English));
            // "the local voice" said nothing: local to where, and in what? These
            // titles exist to say what the download GIVES somebody - "the voice",
            // not "MMS TTS" - and this one is the reason to want the app at all.
            // Vits-11ZA is multi-speaker across eleven South African languages;
            // naming that is both more honest and a better argument than "local".
            wanted.Add(new("the South African voices", ModelModality.Tts, VoiceNames.SouthAfrican));

            // AND A VOICE FOR THE LANGUAGE THIS PHONE IS ACTUALLY SET TO.
            //
            // THE TWO ABOVE ARE FIXED, AND THAT WAS THE BIAS. Their reasons are
            // real and technical - Vits-11ZA is grapheme-driven and right for the
            // South African languages, structurally wrong for English, which is
            // why English is fetched separately - but between them they describe
            // one market. Every other phone on earth downloaded those two, got a
            // voice in neither of its own languages, and was then shown a loading
            // screen presenting the result as what it could do.
            //
            // Seen on a P30 running on Japan time with Japanese on the translate
            // screen: "the English voice / the South African voices", and no
            // Japanese anywhere.
            //
            // ADDED, NOT SUBSTITUTED. English stays because it is the language the
            // rest of the app falls back to, and the South African set stays
            // because it is what this app is for; a phone in Lagos or Osaka now
            // also gets its own. Skipped when the phone's language is already one
            // of the two, so the common case fetches exactly what it did before.
            var mine = Root(language);
            if (mine is not null && !AlreadySpoken(mine))
                wanted.Add(new(VoiceTitle(mine), ModelModality.Tts, null, Speaks: mine));

            wanted.Add(new("the ears",      ModelModality.Asr,      null));
            wanted.Add(new("the wake word", ModelModality.WakeWord, null));
        }
        wanted.Add(new("the brain", ModelModality.Chat, null));

        return wanted;
    }

    /// <summary>Whether a catalogue entry claims to speak a language.</summary>
    /// <remarks>
    /// COMPARED ON THE LANGUAGE, NOT THE TAG. Entries carry comma-separated tags
    /// and some are regional - "es-ES", "pt-BR" - so a phone set to "es" must
    /// still find the Spanish voice. The same rule the wake phrase table needed,
    /// for the same reason.
    /// </remarks>
    private static bool SpeaksIt(ModelEntry entry, string root) =>
        (entry.Language ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Root(t))
            .Any(t => string.Equals(t, root, StringComparison.OrdinalIgnoreCase));

    /// <summary>The language part of a tag, lowercased; null when there is none.</summary>
    private static string? Root(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var root = tag.Split('-', '_')[0].Trim().ToLowerInvariant();
        return root.Length == 0 ? null : root;
    }

    /// <summary>Whether the two fixed voices already cover a language.</summary>
    /// <remarks>
    /// The South African bundle's own catalogue entry is the authority on what it
    /// speaks, but Wanted runs without a registry - it is the list, not a lookup -
    /// so the tags are stated here. They are the eleven official languages of
    /// South Africa, which is a fact about a country rather than about a file, and
    /// it does not drift the way a model's contents can.
    /// </remarks>
    private static bool AlreadySpoken(string root) =>
        root is "en" or "af" or "zu" or "xh" or "nso" or "tn" or "st" or "ts" or "ss" or "ve" or "nr";

    /// <summary>"the Japanese voice", from the app's own language table.</summary>
    /// <remarks>
    /// NAMED, NOT "your language". The row sits beside "the English voice" and
    /// "the South African voices" and would read as an afterthought without its
    /// own name - which is precisely the impression being corrected. Falls back to
    /// the tag when the table has no entry, because a bare code is still more use
    /// than a pronoun.
    /// </remarks>
    private static string VoiceTitle(string root) =>
        $"the {SampleLanguages.Find(root)?.Name ?? root} voice";

    /// <summary>
    /// What this phone can do, and what it is still missing.
    /// </summary>
    /// <remarks>
    /// THE SAME LIST PLAN WALKS, unfiltered. Plan answers "what still has to be
    /// fetched"; this answers "what is here" - and they are two readings of one
    /// list rather than two lists that could disagree. A census that named a
    /// capability setup did not know about, or missed one it was fetching, would
    /// be the same failure this app has had four times over: one fact, two owners.
    ///
    /// It is what somebody sees while waiting. Aether opens by telling you which
    /// radios your phone has and which it does not, and the seconds you were
    /// going to spend anyway become the only moment you would ever read it. The
    /// point is the same here: on a cheap phone more is missing, and the people
    /// most likely to be missing something are exactly who this is for.
    /// </remarks>
    public static Capabilities Census(
        ModelRegistryService registry, BundleModelLoader loader, DeviceProbe probe,
        bool speech, Func<string, bool>? declined = null, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loader);

        var missing = Plan(registry, loader, probe, speech, declined)
            .ToDictionary(s => s.Title, s => s.Model, StringComparer.OrdinalIgnoreCase);

        var rows = new List<CapabilityRow>();
        foreach (var (title, modality, named, _) in Wanted(speech, language))
        {
            if (missing.TryGetValue(title, out var wanted))
            {
                rows.Add(new CapabilityRow(title, false, wanted.TotalBytes, "not on this phone yet"));
                continue;
            }

            // Present. The size is what is actually on disk for it, and the
            // detail is what it means HERE rather than what the catalogue says
            // it could mean somewhere else.
            var entry = named is not null
                ? registry.AllModels.FirstOrDefault(m =>
                      string.Equals(m.Name, named, StringComparison.OrdinalIgnoreCase))
                : registry.AllModels.FirstOrDefault(m =>
                      m.Modality == modality && loader.ModelPresent(m.Name));

            rows.Add(new CapabilityRow(title, true, entry?.TotalBytes ?? 0,
                                       Detail(entry, modality, language)));
        }

        // AND EVERYTHING ELSE THAT IS ACTUALLY ON THE PHONE.
        //
        // THE CENSUS READ THE PLAN, NOT THE DISK. Wanted names two voices, so a
        // phone carrying thirty-two of them reported "5 of 5 on this phone" over
        // a list that mentioned eleven languages. Every row was true and the
        // screen as a whole was wrong, which is the harder kind: the person had
        // downloaded thirty more voices and the one screen whose entire job is
        // saying what this handset can do never mentioned them.
        //
        // That is the same mistake this file already names twice - one fact, two
        // owners - with the plan standing in for the disk. The plan answers "what
        // still has to be fetched" and is the wrong question to ask about what is
        // here.
        //
        // ONE ROW, NOT THIRTY. Naming every voice would bury the four capabilities
        // this screen exists to report, and the screen's own rule is that a count
        // must not outrun what it shows - so the count and the languages it buys
        // are both on the row, and the row is about the surplus rather than
        // pretending the named two are all there is.
        if (speech)
        {
            var present = registry.AllModels
                .Where(m => m.Modality == ModelModality.Tts && loader.ModelPresent(m.Name));

            if (OtherVoices(present, Wanted(speech, language), language) is { } surplus)
                rows.Add(surplus);
        }

        return new Capabilities(rows, rows.Count(r => r.Present), rows.Count);
    }

    /// <summary>The row for voices that are here but were never in the plan.</summary>
    /// <remarks>
    /// SEPARATED FROM THE DISK ON PURPOSE, so it can be tested. Deciding which
    /// voices are surplus and what to say about them is the part that was wrong;
    /// reading whether a file is on disk is BundleModelLoader's job and is
    /// exercised elsewhere. Keeping them in one method would have made this
    /// testable only by writing sixty-megabyte anchor files into a temp folder,
    /// which is how a rule ends up with no test at all — as the whole of this
    /// file did, despite its header saying it was moved here to be testable.
    /// <para>
    /// BOTH NUMBERS ON THE ROW, because they answer different questions. The
    /// count is what was downloaded; the languages are what it can be understood
    /// in, and the second is the one that matters to somebody wondering whether
    /// their language is in there. They are not the same number — one
    /// multi-speaker bundle carries eleven languages by itself.
    /// </para>
    /// </remarks>
    /// <param name="presentVoices">Every TTS model actually on this device.</param>
    /// <param name="wanted">The plan's own list, whose named voices already have rows.</param>
    /// <returns>The row, or null when the plan already accounts for everything.</returns>
    public static CapabilityRow? OtherVoices(
        IEnumerable<ModelEntry> presentVoices, IEnumerable<Want> wanted, string? mine = null)
    {
        ArgumentNullException.ThrowIfNull(presentVoices);
        ArgumentNullException.ThrowIfNull(wanted);

        var already = wanted
            .Where(w => w.Modality == ModelModality.Tts && w.Named is not null)
            .Select(w => w.Named!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extra = presentVoices
            .Where(m => m.Modality == ModelModality.Tts && !already.Contains(m.Name))
            .ToList();

        if (extra.Count == 0) return null;

        // NAMED, LIKE EVERY OTHER VOICE ROW. This said "9 more, 4 languages",
        // which tells somebody a size and not a single thing they might have
        // opened the screen to find out. The count moves to the TITLE, where it
        // belongs - it is what the row IS - and the languages take the detail,
        // where the other voice rows put theirs.
        var languages = Languages(
            string.Join(',', extra.Select(v => v.Language).Where(l => !string.IsNullOrWhiteSpace(l))),
            mine);

        var title = extra.Count == 1 ? "one more voice" : $"{extra.Count} more voices";

        return new CapabilityRow(title, true, extra.Sum(m => m.TotalBytes), languages);
    }

    /// <inheritdoc cref="Wanted(bool)"/>
    /// <remarks>Exposed so a test can pass the real plan rather than a copy of it.</remarks>
    public static IReadOnlyList<Want> WantedFor(bool speech, string? language = null)
        => Wanted(speech, language);

    /// <summary>What being present actually gets somebody.</summary>
    /// <remarks>
    /// A COUNT OF LANGUAGES THAT SPEAK, not of languages catalogued. Home has
    /// claimed 78 - what the registry says this handset could plan for - while
    /// the two voices actually installed cover about twelve. A screen whose whole
    /// job is being checkable cannot repeat that.
    /// </remarks>
    private static string Detail(ModelEntry? entry, ModelModality modality, string? mine) =>
        entry is null || modality != ModelModality.Tts ? "here" : Languages(entry.Language, mine);

    /// <summary>The languages a voice speaks, named, the owner's first.</summary>
    /// <remarks>
    /// NAMED, NOT COUNTED. The row used to read "10 languages", which is a number
    /// somebody has to take on trust about the one thing they might actually be
    /// checking: whether their own language is in there. "10 languages" and "10
    /// languages, none of them yours" look identical, and on a phone set to
    /// Japanese the second was the true one.
    /// <para>
    /// THE OWNER'S LANGUAGE FIRST, for the same reason. If it is in the bundle it
    /// is the first word on the row rather than the seventh, and if it is absent
    /// that is now visible instead of hidden behind a count.
    /// </para>
    /// <para>
    /// Three then "+n more", because the row is one line on a phone. The cap is
    /// about the width of a screen, not about how much is true - which is why
    /// what it hides is stated rather than dropped.
    /// </para>
    /// </remarks>
    private static string Languages(string? tags, string? mine, int show = 3)
    {
        var roots = (tags ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(Root)
            .Where(r => r is not null)
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0) return "here";

        var owner = Root(mine);
        var ordered = roots
            .OrderByDescending(r => owner is not null
                                 && string.Equals(r, owner, StringComparison.OrdinalIgnoreCase))
            .ThenBy(LanguageName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var named = ordered.Take(show).Select(LanguageName).ToList();
        var rest = ordered.Count - named.Count;

        return rest > 0
            ? $"{string.Join(", ", named)} +{rest} more"
            : string.Join(", ", named);
    }

    /// <summary>A language's name, from the app's own table; the tag if it has none.</summary>
    private static string LanguageName(string root) =>
        SampleLanguages.Find(root)?.Name ?? root;

    /// <summary>Fetches the plan, reporting progress across the whole of it.</summary>
    /// <remarks>
    /// Progress is WEIGHTED BY BYTES, not by step. Four equal-looking steps where
    /// one is nine tenths of the download produce a bar that races to 75% and then
    /// appears to hang for four minutes, which reads as broken. Weighted, it moves
    /// at roughly the speed of the download the whole way.
    /// </remarks>
    public static async Task RunAsync(
        BundleModelLoader loader, IReadOnlyList<SetupStep> steps,
        IProgress<SetupProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) return;

        var total = Math.Max(1L, steps.Sum(s => s.Model.TotalBytes));
        long done = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step  = steps[i];
            var start = done;
            var index = i;

            var inner = new Progress<CircleAI.Core.DownloadProgress>(p =>
            {
                var doneNow  = start + step.Model.TotalBytes * Math.Clamp(p.Ratio, 0, 1);
                var fraction = (float)(doneNow / total);

                // ETA ACROSS THE WHOLE OF SETUP, not just this file. The download
                // service knows how long the current file has left; a person
                // wants to know when they can use the phone, which is when the
                // LAST byte lands. Extrapolated from the live rate over
                // everything still outstanding.
                var left = p.BytesPerSecond > 0
                    ? TimeSpan.FromSeconds((total - doneNow) / p.BytesPerSecond)
                    : TimeSpan.Zero;

                progress?.Report(new SetupProgress(
                    index, steps.Count, step.Title, fraction, p.BytesPerSecond, left, p.Phase));
            });

            await Task.Run(() => loader.DownloadModelAsync(step.Model.Name, inner, ct), ct)
                      .ConfigureAwait(false);

            done += step.Model.TotalBytes;
            progress?.Report(new SetupProgress(
                index, steps.Count, step.Title, done / (float)total,
                Phase: CircleAI.Core.DownloadPhase.Complete));
        }
    }

    /// <summary>Whether this model can run on this phone at all.</summary>
    /// <remarks>
    /// Identical to the abilities screen's rule, and it depends on the platform
    /// memory probe being installed — unwired, DeviceProbe reports the GC heap
    /// (~100 MB) and this says no to everything. See ItApplication.
    /// </remarks>
    static bool Fits(ModelEntry m, DeviceProbe probe) =>
        m.MinRamGb <= probe.UsableRamGb + 0.0001 &&
        (probe.StorageFreeGb <= 0 || m.MinStorageGb <= probe.StorageFreeGb + 0.0001);
}
