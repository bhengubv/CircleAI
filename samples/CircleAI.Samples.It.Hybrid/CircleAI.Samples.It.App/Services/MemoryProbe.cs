// MemoryProbe.cs
//
// Does the memory work on the phone it was built for?
//
// EVERYTHING ABOUT THIS DESIGN ASSUMES A HANDSET and none of it had ever run on
// one. SQLite is the default because it is the only option on a phone; FTS5 is
// the primary search mechanism with LIKE as the floor; the log is text so it can
// be read and synced. All of that was written on a desktop, where the probe in
// SqliteAtomStore has only ever taken one branch.
//
// SO THIS RUNS THE WHOLE LOOP IN THE APP'S OWN DATA DIRECTORY: open a store,
// find out whether FTS5 is really there, record, recall, write a log line,
// throw the index away, rebuild from the log, and read what a conversation
// said. It reports what it found and how long each part took, because "it
// works" and "it works in 40 ms on a P30 Lite" are different claims.
//
// IT LOGS NATIVELY. ILogger reaches nothing on Android - AddDebug() writes to a
// sink that is not logcat - so a probe reporting through it would be silent on
// exactly the device it exists to test.
//
// IT NEVER THROWS INTO THE APP. A diagnostic that can take the launch down with
// it is worse than no diagnostic.

using System.Diagnostics;
using CircleAI.Memory;

namespace CircleAI.Samples.It.App.Services;

/// <summary>Runs the memory end to end on the device and says what happened.</summary>
public static class MemoryProbe
{
    /// <summary>The logcat tag. `adb logcat -s CircleMemory` and nothing else.</summary>
    public const string Tag = "CircleMemory";

    /// <summary>
    /// Run it, off the UI thread, swallowing everything.
    /// </summary>
    /// <remarks>
    /// TASK.RUN RATHER THAN A BARE ASYNC CALL. An async method runs
    /// synchronously until its first real await, and the store's constructor -
    /// which opens SQLite and builds a schema - has none. Called directly it
    /// would do that work on the UI thread during launch.
    /// </remarks>
    public static void Start(string dataDirectory) =>
        Task.Run(async () =>
        {
            try { await RunAsync(dataDirectory); }
            catch (Exception ex) { Write($"FAILED {ex.GetType().Name}: {ex.Message}"); }
        });

    private static async Task RunAsync(string dataDirectory)
    {
        var root = Path.Combine(dataDirectory, "memory-probe");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

        var folder = new MemoryFolder(root);
        var sync = new MemorySync(folder);
        var total = Stopwatch.StartNew();

        Write("---- memory on this device ----");
        Write($"machine  {folder.Machine}");
        Write($"folder   {folder.Path}");

        // 1. Does the store open at all, and is search a real index or the floor?
        var opening = Stopwatch.StartNew();
        using var store = new SqliteAtomStore(folder.IndexConnectionString);
        opening.Stop();

        Write($"open     {opening.ElapsedMilliseconds} ms");
        Write(store.FullTextAvailable
            ? "search   FTS5 - the real index"
            : "search   LIKE - no FTS5 in this build of SQLite");

        // 2. Record, through the log, the way the CLI and the app both do.
        var writing = Stopwatch.StartNew();
        var decision = new MemoryAtom
        {
            Kind = AtomKind.Decision,
            Text = "Use -t:InstallKeepingData when iterating",
            Subject = "deploy:android",
            Challenge = "-t:Install wiped 817 MB of models on every deploy",
            Outcome = DecisionOutcome.Resolved,
        };
        await sync.RecordAsync(store, decision);

        await sync.RecordAsync(store, new MemoryAtom
        {
            Kind = AtomKind.Ruling,
            Text = "Never restart a device or toggle its radios without asking",
            Subject = "device:state",
        });

        await sync.RecordAsync(store, new MemoryAtom
        {
            Kind = AtomKind.Relationship,
            Text = "Blunt. Hates being asked the same thing twice.",
            Subject = "style",
        });
        writing.Stop();
        Write($"record   3 atoms in {writing.ElapsedMilliseconds} ms");

        // 3. THE QUESTION THIS ALL EXISTS TO ANSWER, timed on the handset that
        //    is the benchmark. Anything an agent has to wait for before acting
        //    has to be cheap or it stops being asked.
        var asking = Stopwatch.StartNew();
        var result = await new Recall(store).ForAsync(new Situation("deploy", "android"));
        asking.Stop();

        Write($"recall   {result.Atoms.Count} of {result.Considered} in {asking.ElapsedMilliseconds} ms");
        foreach (var atom in result.Atoms)
            Write($"  - {atom.Kind.ToString().ToLowerInvariant()}: {atom.Text}");
        foreach (var tone in result.Tone)
            Write($"  ~ {tone.Text}");

        var recalled = result.Atoms.Any(a => a.Text.Contains("InstallKeepingData", StringComparison.Ordinal));
        Write(recalled ? "  OK  the decision came back" : "  BAD the decision did not come back");

        // 4. A correction, which is how the count that drives ranking survives.
        await sync.RecordAsync(store, new MemoryAtom
        {
            Kind = AtomKind.Decision,
            Text = "Set EmbedAssembliesIntoApk and use InstallKeepingData",
            Subject = "deploy:android",
        }, supersedes: decision.Id);

        var corrected = await store.MatchAsync(new Situation("deploy", "android"));
        var counted = corrected.FirstOrDefault(a => a.Corrections > 0);
        Write(counted is not null
            ? $"  OK  the correction counted ({counted.Corrections}x)"
            : "  BAD the correction did not count");

        // 5. THE INDEX IS DISPOSABLE. Delete it and rebuild from the text, on
        //    the device - because a phone is exactly where a half-written
        //    database is most likely, and the claim is that it costs a rebuild
        //    rather than a memory.
        var lines = File.ReadAllLines(folder.OwnLog).Length;
        var replaying = Stopwatch.StartNew();
        using (var fresh = new SqliteAtomStore("Data Source=:memory:"))
        {
            var report = await sync.RebuildAsync(fresh);
            replaying.Stop();

            Write($"replay   {report.Records} lines -> {report.Current} current in {replaying.ElapsedMilliseconds} ms");
            var survived = await new Recall(fresh).ForAsync(new Situation("deploy", "android"));
            Write(survived.Atoms.Any(a => a.Text.Contains("EmbedAssembliesIntoApk", StringComparison.Ordinal))
                ? "  OK  the memory survived losing the index"
                : "  BAD the memory did not survive losing the index");
        }

        // 6. Is the log actually readable, on a device, in a language the
        //    catalogue is full of?
        await sync.RecordAsync(store, new MemoryAtom
        {
            Kind = AtomKind.Decision,
            Text = "Sisebenzisa isiZulu kuqala",
            Subject = "language:zu",
        });
        var text = await File.ReadAllTextAsync(folder.OwnLog);
        Write(text.Contains("Sisebenzisa isiZulu kuqala", StringComparison.Ordinal) && !text.Contains(@"\u", StringComparison.Ordinal)
            ? "  OK  isiZulu is readable in the log"
            : "  BAD the log escaped what somebody typed");

        // 7. Does it fill itself here, with no model loaded?
        var reading = Stopwatch.StartNew();
        var kept = 0;
        var learned = await new AtomLearner().LearnAsync(
            new[]
            {
                new EpisodicMemoryEntry
                {
                    UserText = "Never deploy with -t:Install, it wipes the models. " +
                               "The adb push approach did not work, it silently wrote nothing.",
                    AssistantText = "Understood.",
                },
            },
            (atom, ct) => { kept++; return sync.RecordAsync(store, atom, ct: ct); },
            await store.AllAsync(limit: 500),
            subject: "deploy:android");
        reading.Stop();

        Write($"learn    {learned.Considered} spotted, {kept} kept in {reading.ElapsedMilliseconds} ms");
        Write(kept == 2 ? "  OK  it fills itself with no model" : $"  BAD expected 2, kept {kept}");

        // 8. DOES IT FORGET? A working set that only grows is the thing a phone
        //    cannot afford, and this is where the cost is real: every atom that
        //    never fades is one more the ranking has to consider on every recall.
        await ForgetsAsync(folder);

        // 9. DOES IT SURVIVE THE APP BEING KILLED? Everything above happens
        //    inside one launch, and a store that only holds while the process
        //    does is not a memory. This folder is deliberately never deleted:
        //    each launch reads what the last one left, then adds to it.
        await SurvivedAsync(dataDirectory);

        Write($"---- {lines} log lines, {total.ElapsedMilliseconds} ms total ----");

        // Leave nothing behind: this is a diagnostic, not the app's memory.
        try { Directory.Delete(root, recursive: true); } catch { /* next run deletes it */ }
    }

    /// <summary>Whether what goes unused actually goes quiet here.</summary>
    private static async Task ForgetsAsync(MemoryFolder folder)
    {
        var wear = new MemoryWear(folder);
        var now = DateTimeOffset.UtcNow;

        // Said against the constant rather than in literal days: this asserted
        // four months when the initial stability was fourteen days, and the
        // moment that was derived properly - ninety days - four months was no
        // longer long enough to have faded.
        var longGone = Forgetting.InitialStabilityDays * 4.5;

        var stale = new MemoryAtom
        {
            Kind = AtomKind.Decision,
            Text = "Something decided one afternoon a long time ago",
            Subject = "deploy:android",
            RecordedAtUtc = now.AddDays(-longGone),
        };
        var rule = new MemoryAtom
        {
            Kind = AtomKind.Ruling,
            Text = "Never restart a device without asking",
            Subject = "device:state",
            RecordedAtUtc = now.AddDays(-longGone * 2),
        };

        // Invariant: the phone's locale writes 0,400 for four tenths, and a
        // number in a report that could be read as four hundred is worse than
        // no number. Third time this has bitten - the log, the command, here.
        Write($"forget   after {longGone:0} days untouched: decision {Number(wear.Reach(stale, now))}, " +
              $"standing rule {Number(wear.Reach(rule, now))}");

        Write(wear.Faded(stale, now)
            ? "  OK  what went unused has gone quiet"
            : "  BAD a four-month-old decision is still being volunteered");

        Write(!wear.Faded(rule, now)
            ? "  OK  the standing rule did not go quiet after a year"
            : "  BAD a rule faded, which is the failure this store exists to prevent");

        // Reaching for it puts it back - recognition restoring access.
        wear.Retrieved(stale, now);
        Write(!wear.Faded(stale, now)
            ? "  OK  asking for it brought it back"
            : "  BAD it stayed gone after being asked for");

        // And the wear survives the app dying, which is the whole reason it is
        // a file rather than something held in the store.
        wear.Flush();
        var reopened = new MemoryWear(folder);
        Write(reopened.For(stale.Id) is not null
            ? $"  OK  wear persisted ({reopened.Count} atoms worn)"
            : "  BAD the wear did not survive being written down");

        await Task.CompletedTask;
    }

    /// <summary>What the previous launches left behind.</summary>
    private static async Task SurvivedAsync(string dataDirectory)
    {
        var folder = new MemoryFolder(Path.Combine(dataDirectory, "memory-kept"));
        var sync = new MemorySync(folder);

        using var store = new SqliteAtomStore("Data Source=:memory:");
        var report = await sync.RebuildAsync(store);

        if (report.Records == 0)
        {
            Write("survive  first launch - nothing to remember yet");
        }
        else
        {
            var back = await new Recall(store).ForAsync(new Situation("app", "launch"));
            Write($"survive  {report.Records} from earlier launches, {back.Atoms.Count} recalled");
            Write(back.Any
                ? $"  OK  still here after the app was killed: {back.Atoms[0].Text}"
                : "  BAD the log is there and nothing came back");
        }

        await sync.RecordAsync(store, new MemoryAtom
        {
            Kind = AtomKind.Fact,
            Text = $"Launch {report.Records + 1} on this phone",
            Subject = "app:launch",
        });
    }

    /// <summary>A number that reads the same on every phone.</summary>
    private static string Number(double value) =>
        value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    private static void Write(string line)
    {
#if ANDROID
        Android.Util.Log.Info(Tag, line);
#else
        Console.WriteLine($"{Tag}: {line}");
#endif
    }
}
