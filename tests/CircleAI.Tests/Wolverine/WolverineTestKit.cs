// WolverineTestKit.cs
//
// What the S6 tests share: the collection every test that touches Wolverine's process-wide
// state runs in (SelfHeal's switches, BrainTuning's folder, the HealthEvents stream, the kernel
// switch, SelfModel.Current), a clock that moves when told, and fakes for the loop's parts.

using System.Collections.Concurrent;
using CircleAI.Assistant;
using CircleAI.Core.Logging;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Inference;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.SelfKnowledge;
using Xunit;

namespace CircleAI.Tests;

/// <summary>Tests that touch Wolverine's process-wide state run one at a time.</summary>
[CollectionDefinition(nameof(WolverineCollection), DisableParallelization = true)]
public sealed class WolverineCollection;

/// <summary>A clock that stands still until a test moves it.</summary>
internal sealed class WolverineClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The loop's policy, set by the test.</summary>
internal sealed class WolverinePolicy : ISelfHealPolicy
{
    public bool Analyse = true, AutoFix = true, Ready = true;

    public Task<bool> ShouldAnalyseAsync(CancellationToken ct = default) => Task.FromResult(Analyse);

    public Task<bool> MayAutoFixAsync(CancellationToken ct = default) => Task.FromResult(AutoFix);

    public Task<bool> BrainReadyAsync(CancellationToken ct = default) => Task.FromResult(Ready);
}

/// <summary>An analyst that counts its calls and keeps the failure it was handed.</summary>
internal sealed class WolverineAnalyst : IFailureAnalyst
{
    public int Calls;
    public FailureContext? Last;
    public HealingVerdict Next = HealingVerdict.NeedsAHuman("nothing set");

    public Task<HealingVerdict> AnalyseAsync(FailureContext failure, CancellationToken ct = default)
    {
        Calls++;
        Last = failure;
        return Task.FromResult(Next);
    }
}

/// <summary>A fix that says what it was told to say.</summary>
internal sealed class WolverineFix(string category, bool result = true) : ISafeFix
{
    public string Category { get; } = category;
    public bool Result = result;
    public int Calls;

    public Task<bool> TryFixAsync(FailureContext failure, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

/// <summary>A healing log in a list.</summary>
internal sealed class WolverineLog : IHealingLog
{
    public readonly ConcurrentQueue<HealingRecord> Records = new();

    public Task AppendAsync(HealingRecord record, CancellationToken ct = default)
    {
        Records.Enqueue(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HealingRecord>> RecentAsync(int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HealingRecord>>(Records.Reverse().Take(limit).ToList());

    public Task MarkHandledAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult(Records.Count(r => r.At >= since));
}

/// <summary>Events in the shapes the service raises them.</summary>
internal static class WolverineEvents
{
    public const string Model = "Ternary-Bonsai-2-2B";

    public static HealthEvent Engine(string errorType, CircleAI.Assistant.Lifecycle.WorkPhase phase, string? model = Model) =>
        HealthEvents.Of(HealingRules.EngineArea, $"{phase} {model}", errorType, EngineFaults.Site(phase),
            "exit-reason=# signal=#; the inference engine crashed", "Phone, Phone tier", model);

    public static HealthEvent Download(string errorType, string model = Model) =>
        HealthEvents.Of(HealingRules.ModelsArea, "download " + model, errorType, "ModelDownloadService.EnsureBundleCoreAsync",
            "SHA-# mismatch for bundle file '…' of model '…'.", subject: model);

    /// <summary>Puts every static Wolverine touches back as a fresh process has it.</summary>
    public static void Reset()
    {
        SelfHeal.Clear();
        SelfHeal.Say = null;
        BrainTuning.Directory = null;
        DownloadMirrors.PreferAlternate = null;
        KernelChoice.ScalarOnly = false;
        RiskySteps.Directory = null;
        SelfModel.Current = null;
        DeviceWolverine.Current = null;
    }

    public static string TempDir(string what)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"circleai-{what}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
