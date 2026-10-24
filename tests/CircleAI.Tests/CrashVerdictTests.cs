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
    public void One_death_while_loading_is_a_strike_not_a_refusal()
    {
        // THE RULE THAT REFUSED QWEN3.5-0.8B ON THE P30. A surviving breadcrumb
        // proves the process died inside a load, not that the model killed it, and
        // that phone's OS kills this service for memory all the time.
        _cat.Upsert(Model("big", 16));

        var refused = CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        Assert.Null(refused);
        Assert.False(_cat.Assessment("big")!.Refused);
        Assert.Contains(_said, s => s.Contains("1 of 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_deaths_in_a_row_refuse_the_model_it_names()
    {
        // A model that genuinely cannot load dies every time, so it is still
        // refused - one attempt later than the old rule.
        _cat.Upsert(Model("big", 16));

        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);
        var refused = CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        Assert.Equal("big", refused);
        Assert.True(_cat.Assessment("big")!.Refused);
        Assert.Equal(CrashVerdict.Reason, _cat.Assessment("big")!.RefusedReason);
        Assert.Contains(_said, s => s.Contains("stopped this phone", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_load_between_two_deaths_means_they_were_not_in_a_row()
    {
        _cat.Upsert(Model("big", 16));

        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);
        CrashVerdict.Loaded(_cat, "big");
        var refused = CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        Assert.Null(refused);
        Assert.False(_cat.Assessment("big")!.Refused);
    }

    [Fact]
    public void A_death_the_platform_says_was_not_the_models_is_not_counted()
    {
        // The person force-stopped it, or the app was updated mid-load. Neither
        // says anything about the model, so neither is a strike - not even one.
        _cat.Upsert(Model("big", 16));

        Assert.Null(CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add, killedByTheSystem: true));
        Assert.Null(CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add, killedByTheSystem: true));
        Assert.Null(CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add));

        Assert.False(_cat.Assessment("big")!.Refused, "only the last death counted, and one is a strike");
        Assert.Contains(_said, s => s.Contains("not held against the model", StringComparison.Ordinal));
    }

    [Fact]
    public void A_death_loading_a_model_the_catalogue_does_not_hold_refuses_nothing_and_says_so_truthfully()
    {
        var refused = CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("nowhere"), _said.Add);

        Assert.Null(refused);
        Assert.DoesNotContain(_said, s => s.Contains("0 of", StringComparison.Ordinal));
        Assert.Contains(_said, s => s.Contains("not in this phone's catalogue", StringComparison.Ordinal));
    }

    [Fact]
    public void The_old_rules_refusals_come_back_on_one_strike()
    {
        // WHAT IS ON THE P30 NOW: refused under the old rule, with its reason and no
        // strikes, because the column did not exist when the refusal was written.
        _cat.Upsert(Model("qwen35", 8));
        _cat.Refuse("qwen35", CrashVerdict.Reason);

        Assert.Equal(1, CrashVerdict.Reconsider(_cat, _said.Add));
        Assert.False(_cat.Assessment("qwen35")!.Refused);

        // ONE strike, not a pardon: the next death refuses it again.
        Assert.Equal("qwen35", CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("qwen35"), _said.Add));
        Assert.True(_cat.Assessment("qwen35")!.Refused);
    }

    [Fact]
    public void Reconsidering_never_touches_a_refusal_the_two_strike_rule_made()
    {
        // Safe to run on every start: a refusal earned under the new rule carries
        // its strikes, so it can never be mistaken for one of the old rule's.
        _cat.Upsert(Model("big", 16));
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        Assert.Equal(0, CrashVerdict.Reconsider(_cat, _said.Add));
        Assert.True(_cat.Assessment("big")!.Refused);
    }

    [Fact]
    public void Reconsidering_never_touches_a_refusal_for_another_reason()
    {
        _cat.Upsert(Model("big", 16));
        _cat.Refuse("big", "a person said no");

        Assert.Equal(0, CrashVerdict.Reconsider(_cat, _said.Add));
        Assert.Equal("a person said no", _cat.Assessment("big")!.RefusedReason);
    }

    [Fact]
    public void A_pardon_is_a_fresh_start_and_takes_the_strikes_with_it()
    {
        _cat.Upsert(Model("big", 16));
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        _cat.Pardon("big");
        var refused = CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        Assert.Null(refused);
        Assert.False(_cat.Assessment("big")!.Refused);
    }

    [Fact]
    public void Strikes_survive_a_resync_from_the_registry()
    {
        // ReSync runs on every start. A strike it wiped would make "in a row"
        // impossible on any phone that restarts between loads - which is every one.
        _cat.Upsert(Model("big", 16));
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add);

        Assert.True(_cat.ReSync(Model("big", 17)));

        Assert.Equal("big", CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add));
    }

    [Fact]
    public void Two_unexplained_load_deaths_on_api_29_never_refuse_the_brain()
    {
        // THE P30'S FRESH INSTALL ON 2026-10-23, as the supervisor judges it: iAware took
        // both processes for memory mid-load, twice, and API 29 kept no record of why.
        _cat.Upsert(Model("qwen35", 8));
        var facts = new CircleAI.Assistant.Lifecycle.ExitFacts(29, CircleAI.Assistant.Lifecycle.WorkPhase.Load,
            "qwen35", DateTimeOffset.UtcNow, BootChanged: false, PackageUpdated: false);

        for (var i = 0; i < 2; i++)
        {
            var failure = CircleAI.Assistant.Lifecycle.FailureClassifier.Classify(facts);
            CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("qwen35"), _said.Add,
                                killedByTheSystem: !failure.CountsAgainstModel);
        }

        Assert.False(_cat.Assessment("qwen35")!.Refused, "the only brain was refused for the phone's memory");
        Assert.False(CrashVerdict.IsRefused(_cat, "qwen35"));
    }

    [Fact]
    public void Two_crashes_with_evidence_still_refuse_the_model()
    {
        _cat.Upsert(Model("big", 16));
        var facts = new CircleAI.Assistant.Lifecycle.ExitFacts(29, CircleAI.Assistant.Lifecycle.WorkPhase.Load,
            "big", DateTimeOffset.UtcNow, WorkerDiedAlone: true, CrashBufferShowsSignal: true);

        for (var i = 0; i < 2; i++)
        {
            var failure = CircleAI.Assistant.Lifecycle.FailureClassifier.Classify(facts);
            CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("big"), _said.Add,
                                killedByTheSystem: !failure.CountsAgainstModel);
        }

        Assert.True(_cat.Assessment("big")!.Refused);
    }

    [Fact]
    public void A_refusal_the_unexplained_death_rule_made_comes_back_on_one_strike()
    {
        // WHAT A PHONE UPDATING FROM 3.9.1 MAY HOLD: refused with its two strikes, which
        // Reconsider - matching only refusals with none - never looks at.
        _cat.Upsert(Model("qwen35", 8));
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("qwen35"), _said.Add);
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("qwen35"), _said.Add);
        Assert.Equal(0, CrashVerdict.Reconsider(_cat, _said.Add));

        Assert.Equal(1, CrashVerdict.ReconsiderAmbiguousRefusals(_cat, _said.Add));
        Assert.False(_cat.Assessment("qwen35")!.Refused);

        // ONE strike, not a pardon: a crash that counts refuses it again.
        Assert.Equal("qwen35", CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb("qwen35"), _said.Add));
    }

    [Fact]
    public void Reconsidering_unexplained_refusals_never_touches_another_reason()
    {
        _cat.Upsert(Model("big", 16));
        _cat.Refuse("big", "a person said no");

        Assert.Equal(0, CrashVerdict.ReconsiderAmbiguousRefusals(_cat, _said.Add));
        Assert.Equal("a person said no", _cat.Assessment("big")!.RefusedReason);
        Assert.Equal(0, CrashVerdict.ReconsiderAmbiguousRefusals(null, _said.Add));
    }

    [Fact]
    public void Reconsidering_that_could_not_finish_says_so_and_does_not_count_as_done()
    {
        // -1, NOT 0: "nothing to heal" and "could not get to it" must not look the same,
        // or the run-once marker is written and the only brain stays refused for good.
        var gone = new SqliteModelCatalog("Data Source=:memory:");
        gone.Dispose();

        Assert.Equal(-1, CrashVerdict.ReconsiderAmbiguousRefusals(gone, _said.Add));
        Assert.Contains(_said, s => s.Contains("could not reconsider", StringComparison.Ordinal));
    }

    [Fact]
    public void The_supervisor_holds_a_stopped_load_off_and_heals_old_refusals_once()
    {
        // Android-only, so guarded at the source like the rest of the supervisor's wiring.
        var supervisor = System.IO.File.ReadAllText(System.IO.Path.Combine(
            Root(), "src", "CircleAIService", "Droid", "Inference", "InferenceSupervisor.cs"));

        var load    = supervisor.IndexOf("private async Task LoadAsync(string? forced)", StringComparison.Ordinal);
        var gate    = supervisor.IndexOf("if (HeldOff(model) is { } held) throw HoldOffNow(", load, StringComparison.Ordinal);
        var crumb   = supervisor.IndexOf("BeginRisky(_stateDir", load, StringComparison.Ordinal);
        var loaded  = supervisor.IndexOf("CrashVerdict.Loaded(_catalogue(), model);", load, StringComparison.Ordinal);
        var cleared = supervisor.IndexOf("ClearSetback(model);", load, StringComparison.Ordinal);
        Assert.True(load >= 0 && gate > load, "no load waits out a hold-off");
        Assert.True(crumb > gate, "the hold-off must come before the breadcrumb");
        Assert.True(cleared > loaded, "a clean load must clear the hold-off");

        var recover    = supervisor.IndexOf("public void RecoverFromPreviousRun()", StringComparison.Ordinal);
        var reconsider = supervisor.IndexOf("CrashVerdict.Reconsider(", recover, StringComparison.Ordinal);
        var once       = supervisor.IndexOf("ReconsiderAmbiguousRefusalsOnce();", recover, StringComparison.Ordinal);
        var record     = supervisor.IndexOf("CrashVerdict.Record(", recover, StringComparison.Ordinal);
        var setback    = supervisor.IndexOf("RecordSetback(model)", recover, StringComparison.Ordinal);
        Assert.True(recover >= 0 && reconsider > recover && once > reconsider && record > once,
                    "old refusals must be healed before the last death is recorded");
        Assert.True(setback > record, "a death the main process did not survive must hold the next load off");
        Assert.Contains("reconsidered-unexplained-deaths.txt", supervisor);
        Assert.Contains("ReconsiderAmbiguousRefusals(catalogue, line => _log(line)) < 0) return;", supervisor);

        // A worker's death keeps its breadcrumb until its verdict is written.
        var died    = supervisor.IndexOf("catch (WorkerDiedException died)", load, StringComparison.Ordinal);
        var fail    = supervisor.IndexOf("Fail(failure, model);", died, StringComparison.Ordinal);
        var endRisk = supervisor.IndexOf("DeviceDiagnostics.EndRisky(_stateDir);", fail, StringComparison.Ordinal);
        var nextCatch = supervisor.IndexOf("catch (BrainWorkerException ex)", died, StringComparison.Ordinal);
        Assert.True(died > load && fail > died && endRisk > fail && endRisk < nextCatch,
                    "the breadcrumb must outlive the classification of the death");

        // A model only told to wait is not validated - and rejected for a week - meanwhile.
        var validate = supervisor.IndexOf("public async Task<bool> ValidateCandidateIfDueAsync(", StringComparison.Ordinal);
        var skip     = supervisor.IndexOf("if (HeldOff(candidate) is not null) return false;", validate, StringComparison.Ordinal);
        var release  = supervisor.IndexOf("await ReleaseAsync(ct)", validate, StringComparison.Ordinal);
        Assert.True(validate >= 0 && skip > validate && skip < release, "a held-off candidate must not be validated");
    }

    [Fact]
    public void The_service_that_loads_the_models_actually_runs_the_rule()
    {
        // THIS REPO'S SIGNATURE DEFECT is a rule that is written, tested and never
        // called. The service is an Android Service that cannot be built off a
        // device, so the wiring is guarded at the source: Reconsider BEFORE Record
        // (or the old refusals are never converted), the platform's word passed in,
        // and a clean load clearing the strikes (or they are not "in a row").
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            Root(), "src", "CircleAI", "Device", "CircleNeuronService.cs"));

        var reconsider = source.IndexOf("CrashVerdict.Reconsider(", StringComparison.Ordinal);
        var record     = source.IndexOf("CrashVerdict.Record(", StringComparison.Ordinal);
        var ready      = source.IndexOf("\"brain ready\"", StringComparison.Ordinal);
        var loaded     = source.IndexOf("CrashVerdict.Loaded(Catalogue, wanted)", StringComparison.Ordinal);

        Assert.True(reconsider >= 0, "Reconsider is never called");
        Assert.True(record > reconsider, "Reconsider must run before Record");
        Assert.Contains("killedByTheSystem: LastDeathWasTheSystems(context)", source);
        Assert.True(loaded > ready, "a clean load must clear the strikes");

        // And the host runs it, before the catalogue bootstrap whose housekeeping
        // reads the verdicts - see CircleNeuronService.RecordLastLoad.
        var host = System.IO.File.ReadAllText(System.IO.Path.Combine(
            Root(), "src", "CircleAIService", "ServiceApplication.cs"));
        Assert.Contains("_supervisor?.RecoverFromPreviousRun();", host);

        // AND THE SUPERVISOR - which owns the brain in its own process - runs the rule by
        // KIND: a strike only when the classified failure counts against the model.
        var supervisor = System.IO.File.ReadAllText(System.IO.Path.Combine(
            Root(), "src", "CircleAIService", "Droid", "Inference", "InferenceSupervisor.cs"));
        Assert.Contains("CrashVerdict.Reconsider(", supervisor);
        Assert.Contains("killedByTheSystem: !failure.CountsAgainstModel", supervisor);
        Assert.Contains("CrashVerdict.Loaded(_catalogue(), model)", supervisor);
    }

    [Fact]
    public void A_self_exit_is_not_excused_as_the_platforms()
    {
        // Nothing in this codebase exits its own process, so EXIT_SELF during a
        // load is a native library calling exit() - the model's failure. Excusing
        // it would let that model loop for ever, exactly like a low-memory kill.
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            Root(), "src", "CircleAI", "Device", "CircleNeuronService.cs"));

        Assert.DoesNotContain("ExitSelf = 1", source);
        Assert.DoesNotContain("LowMemory = 3", source);
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "src")))
            dir = System.IO.Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void A_catalogue_that_throws_costs_a_verdict_not_the_brain()
    {
        Assert.Null(CrashVerdict.Record(new Throwing(), CrashVerdict.Breadcrumb("big"), _said.Add));
        Assert.Equal(0, CrashVerdict.Reconsider(new Throwing(), _said.Add));
        CrashVerdict.Loaded(new Throwing(), "big");
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

    private sealed class Throwing : IModelCatalog
    {
        private static InvalidOperationException Boom() => new("database is locked");

        public void Upsert(ModelEntry entry) => throw Boom();
        public bool ReSync(ModelEntry entry) => throw Boom();
        public void SetAssessment(string id, bool compatible, double rank) => throw Boom();
        public void Refuse(string id, string reason) => throw Boom();
        public void Pardon(string id) => throw Boom();
        public int Strike(string id) => throw Boom();
        public void RecordLoad(string id) => throw Boom();
        public int ForgiveFirstStrikes(string reason) => throw Boom();
        public AssessedModel? Assessment(string id) => throw Boom();
        public IReadOnlyList<AssessedModel> AllAssessed() => throw Boom();
        public void SetInstalled(string id, bool installed) => throw Boom();
        public ModelEntry? Get(string id) => throw Boom();
        public bool IsInstalled(string id) => throw Boom();
        public IReadOnlyList<ModelEntry> All() => throw Boom();
        public IReadOnlyList<ModelEntry> Compatible(ModelModality modality) => throw Boom();
        public ModelEntry? Best(ModelModality modality) => throw Boom();
        public int Count() => throw Boom();
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
                SparseStore.Write(System.IO.Path.Combine(dir, f.Name), f.SizeBytes);
            }
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
