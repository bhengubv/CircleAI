// ModelRefusalTests.cs
//
// A PREDICTION AND A RESULT MUST NOT SHARE A COLUMN.
//
// `compatible` is recomputed from declared metadata every time the assessor runs.
// The declared metadata is what was wrong: Qwen3.6-35B-A3B-MNN carried
// MinRamGb = 2.5 against 22.8 GB of weights, and on a Tensor G2 with 7.6 GB it
// aborted the process with signal 6 and set lowmemorykiller on a dozen system apps
// - printspooler, contacts, camera2, calendar, za.co.circleos.settings.
//
// So a verdict written into `compatible` lasts exactly until the next launch, when
// the assessor reads the same 2.5 GB and says yes again. Forever. The only thing
// that knows the truth is the attempt, and the attempt has to be remembered
// somewhere the prediction cannot reach.
//
// These pin that: Refuse writes a column the assessor READS and never WRITES.

using System;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelRefusalTests : IDisposable
{
    private readonly SqliteModelCatalog _cat = new("Data Source=:memory:");

    public void Dispose() => _cat.Dispose();

    private static ModelEntry Model(string name, int rank, double ramGb, long bytes) =>
        new(name, "1.0", "MNN-Q4")
        {
            Modality     = ModelModality.Chat,
            Engine       = ModelEngine.Mnn,
            TotalBytes   = bytes,
            MinRamGb     = ramGb,
            MinStorageGb = 1,
            QualityRank  = rank,
            Capabilities = ["Default"],
        };

    private static DeviceProbe Phone(double ramGb = 8, double storageGb = 64) =>
        new(RamAvailableBytes: (long)(ramGb * 1_000_000_000),
            StorageFreeBytes:  (long)(storageGb * 1_000_000_000),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

    [Fact]
    public void A_refused_model_is_never_offered_again()
    {
        _cat.Upsert(Model("big", rank: 16, ramGb: 1, bytes: 1_000_000_000));
        _cat.Upsert(Model("small", rank: 9, ramGb: 1, bytes: 500_000_000));
        DeviceModelAssessor.MnnOnly(_cat).Assess(Phone());

        Assert.Equal("big", _cat.Best(ModelModality.Chat)?.Name);

        _cat.Refuse("big", "aborted on this phone");

        Assert.Equal("small", _cat.Best(ModelModality.Chat)?.Name);
        Assert.DoesNotContain("big", _cat.Compatible(ModelModality.Chat).Select(m => m.Name));
    }

    [Fact]
    public void A_refusal_survives_the_next_assessment()
    {
        // THE WHOLE POINT. The assessor runs on every launch and rewrites
        // `compatible` from the same declared numbers that got it wrong. If the
        // verdict lived there, the device would forget overnight.
        _cat.Upsert(Model("big", rank: 16, ramGb: 1, bytes: 1_000_000_000));
        _cat.Upsert(Model("small", rank: 9, ramGb: 1, bytes: 500_000_000));
        var assessor = DeviceModelAssessor.MnnOnly(_cat);
        assessor.Assess(Phone());

        _cat.Refuse("big", "aborted on this phone");
        assessor.Assess(Phone());        // a fresh launch
        assessor.Assess(Phone());        // and another

        Assert.Equal("small", _cat.Best(ModelModality.Chat)?.Name);
        Assert.Equal("aborted on this phone", _cat.Assessment("big")!.RefusedReason);
        Assert.False(_cat.Assessment("big")!.Compatible);
    }

    [Fact]
    public void The_reason_is_readable_because_somebody_has_to_be_told_it()
    {
        _cat.Upsert(Model("big", rank: 16, ramGb: 1, bytes: 1_000_000_000));
        DeviceModelAssessor.MnnOnly(_cat).Assess(Phone());
        _cat.Refuse("big", "aborted on this phone");

        var a = _cat.Assessment("big");

        Assert.NotNull(a);
        Assert.True(a!.Refused);
        Assert.False(a.Usable);
        Assert.Equal("aborted on this phone", a.RefusedReason);
    }

    [Fact]
    public void The_assessment_itself_is_readable_at_last()
    {
        // compatible, rank and assessed_at were written on every assessment and
        // projected by nothing - the column list stopped at mmap_resident_gb. So no
        // screen could say WHY a model won and no diagnostic could say the
        // assessment was stale.
        _cat.Upsert(Model("small", rank: 9, ramGb: 1, bytes: 500_000_000));
        _cat.SetInstalled("small", true);
        DeviceModelAssessor.MnnOnly(_cat).Assess(Phone());

        var a = _cat.Assessment("small");

        Assert.NotNull(a);
        Assert.True(a!.Compatible);
        Assert.True(a.Installed);
        Assert.True(a.Rank > 0, "rank was write-only and read back as zero");
        Assert.NotNull(a.AssessedAt);
        Assert.Null(a.RefusedReason);
        Assert.True(a.Usable);
    }

    [Fact]
    public void A_never_assessed_row_says_so_rather_than_guessing()
    {
        _cat.Upsert(Model("fresh", rank: 9, ramGb: 1, bytes: 500_000_000));

        var a = _cat.Assessment("fresh");

        Assert.NotNull(a);
        Assert.False(a!.Compatible);
        Assert.Null(a.AssessedAt);
        Assert.False(a.Usable);
    }

    [Fact]
    public void A_person_can_withdraw_the_refusal()
    {
        // Nothing retries on its own - a native abort during load is not transient,
        // and each attempt costs a dozen system apps. This is the way back.
        _cat.Upsert(Model("big", rank: 16, ramGb: 1, bytes: 1_000_000_000));
        var assessor = DeviceModelAssessor.MnnOnly(_cat);
        assessor.Assess(Phone());
        _cat.Refuse("big", "aborted on this phone");

        _cat.Pardon("big");
        assessor.Assess(Phone());

        Assert.Equal("big", _cat.Best(ModelModality.Chat)?.Name);
        Assert.Null(_cat.Assessment("big")!.RefusedReason);
    }

    [Fact]
    public void Re_pinned_bytes_clear_the_old_verdict()
    {
        // A refusal is about the bytes that aborted. A new version is different
        // bytes, and holding the old verdict against them would strand a model that
        // was fixed upstream.
        _cat.Upsert(Model("big", rank: 16, ramGb: 1, bytes: 1_000_000_000));
        _cat.Refuse("big", "aborted on this phone");

        _cat.Upsert(Model("big", rank: 16, ramGb: 1, bytes: 1_000_000_000) with { Version = "2.0" });

        Assert.Null(_cat.Assessment("big")!.RefusedReason);
    }

    [Fact]
    public void Refusing_something_that_is_not_there_is_not_an_error()
    {
        _cat.Refuse("nobody", "whatever");
        _cat.Pardon("nobody");
        Assert.Null(_cat.Assessment("nobody"));
    }

    [Fact]
    public void Every_row_can_be_listed_with_its_verdict()
    {
        _cat.Upsert(Model("big", rank: 16, ramGb: 1, bytes: 1_000_000_000));
        _cat.Upsert(Model("small", rank: 9, ramGb: 1, bytes: 500_000_000));
        DeviceModelAssessor.MnnOnly(_cat).Assess(Phone());
        _cat.Refuse("big", "aborted on this phone");

        var all = _cat.AllAssessed();

        Assert.Equal(2, all.Count);
        Assert.Single(all, a => a.Refused);
        Assert.Single(all, a => a.Usable);
    }
}
