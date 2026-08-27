// MemorySyncTests.cs
//
// Three machines, one memory.
//
// The requirement is plain: a Linux box, a Windows box and a Mac all have to
// see the same store, and they sync through git. That rules out committing a
// SQLite file - git cannot merge a binary blob, and the only resolutions it
// offers destroy half the memory. So the durable thing is one append-only log
// per machine, and these tests are about whether that actually holds together.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Tests;

public class MemorySyncTests : IDisposable
{
    private readonly string _dir;

    public MemorySyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "circleai-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing a run */ }
    }

    private MemoryFolder Folder(string machine) => new(_dir, machine);

    private static MemoryAtom Decision(string challenge, string text, string subject,
        DecisionOutcome outcome = DecisionOutcome.Resolved) => new()
    {
        Kind = AtomKind.Decision,
        Challenge = challenge,
        Text = text,
        Subject = subject,
        Outcome = outcome,
    };

    // ------------------------------------------------------------------
    // The layout
    // ------------------------------------------------------------------

    [Fact]
    public void Each_machine_writes_only_its_own_file()
    {
        // ONE WRITER PER FILE IS THE WHOLE SYNC STRATEGY. Git merges files that
        // only ever grew apart without asking anybody anything.
        var windows = Folder("windows-desk");
        var mac = Folder("mac-build");

        Assert.NotEqual(windows.OwnLog, mac.OwnLog);
        Assert.EndsWith("atoms.windows-desk.jsonl", windows.OwnLog);
        Assert.EndsWith("atoms.mac-build.jsonl", mac.OwnLog);
    }

    [Fact]
    public void The_index_is_never_the_thing_that_syncs()
    {
        // A committed .db is a conflict with no correct resolution. The
        // gitignore is part of the design, not housekeeping.
        var folder = Folder("linux-box");
        folder.EnsureGitIgnore();

        var ignore = File.ReadAllText(Path.Combine(_dir, ".gitignore"));
        Assert.Contains("index.*.db", ignore);
        Assert.DoesNotContain("atoms", ignore);
    }

    [Fact]
    public void Two_machines_do_not_share_an_index_file()
    {
        // A synced drive would otherwise put two machines inside one SQLite
        // file, and a half-written index looks like an answer.
        Assert.NotEqual(Folder("windows-desk").IndexPath, Folder("mac-build").IndexPath);
    }

    // ------------------------------------------------------------------
    // Replay
    // ------------------------------------------------------------------

    [Fact]
    public async Task What_one_machine_records_another_machine_recalls()
    {
        var windows = new MemorySync(Folder("windows-desk"));
        using (var store = new SqliteAtomStore("Data Source=:memory:"))
        {
            await windows.RecordAsync(store, Decision(
                "-t:Install wiped the models again",
                "Use -t:InstallKeepingData to iterate",
                "deploy:android"));
        }

        // A different machine, a fresh index, nothing but the shared folder.
        var mac = new MemorySync(Folder("mac-build"));
        using var macStore = new SqliteAtomStore("Data Source=:memory:");
        var report = await mac.RebuildAsync(macStore);

        Assert.Equal(1, report.Records);
        Assert.Equal(1, report.Current);

        var recall = new Recall(macStore);
        var result = await recall.ForAsync(new Situation("deploy", "android"));

        Assert.Contains(result.Atoms, a => a.Text.Contains("InstallKeepingData", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_correction_on_one_machine_supersedes_a_decision_from_another()
    {
        // The case that decides whether this is a memory or three memories.
        var windows = new MemorySync(Folder("windows-desk"));
        var wrong = Decision("How do we deploy?", "Use -t:Install", "deploy:android");

        using (var store = new SqliteAtomStore("Data Source=:memory:"))
            await windows.RecordAsync(store, wrong);

        var mac = new MemorySync(Folder("mac-build"));
        var right = Decision("How do we deploy?", "Use -t:InstallKeepingData", "deploy:android");

        using (var store = new SqliteAtomStore("Data Source=:memory:"))
        {
            await mac.RebuildAsync(store);
            await mac.RecordAsync(store, right, supersedes: wrong.Id);
        }

        // A third machine, seeing both logs for the first time, must land on
        // the correction rather than on whichever file it happened to read last.
        var linux = new MemorySync(Folder("linux-box"));
        using var third = new SqliteAtomStore("Data Source=:memory:");
        var report = await linux.RebuildAsync(third);

        Assert.Equal(2, report.Records);
        Assert.Equal(1, report.Current);

        // Two, not three: Machines counts who has WRITTEN, and this box has
        // only read so far. A machine that has contributed nothing yet is not
        // part of the memory, however many logs it can see.
        Assert.Equal(2, report.Machines);

        var recall = new Recall(third);
        var result = await recall.ForAsync(new Situation("deploy", "android"));

        Assert.Single(result.Atoms);
        Assert.Contains("InstallKeepingData", result.Atoms[0].Text, StringComparison.Ordinal);

        // And the correction is counted, because that is what makes it rank.
        Assert.Equal(1, result.Atoms[0].Corrections);
    }

    [Fact]
    public async Task Rebuilding_twice_changes_nothing()
    {
        // Replay runs at startup and after every pull. If it were not
        // idempotent the store would grow a duplicate on every sync.
        var sync = new MemorySync(Folder("windows-desk"));
        using var store = new SqliteAtomStore("Data Source=:memory:");

        await sync.RecordAsync(store, Decision("A", "B", "subject"));

        var first = await sync.RebuildAsync(store);
        var second = await sync.RebuildAsync(store);

        Assert.Equal(first.Current, second.Current);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact]
    public async Task The_index_can_be_thrown_away_without_losing_a_memory()
    {
        // The whole point of the log being the durable half.
        var sync = new MemorySync(Folder("windows-desk"));

        using (var store = new SqliteAtomStore("Data Source=:memory:"))
            await sync.RecordAsync(store, Decision(
                "Models keep disappearing", "InstallKeepingData preserves them", "deploy:android"));

        using var rebuilt = new SqliteAtomStore("Data Source=:memory:");
        var report = await sync.RebuildAsync(rebuilt);

        Assert.Equal(1, report.Current);
    }

    [Fact]
    public async Task A_line_somebody_mangled_by_hand_does_not_cost_the_rest()
    {
        // The logs are text so a person can read and fix them, which means a
        // person will sometimes break one. One bad line must not be the end of
        // somebody's memory.
        var folder = Folder("windows-desk");
        var sync = new MemorySync(folder);

        using (var store = new SqliteAtomStore("Data Source=:memory:"))
        {
            await sync.RecordAsync(store, Decision("first", "kept", "s"));
            await sync.RecordAsync(store, Decision("second", "also kept", "s"));
        }

        File.AppendAllText(folder.OwnLog, "{ this is not json at all\n");

        using var store2 = new SqliteAtomStore("Data Source=:memory:");
        var report = await sync.RebuildAsync(store2);

        Assert.Equal(2, report.Records);
    }

    [Fact]
    public async Task Replay_orders_by_time_not_by_which_file_was_read_first()
    {
        // Machines are read in filename order, so a Mac correction would land
        // before a Windows decision if order came from the filesystem. It must
        // come from the clock.
        var mac = new MemorySync(Folder("mac-build"));       // sorts before "windows"
        var windows = new MemorySync(Folder("windows-desk"));

        var earlier = Decision("q", "the original answer", "s");
        using (var s = new SqliteAtomStore("Data Source=:memory:"))
            await windows.RecordAsync(s, earlier);

        await Task.Delay(20);

        var later = Decision("q", "the corrected answer", "s");
        using (var s = new SqliteAtomStore("Data Source=:memory:"))
            await mac.RecordAsync(s, later, supersedes: earlier.Id);

        using var store = new SqliteAtomStore("Data Source=:memory:");
        await new MemorySync(Folder("linux-box")).RebuildAsync(store);

        var current = await store.MatchAsync(new Situation("s"));
        Assert.Single(current);
        Assert.Equal("the corrected answer", current[0].Text);
    }

    // ------------------------------------------------------------------
    // The format
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_log_is_something_a_person_can_read()
    {
        // Half the reason it is text. If this stops being true the sovereignty
        // argument goes with it.
        var folder = Folder("windows-desk");
        var sync = new MemorySync(folder);

        using (var store = new SqliteAtomStore("Data Source=:memory:"))
            await sync.RecordAsync(store, Decision(
                "-t:Install wiped 817 MB of models",
                "Use -t:InstallKeepingData when iterating",
                "deploy:android"));

        var line = File.ReadAllLines(folder.OwnLog).Single();

        Assert.Contains("\"challenge\":\"-t:Install wiped 817 MB of models\"", line, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"Resolved\"", line, StringComparison.Ordinal);
        Assert.Contains("\"machine\":\"windows-desk\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_correction_points_backwards_because_a_log_cannot_be_edited()
    {
        // The structural consequence of append-only: the new line names what it
        // replaces, and the forward pointer is derived on replay.
        var folder = Folder("windows-desk");
        var sync = new MemorySync(folder);
        var first = Decision("q", "first answer", "s");

        using (var store = new SqliteAtomStore("Data Source=:memory:"))
        {
            await sync.RecordAsync(store, first);
            await sync.RecordAsync(store, Decision("q", "second answer", "s"), supersedes: first.Id);
        }

        var lines = File.ReadAllLines(folder.OwnLog);

        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("supersedes", lines[0], StringComparison.Ordinal);
        Assert.Contains($"\"supersedes\":\"{first.Id:N}\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_name_is_safe_in_a_filename_on_all_three()
    {
        var folder = new MemoryFolder(_dir, "Windows / Dev Box (main)");
        Assert.DoesNotContain('/', folder.Machine);
        Assert.DoesNotContain(' ', folder.Machine);
        Assert.DoesNotContain('(', folder.Machine);
    }

    [Fact]
    public void The_default_machine_name_says_which_platform_it_is()
    {
        // A Windows box and a Mac that happen to share a hostname would
        // otherwise write to the same file, which is the merge problem back.
        var name = MemoryFolder.DefaultMachineName();
        Assert.Contains('-', name);
        Assert.True(
            name.StartsWith("windows-") || name.StartsWith("mac-") ||
            name.StartsWith("linux-") || name.StartsWith("android-") || name.StartsWith("other-"),
            $"unexpected machine name: {name}");
    }
}
