// DeviceCapabilityReportTests.cs
//
// "Best we can do with your device is this." These pin that the answer is COMPUTED
// from the device rather than promised, that it is honest about absences, and that
// the floor is the measured P30 Lite rather than a number somebody liked.

using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class DeviceCapabilityReportTests
{
    // The reference device, measured: docs/HARDWARE_FINDINGS_HUAWEI_P30.md.
    // Huawei P30 Lite (MAR-LX1M): 3.6 GB RAM, ~1.4 GB free, 38 GB storage, no GPU.
    static DeviceProbe P30() => new(
            RamAvailableBytes: 1_400_000_000,
            StorageFreeBytes:  20_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online)
        { RamTotalBytes = 3_600_000_000, StorageTotalBytes = 38_000_000_000 };

    // A 2015-era handset: below the floor on both counts.
    static DeviceProbe TooOld() => new(
            RamAvailableBytes: 600_000_000,
            StorageFreeBytes:  4_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          4,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online)
        { RamTotalBytes = 1_500_000_000, StorageTotalBytes = 16_000_000_000 };

    // Two selectors, because the product has two: chat and vision go through the
    // chat selector (the speech one refuses them outright), the rest through speech.
    static ModelRegistryService Registry() => new();
    static (IModelSelector chat, ISpeechModelSelector speech) Selectors()
    {
        var reg = Registry();
        return (new DeviceAwareModelSelector(reg), new SpeechModelSelector(reg));
    }

    static DeviceCapabilityReport Report(DeviceProbe probe)
    {
        var (chat, speech) = Selectors();
        return DeviceCapability.For(chat, speech, probe);
    }

    [Fact]
    public void The_reference_device_clears_the_floor_and_a_seven_year_older_one_does_not()
    {
        // The floor is a decision about WHO THE PRODUCT IS FOR, not a convenience. A
        // P30 Lite is itself a seven-year-old handset; supporting down to it is the
        // point, because that is what a great many people actually hold.
        Assert.True(DeviceCapability.MeetsFloor(P30()));
        Assert.False(DeviceCapability.MeetsFloor(TooOld()));
    }

    [Fact]
    public void A_device_that_reports_nothing_is_given_the_benefit_of_the_doubt()
    {
        // A desktop build reports 0 for both totals — only a platform head can read
        // them on Android. Refusing on missing data would turn away every device the
        // SDK runs on outside a phone, which is not what the floor is for.
        var unknown = new DeviceProbe(
            RamAvailableBytes: 8_000_000_000,
            StorageFreeBytes:  100_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Active,
            Connectivity:      Connectivity.Online);
        Assert.True(DeviceCapability.MeetsFloor(unknown));
    }

    [Fact]
    public void The_report_says_what_the_P30_can_do_and_what_it_cannot()
    {
        var report = Report(P30());

        // Every capability asked about gets an answer — none is silently dropped.
        Assert.Equal(DeviceCapability.Asked.Count, report.Lines.Count);
        Assert.All(report.Lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Plan.Reason)));

        // Image generation is out on this phone BY MEASUREMENT, not opinion: the
        // cheapest catalogued option needs 8.5 GB of RAM against ~1.19 GB usable.
        // It is also the one capability with no built-in to fall back to.
        var image = report.Lines.Single(l => l.Modality == ModelModality.ImageGeneration);
        Assert.False(image.Available);
    }

    [Fact]
    public void Disk_is_not_what_limits_the_reference_device()
    {
        // RAM IS PER MODEL, NEVER A SUM, because models are tagged in and out as
        // needed — the resident slot manager keeps the generalist warm and hot-swaps
        // the specialist. So only DISK has to hold the whole set, and on this phone
        // it is not close. If this ever tightens, the constraint has moved and the
        // story about what a cheap handset can carry changes with it.
        var report = Report(P30());
        Assert.True(report.FitsBudget,
            $"offered set is {report.TotalBytes / 1e9:F2} GB against a {report.BudgetBytes / 1e9:F2} GB budget");
        Assert.True(report.TotalBytes < report.BudgetBytes / 2,
            $"{report.TotalBytes / 1e9:F2} GB of {report.BudgetBytes / 1e9:F2} GB — disk has become the constraint");
    }

    [Fact]
    public void The_disclaimer_leads_with_what_the_device_can_do()
    {
        // A list of absences is not a product. The sentence a person reads first must
        // be what their phone CAN do; what it cannot comes after, once.
        var text = DeviceCapability.Disclaimer(Report(P30()));

        Assert.StartsWith("On this device CircleAI can ", text);
        Assert.Contains("Not yet:", text);
        Assert.Contains("draw a picture", text);          // named plainly, not "ImageGeneration"
        Assert.DoesNotContain("ImageGeneration", text);   // no enum names leak to a person
        Assert.DoesNotContain("sorry", text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_device_below_the_floor_is_told_plainly_and_not_offered_a_half_product()
    {
        var text = DeviceCapability.Disclaimer(Report(TooOld()));

        Assert.Contains("below what CircleAI supports", text);
        Assert.Contains("3.5 GB", text);      // says WHAT it needs, so the sentence is actionable
        Assert.Contains("32 GB", text);
    }

    [Fact]
    public void A_catalogued_model_with_no_engine_is_not_reported_as_missing()
    {
        // THREE STATES, NOT TWO. Qwen-Image is catalogued, verified and on disk; what
        // it lacks is a runtime. The device was reporting "no image-generation model
        // is catalogued", which sends a person looking for a model they already have
        // and a developer looking in the wrong place. Fixed by asking whether a row
        // EXISTS separately from whether this build can open it.
        var image = Report(P30()).Lines.Single(l => l.Modality == ModelModality.ImageGeneration);

        Assert.False(image.Available);
        Assert.Contains("catalogued", image.Plan.Reason);
        Assert.Contains("no engine", image.Plan.Reason);
        Assert.DoesNotContain("no image-generation model is catalogued", image.Plan.Reason);
    }

    [Fact]
    public void The_selectors_and_the_assessor_agree_about_vision_on_the_P30()
    {
        // THE DISAGREEMENT THIS CLOSES. The catalogue said Qwen2.5-VL-3B runs on a
        // P30 -- 2.74 GB of weights paged, ~1.16 GB resident -- while the selector
        // that actually picks a vision model said SmolVLM-256M, because it compared a
        // declared 3.9 GB against ~1.19 GB usable and stopped there. Same phone, same
        // catalogue, two answers, and the one a person saw was the wrong one.
        //
        // Six places asked "does this fit": two in the chat selector, three in the
        // speech selector, one in the assessor, and only the assessor knew about
        // paging. They now all call ModelFit.
        var probe = P30();
        using var registry = Registry();
        var vl = registry.AllModels.Single(m => m.Name == "Qwen2.5-VL-3B-Instruct-MNN");

        // the shared rule says it fits, with paging
        Assert.True(ModelFit.Fits(vl, probe, mmapAllowed: true));
        Assert.True(ModelFit.WillMmap(vl, probe.UsableRamGb, mmapAllowed: true));

        // and the assessor, which scores the catalogue, agrees
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        cat.Upsert(vl);
        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true).Assess(probe);
        Assert.NotNull(cat.Best(ModelModality.Vision));

        // eagerly it would NOT fit -- so the test cannot pass for a trivial reason
        Assert.False(ModelFit.Fits(vl, probe, mmapAllowed: false));
    }

}
