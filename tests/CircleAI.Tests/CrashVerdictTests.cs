// CrashVerdictTests.cs
//
// A NATIVE ABORT RUNS NO MANAGED HANDLER, so the only way the process can notice it
// killed itself is a mark left on disk before the risky call and removed after.
//
// Measured on a Circle OS device (Tensor G2, 7.6 GB) on 2026-10-04, loading a
// 22.8 GB MoE that declared 2.5 GB of RAM:
//
//   Process com.bhengubv.circleai.service (pid 28525) has died: signal 6 (Aborted)
//   lowmemorykiller: Kill 'com.android.printspooler' ... 'com.android.contacts'
//     ... 'com.android.camera2' ... 'za.co.circleos.settings'      (ten more)
//
// The catch around that load never fired. Status was never written. Nothing was
// logged. And the next launch, reading the same 2.5 GB, would pick the same model.
//
// The two breadcrumbs this codebase already wrote were "loading the model" and
// "generating a response (…)" - neither names a model, and "the model" must never
// be read AS one, or the first crash refuses a model called "the model" and the
// real one is picked again.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class CrashVerdictTests : IDisposable
{
    private readonly SqliteModelCatalog _cat = new("Data Source=:memory:");
    private readonly List<string> _said = [];

    public void Dispose() => _cat.Dispose();

    private static ModelEntry Model(string name, int rank) =>
        new(name, "1.0", "MNN-Q4")
        {
            Modality     = ModelModality.Chat,
            Engine       = ModelEngine.Mnn,
            TotalBytes   = 1_000_000_000,
            MinRamGb     = 1,
            MinStorageGb = 1,
            QualityRank  = rank,
            Capabilities = ["Default"],
        };

    [Fact]
    public void A_breadcrumb_round_trips_the_model_name()
    {
        var crumb = CrashVerdict.Breadcrumb("Qwen3.6-35B-A3B-MNN");

        Assert.Equal("Qwen3.6-35B-A3B-MNN", CrashVerdict.ModelFrom(crumb));
    }

    [Theory]
    [InlineData("loading the model")]            // the old breadcrumb: names no model
    [InlineData("generating a response (tool turn)")]
    [InlineData("loading ")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_does_not_name_a_model_names_no_model(string? crumb)
        => Assert.Null(CrashVerdict.ModelFrom(crumb));

    [Fact]
    public void A_surviving_breadcrumb_refuses_the_model_it_names()
    {
        _cat.Upsert(Model("big", 16));

        var refused = CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        Assert.Equal("big", refused);
        Assert.True(_cat.Assessment("big")!.Refused);
        Assert.Contains(_said, s => s.Contains("stopped this phone", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_run_refuses_nothing()
    {
        // EndRisky deleted the file, so there is nothing to read.
        _cat.Upsert(Model("big", 16));

        Assert.Null(CrashVerdict.Record(_cat, null, _said.Add));
        Assert.False(_cat.Assessment("big")!.Refused);
        Assert.Empty(_said);
    }

    [Fact]
    public void The_old_breadcrumb_does_not_refuse_a_model_called_the_model()
    {
        // THE TRAP. DeviceBrain has written "loading the model" since long before
        // any of this. Parsing that as an id would write a refusal for a model
        // called "the model" - harmless in itself, and it would mean the first real
        // crash was recorded against the wrong row while the model that actually
        // aborted stayed selectable.
        _cat.Upsert(Model("big", 16));

        Assert.Null(CrashVerdict.Record(_cat, "loading the model", _said.Add));
        Assert.Empty(_said);
    }

    [Fact]
    public void A_refused_model_reads_as_refused_and_everything_else_does_not()
    {
        _cat.Upsert(Model("big", 16));
        _cat.Upsert(Model("small", 9));
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        Assert.True(CrashVerdict.IsRefused(_cat, "big"));
        Assert.False(CrashVerdict.IsRefused(_cat, "small"));
        Assert.False(CrashVerdict.IsRefused(_cat, "never-heard-of-it"));
    }

    [Fact]
    public void No_catalogue_refuses_nothing_rather_than_throwing()
    {
        // A device whose catalogue could not be opened must select exactly as it did
        // before any of this existed, not fail to bring the brain up.
        Assert.False(CrashVerdict.IsRefused(null, "big"));
        Assert.Null(CrashVerdict.Record(null, CrashVerdict.Breadcrumb("big"), _said.Add));
    }

    [Fact]
    public void The_reason_is_in_words_a_person_can_hear()
    {
        // Voice is the primary surface, so this is read aloud. No model id, no
        // decimals, no "SIGABRT".
        Assert.DoesNotContain("SIG", CrashVerdict.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abort", CrashVerdict.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".", CrashVerdict.Reason);
        Assert.DoesNotContain("MNN", CrashVerdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installed_fallback_skips_what_this_phone_was_killed_by()
    {
        // The end-to-end shape, without a device: the loader's fallback is the route
        // that chose the fatal model, and it now takes the same refusal predicate the
        // service hands it. Asserted relatively rather than by name - which model
        // ranks top is the catalogue's business and will change.
        using var store = new TempStore();
        var registry = new ModelRegistryService();
        foreach (var m in registry.AllModels.Where(m => m.Modality == ModelModality.Chat))
            store.Install(m);

        using var loader = new BundleModelLoader(store.Path, registry);

        var best = loader.BestInstalledChatModel();
        Assert.NotNull(best);

        var instead = loader.BestInstalledChatModel(
            refused: id => string.Equals(id, best, StringComparison.Ordinal));

        Assert.NotNull(instead);
        Assert.NotEqual(best, instead);
    }

    [Fact]
    public void Refusing_everything_leaves_nothing_rather_than_a_bad_choice()
    {
        // The state the service has to say something about out loud: the model this
        // phone can run is not here. Better than handing the engine a model that
        // aborts it.
        using var store = new TempStore();
        var registry = new ModelRegistryService();
        foreach (var m in registry.AllModels.Where(m => m.Modality == ModelModality.Chat))
            store.Install(m);

        using var loader = new BundleModelLoader(store.Path, registry);

        Assert.Null(loader.BestInstalledChatModel(refused: _ => true));
    }

    private sealed class TempStore : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "circleai-crash-" + Guid.NewGuid().ToString("N"));

        public TempStore() => System.IO.Directory.CreateDirectory(Path);

        public void Install(ModelEntry m)
        {
            var dir = System.IO.Path.Combine(Path, m.Name);
            System.IO.Directory.CreateDirectory(dir);
            foreach (var f in m.BundleFiles!)
            {
                using var fs = new System.IO.FileStream(
                    System.IO.Path.Combine(dir, f.Name), System.IO.FileMode.Create);
                fs.SetLength(f.SizeBytes);
            }
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
