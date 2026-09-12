// ModelChoiceTests.cs
//
// The rule that decides which model a phone downloads, asserted for the first
// time.
//
// IT HAD NO TESTS BECAUSE NOTHING COULD REFERENCE IT. ModelChoice contains no
// Android - a registry, a bundle loader and a device probe, all of which
// multi-target net9 and net10 - but it sat in a net10.0-android library, and not
// one of the sixteen test projects can reference one of those. So the one rule
// that decides what a person is offered, and what their data is spent on, was
// the one rule nothing asserted.
//
// What that cost, on a handset with 1.4 GB of usable RAM: Settings offered
// Answering at 547 MB while the chat screen said it needed 22,797 MB. Same
// question, two rules, forty times apart, on one phone at one moment. A person
// reading 22.8 GB concludes the thing is not worth it and stops.
//
// These run against the REAL embedded registry deliberately. The class of bug
// they exist to catch is metadata drift, and a fake registry hides exactly that.

using System;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelChoiceTests : IDisposable
{
    /// <summary>An empty model store: a phone with nothing downloaded.</summary>
    private readonly string _empty =
        Path.Combine(Path.GetTempPath(), "circleai-choice-" + Guid.NewGuid().ToString("N"));

    public ModelChoiceTests() => Directory.CreateDirectory(_empty);

    public void Dispose()
    {
        try { Directory.Delete(_empty, recursive: true); } catch { /* a temp dir */ }
    }

    /// <summary>A synthetic device. RAM is what the fit gate actually keys on.</summary>
    private static DeviceProbe Device(double ramGb, double storageGb = 32) =>
        new(
            RamAvailableBytes: (long)(ramGb * 1024 * 1024 * 1024),
            StorageFreeBytes:  (long)(storageGb * 1024 * 1024 * 1024),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

    // ── Fits ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_model_bigger_than_the_phones_memory_does_not_fit()
    {
        var registry = new ModelRegistryService();
        var probe = Device(1.4);

        foreach (var m in registry.AllModels.Where(m => m.MinRamGb > 1.4001))
            Assert.False(ModelChoice.Fits(m, probe),
                $"{m.Name} wants {m.MinRamGb} GB and was accepted on a 1.4 GB phone");
    }

    [Fact]
    public void A_phone_that_cannot_measure_its_free_space_is_not_refused_everything()
    {
        // A device that cannot report free space returns 0, and refusing every
        // model on that basis would leave the app telling somebody with a working
        // phone that it can do nothing at all.
        var registry = new ModelRegistryService();
        var blind = Device(8, storageGb: 0);

        Assert.Contains(registry.AllModels, m => ModelChoice.Fits(m, blind));
    }

    // ── For: the 547 MB versus 22,797 MB defect ──────────────────────────────

    [Fact]
    public void A_cheap_phone_is_never_offered_a_model_it_cannot_hold()
    {
        // THE DEFECT, AS ONE ASSERTION. The chat screen took the highest quality
        // model in the catalogue without asking whether the handset could run it,
        // and said "Answering needs Qwen3.6-35B-A3B-MNN - 22797 MB" on a phone
        // with 1.4 GB of usable RAM.
        var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(_empty, registry);
        var probe = Device(1.4);

        var pick = ModelChoice.For(ModelModality.Chat, registry, loader, probe);

        Assert.NotNull(pick);
        Assert.True(ModelChoice.Fits(pick!, probe),
            $"{pick!.Name} wants {pick.MinRamGb} GB on a 1.4 GB handset");
    }

    [Fact]
    public void The_best_thing_that_fits_wins_rather_than_the_smallest()
    {
        // The gate must be live in BOTH directions. A rule that always returns
        // the smallest model passes the test above and is still wrong.
        var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(_empty, registry);
        var probe = Device(1.4);

        var pick = ModelChoice.For(ModelModality.Chat, registry, loader, probe);
        Assert.NotNull(pick);

        var better = registry.AllModels
            .Where(m => m.Modality == ModelModality.Chat
                     && ModelChoice.Fits(m, probe)
                     && m.QualityRank > pick!.QualityRank)
            .Select(m => m.Name)
            .ToList();

        Assert.True(better.Count == 0,
            "a better model also runs here and was passed over: " + string.Join(", ", better));
    }

    [Fact]
    public void Selection_scales_with_the_device()
    {
        var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(_empty, registry);

        var small = ModelChoice.For(ModelModality.Chat, registry, loader, Device(1.4));
        var big   = ModelChoice.For(ModelModality.Chat, registry, loader, Device(16, storageGb: 64));

        Assert.NotNull(small);
        Assert.NotNull(big);
        Assert.True(big!.QualityRank >= small!.QualityRank);
    }

    [Fact]
    public void Nothing_that_fits_is_null_rather_than_a_model_the_phone_cannot_load()
    {
        // Null is a real answer on a cheap phone and has to be said, rather than
        // papered over with something that will not load. The screen turns it
        // into "needs more memory", which is a different sentence from "not
        // ready yet" and must stay different.
        var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(_empty, registry);

        var pick = ModelChoice.For(ModelModality.Chat, registry, loader, Device(0.01, storageGb: 0.01));

        Assert.Null(pick);
    }

    // ── Installed ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ModelModality.Chat)]
    [InlineData(ModelModality.Tts)]
    [InlineData(ModelModality.Asr)]
    [InlineData(ModelModality.Vision)]
    [InlineData(ModelModality.WakeWord)]
    public void Nothing_is_installed_on_a_phone_with_an_empty_model_store(ModelModality modality)
    {
        // The census question, and the one the home screen asks three times on
        // the way to drawing itself. It used to go through ModelExists, which
        // hashes a 470 MB anchor file to answer it.
        //
        // The other half - "an installed model wins over a better one" - is not
        // asserted here and deliberately so: ModelPresent requires the anchor
        // file at its full catalogued length, and fabricating one means writing
        // hundreds of megabytes per run. That half is exercised on the phone.
        var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(_empty, registry);

        Assert.Null(ModelChoice.Installed(modality, registry, loader));
    }

    // ── AnyCatalogued: our gap versus their phone ────────────────────────────

    [Fact]
    public void Nothing_catalogued_and_nothing_that_fits_are_different_questions()
    {
        // "Needs more memory" tells a person their handset is the problem and
        // invites them to go and buy a better one. Saying that for a modality
        // nothing serves at all would be blaming their phone for our gap.
        var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(_empty, registry);
        var tiny = Device(0.01, storageGb: 0.01);

        // Chat is catalogued and will not fit: their phone.
        Assert.True(ModelChoice.AnyCatalogued(ModelModality.Chat, registry));
        Assert.Null(ModelChoice.For(ModelModality.Chat, registry, loader, tiny));
    }

    [Fact]
    public void Vision_is_catalogued_now_and_this_test_says_so_out_loud()
    {
        // It was not, for months, while the abilities screen offered a 311 MB
        // download for it. Two entries declare Vision today; if that ever goes
        // back to zero, the Seeing row silently becomes "not ready yet" again
        // and this is the line that says why.
        var registry = new ModelRegistryService();

        Assert.True(ModelChoice.AnyCatalogued(ModelModality.Vision, registry));
    }

    // ── Size ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(547_000_000L, "547 MB")]
    [InlineData(999_999_999L, "1000 MB")]
    [InlineData(1_000_000_000L, "1 GB")]
    public void A_size_reads_the_way_a_person_would_say_it(long bytes, string expected)
        => Assert.Equal(expected, ModelChoice.Size(bytes));

    [Fact]
    public void A_size_is_punctuated_in_the_readers_language()
    {
        // THE FIRST VERSION OF THIS TEST ASSERTED "1.4 GB" AND FAILED ON THIS
        // BOX. It was the test that was wrong: the machine is en-ZA, where the
        // decimal separator is a comma, and "1,4 GB" is what somebody in
        // Johannesburg should read. Size formats in the CURRENT culture on
        // purpose, and pinning a full stop into an assertion would have pinned
        // an American phone's punctuation onto every handset this is for.
        var sep = System.Globalization.CultureInfo.CurrentCulture
            .NumberFormat.NumberDecimalSeparator;

        Assert.Equal($"1{sep}4 GB", ModelChoice.Size(1_400_000_000L));
    }
}
