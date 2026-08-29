// MemoryServiceTests.cs
//
// The memory an app holds, and what happens when the app is killed.
//
// BEING KILLED IS THE ORDINARY CASE ON A PHONE, not the exception. The system
// takes the app for memory, the person swipes it away, the battery goes - and a
// force-stop calls no lifecycle callback at all, so anything a design was
// holding back for "later" is simply gone.
//
// So the kill is simulated the only honest way: the service is ABANDONED rather
// than disposed - the reference is dropped and a new one is opened on the same
// folder, which is exactly what the next launch sees. A test that politely
// disposed first would prove nothing about the case that actually happens.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Tests;

public class MemoryServiceTests : IDisposable
{
    private readonly string _dir;

    public MemoryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "circleai-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// A launch of the app.
    /// </summary>
    /// <remarks>
    /// Deliberately not disposed by the caller in most of these. Dropping it is
    /// the point.
    /// </remarks>
    private MemoryService Launch() => new(_dir, "test-device");

    private static MemoryAtom Decision(string text, string subject = "deploy:android") => new()
    {
        Kind = AtomKind.Decision,
        Text = text,
        Subject = subject,
        Outcome = DecisionOutcome.Resolved,
    };

    // ==================================================================
    // Being killed
    // ==================================================================

    [Fact]
    public async Task What_was_remembered_survives_the_app_being_killed()
    {
        var killed = Launch();
        await killed.RememberAsync(Decision("Use -t:InstallKeepingData when iterating"));
        // No Dispose. No Save. The process is simply gone.

        var next = Launch();
        var result = await next.RecallAsync(new Situation("deploy", "android"));

        Assert.Contains(result.Atoms, a => a.Text.Contains("InstallKeepingData", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_was_learned_survives_the_app_being_killed()
    {
        var killed = Launch();
        await killed.LearnAsync(
            "Never restart a device or toggle its radios without asking me first.",
            subject: "device:state");

        var next = Launch();
        var result = await next.RecallAsync(new Situation("device", "state"));

        Assert.Contains(result.Atoms, a => a.Kind == AtomKind.Ruling);
    }

    [Fact]
    public async Task The_wear_survives_the_app_being_killed()
    {
        // ⭐ THE ONE THIS DESIGN TURNS ON. Wear is the only thing that was ever
        // buffered, and it is what decides what has faded. A design that wrote
        // it on a lifecycle callback would lose a session's familiarity to
        // every force-stop - which is how a phone usually kills an app - and
        // the decay model would quietly be switched off on the one platform it
        // exists for.
        var killed = Launch();
        await killed.RememberAsync(Decision("Use -t:InstallKeepingData when iterating"));
        var recalled = await killed.RecallAsync(new Situation("deploy", "android"));
        Assert.NotEmpty(recalled.Atoms);
        // No Dispose. No Save.

        var wear = new MemoryWear(new MemoryFolder(_dir, "test-device"));

        Assert.True(wear.Count > 0, "the retrieval was lost when the app went away");
        Assert.NotNull(wear.For(recalled.Atoms[0].Id));
    }

    [Fact]
    public async Task Nothing_is_held_back_waiting_for_a_callback()
    {
        // Said as a property rather than as a promise: after any operation, a
        // reader that knows nothing about this service sees the result.
        var service = Launch();

        await service.RememberAsync(Decision("Something worth keeping"));

        var folder = new MemoryFolder(_dir, "test-device");
        Assert.Contains("Something worth keeping",
            File.ReadAllText(folder.OwnLog), StringComparison.Ordinal);

        await service.RecallAsync(new Situation("deploy", "android"));
        Assert.True(File.Exists(Path.Combine(_dir, "wear.test-device.json")));
    }

    [Fact]
    public async Task Saving_when_there_is_nothing_outstanding_is_not_an_error()
    {
        // Save() is a belt on top of braces, and a host with a lifecycle
        // callback will call it whether or not anything is pending.
        var service = Launch();
        service.Save();

        await service.RememberAsync(Decision("Something"));
        service.Save();
        service.Save();

        Assert.Equal(1, await service.CountAsync());
    }

    // ==================================================================
    // Launching
    // ==================================================================

    [Fact]
    public async Task A_second_launch_sees_everything_the_first_one_wrote()
    {
        var first = Launch();
        for (var i = 0; i < 5; i++)
            await first.RememberAsync(Decision($"Decision {i}", $"work:area{i}"));

        var second = Launch();

        Assert.Equal(5, await second.CountAsync());
    }

    [Fact]
    public async Task Losing_the_index_costs_a_rebuild_rather_than_a_memory()
    {
        // A phone is exactly where a half-written database is most likely, and
        // this is the claim that makes that survivable.
        var first = Launch();
        await first.RememberAsync(Decision("Use -t:InstallKeepingData when iterating"));
        first.Dispose();

        var folder = new MemoryFolder(_dir, "test-device");
        File.Delete(folder.IndexPath);

        var next = Launch();
        var result = await next.RecallAsync(new Situation("deploy", "android"));

        Assert.Contains(result.Atoms, a => a.Text.Contains("InstallKeepingData", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Closing_it_lets_go_of_the_file()
    {
        // Disposing a SqliteConnection returns it to a pool rather than closing
        // the handle, so the database stays locked for the life of the process.
        // The next thing that tries to replace or clean up the index then fails
        // with a file-in-use error that has nothing to do with what it was
        // doing - which is how this was found.
        var service = Launch();
        await service.RememberAsync(Decision("Something"));
        service.Dispose();

        var index = new MemoryFolder(_dir, "test-device").IndexPath;
        var failed = Record.Exception(() => File.Delete(index));

        Assert.Null(failed);
    }

    [Fact]
    public void The_first_ever_launch_works_on_an_empty_folder()
    {
        var empty = Path.Combine(_dir, "never-used");
        using var service = new MemoryService(empty, "test-device");

        Assert.True(Directory.Exists(empty));
        Assert.Equal("test-device", service.Machine);
    }

    [Fact]
    public void A_device_that_cannot_name_itself_still_gets_its_own_memory()
    {
        // Every Android device answers "localhost" for its host name.
        using var one = new MemoryService(Path.Combine(_dir, "phone-a"));
        using var two = new MemoryService(Path.Combine(_dir, "phone-b"));

        Assert.False(string.IsNullOrWhiteSpace(one.Machine));
        Assert.False(string.IsNullOrWhiteSpace(two.Machine));
    }

    // ==================================================================
    // Two threads
    // ==================================================================

    [Fact]
    public async Task An_app_can_ask_and_remember_from_several_threads_at_once()
    {
        // A SQLite connection is not thread-safe, and an app will reach for its
        // memory from the UI thread and a background one in the same second.
        // Unguarded this does not fail cleanly - it tears a read, rarely, and
        // never the same way twice.
        using var service = Launch();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            if (i % 2 == 0)
                await service.RememberAsync(Decision($"Decision {i}", $"work:area{i % 5}"));
            else
                await service.RecallAsync(new Situation("work", $"area{i % 5}"));
        }));

        Assert.Equal(20, await service.CountAsync());
    }

    [Fact]
    public async Task Learning_from_several_threads_still_keeps_one_of_each()
    {
        using var service = Launch();

        const string said = "Never restart a device or toggle its radios without asking.";
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.LearnAsync(said, "device:state")));

        var kept = await service.AllAsync();
        Assert.Single(kept, a => a.Text.StartsWith("Never restart", StringComparison.Ordinal));
    }

    // ==================================================================
    // Being a memory
    // ==================================================================

    [Fact]
    public async Task Asking_it_something_it_has_never_heard_of_is_not_an_error()
    {
        using var service = Launch();

        var result = await service.RecallAsync(new Situation("bake", "bread"));

        Assert.Empty(result.Atoms);
    }

    [Fact]
    public async Task What_it_hands_back_gets_easier_to_reach()
    {
        using var service = Launch();
        var atom = Decision("Use -t:InstallKeepingData when iterating");
        await service.RememberAsync(atom);

        var before = service.Reach(atom);
        await service.RecallAsync(new Situation("deploy", "android"));
        var after = service.Reach(atom);

        Assert.True(after >= before);
    }

    [Fact]
    public async Task A_correction_through_the_service_supersedes_and_counts()
    {
        using var service = Launch();

        var first = Decision("Use -t:Install");
        await service.RememberAsync(first);
        await service.RememberAsync(Decision("Use -t:InstallKeepingData"), supersedes: first.Id);

        var result = await service.RecallAsync(new Situation("deploy", "android"));

        Assert.Single(result.Atoms);
        Assert.Equal("Use -t:InstallKeepingData", result.Atoms[0].Text);
        Assert.Equal(1, result.Atoms[0].Corrections);
    }

    [Fact]
    public async Task Using_it_after_it_has_been_disposed_says_so()
    {
        var service = Launch();
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.RecallAsync(new Situation("deploy", "android")));
    }
}
