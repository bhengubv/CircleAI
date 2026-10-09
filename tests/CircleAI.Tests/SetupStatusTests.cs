// SetupStatusTests.cs
//
// "How is Circle AI Service not aware of its total engine component setup status?"
//
// It was not, and this is the shape of the blindness. Every question the service
// asked about its own components was a boolean, asked about one component, at the
// moment that component was needed:
//
//   is the model present?            no        (true, and useless)
//   is there an abandoned download?  no        (the sweep had already eaten it)
//   how big is the model?            22.8 GB   (the full size, as if untouched)
//
// Not one of them asked "what have I got, in total, and how far through is it" -
// so a Circle OS device sat at 1.1 GB of 22.8 GB for three days, answering from a
// 2 B model, with every readout agreeing it was fine.
//
// These rebuild that device's directory against the REAL registry and assert the
// service can now say what it holds. A fake registry would hide the metadata drift
// this class of bug lives in.

using System;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class SetupStatusTests : IDisposable
{
    private readonly string _store =
        Path.Combine(Path.GetTempPath(), "circleai-setup-" + Guid.NewGuid().ToString("N"));

    public SetupStatusTests() => Directory.CreateDirectory(_store);

    public void Dispose()
    {
        try { Directory.Delete(_store, recursive: true); } catch { /* a temp dir */ }
    }

    /// <summary>The biggest chat bundle in the catalogue — the one that cannot land in one go.</summary>
    private static ModelEntry Biggest(ModelRegistryService registry) =>
        registry.AllModels
            .Where(m => m.Modality == ModelModality.Chat
                     && m.BundleFiles is { Count: > 1 })
            .OrderByDescending(m => m.TotalBytes)
            .First();

    private static void Sparse(string path, long length)
    {
        SparseStore.Write(path, length);
    }

    [Fact]
    public void A_download_that_stopped_halfway_is_reported_as_halfway()
    {
        // THE DEVICE, REBUILT. Every bundle file down except the weight, which is
        // the 21.3 GB one and the whole wait.
        var registry = new ModelRegistryService();
        var model = Biggest(registry);
        var weight = model.BundleFiles!.OrderByDescending(f => f.SizeBytes).First();
        var dir = Path.Combine(_store, model.Name);

        long laid = 0;
        foreach (var f in model.BundleFiles!.Where(f => f.Name != weight.Name))
        {
            Sparse(Path.Combine(dir, f.Name), f.SizeBytes);
            laid += f.SizeBytes;
        }

        using var loader = new BundleModelLoader(_store, registry);

        Assert.False(loader.ModelPresent(model.Name));
        Assert.True(loader.ModelPartial(model.Name), "a model with bytes down read as untouched");

        var (have, need) = loader.Progress(model.Name);
        Assert.Equal(laid, have);
        Assert.Equal(model.BundleFiles!.Sum(f => f.SizeBytes), need);
        Assert.True(have < need);
    }

    [Fact]
    public void The_bytes_still_in_flight_are_counted_too()
    {
        // WHAT PROGRESS FIRST MISSED. Only the FINISHED file names were looked at,
        // so a weight file two thirds fetched into llm.mnn.weight.tmp counted as
        // zero and a download nearly done looked like one never begun.
        var registry = new ModelRegistryService();
        var model = Biggest(registry);
        var weight = model.BundleFiles!.OrderByDescending(f => f.SizeBytes).First();
        var dir = Path.Combine(_store, model.Name);

        var temp = Path.Combine(dir, weight.Name + DownloadSidecar.TempSuffix);
        // Preallocated to the FULL size, as a segmented fetch does, with the marker
        // saying a third of it is real. Measuring the file would read 100%.
        Sparse(temp, weight.SizeBytes);
        var third = weight.SizeBytes / 3;
        File.WriteAllLines(DownloadSidecar.For(temp),
        [
            $"0,{third},{weight.SizeBytes - 1}",
        ]);

        using var loader = new BundleModelLoader(_store, registry);
        var (have, _) = loader.Progress(model.Name);

        Assert.Equal(third, have);
        Assert.True(loader.ModelPartial(model.Name));
    }

    [Fact]
    public void A_model_nobody_started_is_not_called_partial()
    {
        // THE DISTINCTION THAT WAS LOST, from the other side. Never started and
        // stopped halfway are different offers - begin, or carry on - and reporting
        // the first as the second would have the service resume a download its
        // owner never authorised.
        var registry = new ModelRegistryService();
        var model = Biggest(registry);
        Directory.CreateDirectory(Path.Combine(_store, model.Name));

        using var loader = new BundleModelLoader(_store, registry);

        Assert.False(loader.ModelPartial(model.Name));
        Assert.Equal(0, loader.Progress(model.Name).Have);
    }

    [Fact]
    public void An_unknown_model_answers_zero_rather_than_throwing()
    {
        // Asked on a loading screen, about whatever directories happen to be there.
        using var loader = new BundleModelLoader(_store, new ModelRegistryService());

        Assert.Equal((0L, 0L), loader.Progress("Something-Nobody-Ships"));
        Assert.False(loader.ModelPartial("Something-Nobody-Ships"));
    }

    // ── The readout itself ────────────────────────────────────────────────────

    [Fact]
    public void The_setup_line_names_the_component_and_how_far_through_it_is()
    {
        var registry = new ModelRegistryService();
        var model = Biggest(registry);
        var weight = model.BundleFiles!.OrderByDescending(f => f.SizeBytes).First();
        var dir = Path.Combine(_store, model.Name);

        foreach (var f in model.BundleFiles!.Where(f => f.Name != weight.Name))
            Sparse(Path.Combine(dir, f.Name), f.SizeBytes);

        using var loader = new BundleModelLoader(_store, registry);
        var line = ModelSetup.Describe(loader);

        Assert.Contains(model.Name, line);
        Assert.DoesNotContain("nothing yet", line);
        Assert.DoesNotContain("complete", line);
        Assert.Matches(@"\d+% \(\d+ of \d+\)", line);
    }

    [Fact]
    public void An_empty_store_says_so_rather_than_inventing_a_model_called_nothing()
    {
        // THE SENTENCE WAS BEING PARSED BACK INTO DATA. The list came from splitting
        // the human log line on commas, so an empty store produced a model named
        // "nothing" and a missing one a model named "nothing - the store does not
        // exist". Both went through the status line and the resume sweep.
        using var loader = new BundleModelLoader(_store, new ModelRegistryService());

        Assert.Empty(loader.InstalledIds());
        Assert.Equal("no engine components on this device", ModelSetup.Describe(loader));
        Assert.Empty(ModelSetup.Unfinished(loader));
    }

    [Fact]
    public void A_store_that_does_not_exist_is_not_a_model_either()
    {
        using var loader = new BundleModelLoader(
            Path.Combine(_store, "never-made"), new ModelRegistryService());

        Assert.Empty(loader.InstalledIds());
        Assert.Equal("no engine components on this device", ModelSetup.Describe(loader));
    }

    [Fact]
    public void The_resume_sweep_says_what_it_holds_even_when_there_is_nothing_to_do()
    {
        // A STATUS LINE THAT ONLY APPEARS WHEN THERE IS NEWS IS NOT A STATUS LINE.
        // The device in the broken state logged nothing at all and read as healthy.
        var said = new System.Collections.Generic.List<string>();
        using var loader = new BundleModelLoader(_store, new ModelRegistryService());

        ModelSetup.ResumeUnfinishedAsync(loader, said.Add).GetAwaiter().GetResult();

        Assert.Single(said);
        Assert.StartsWith("setup: ", said[0]);
    }

    [Fact]
    public void A_model_nobody_started_is_never_resumed()
    {
        // THIS IS STILL TRUE OF **RESUME**, AND NO LONGER TRUE OF THE PRODUCT.
        // The rule used to be the whole of it: bytes on disk are the owner's prior
        // instruction, zero bytes is not, and a service that fetches 22.8 GB
        // nobody asked for is a worse failure than one that fetches nothing.
        //
        // On 2026-10-09 the owner decided the other way - every ability on from
        // first run - and ModelSetup.FetchNotStartedAsync now begins downloads at
        // zero bytes. ResumeUnfinishedAsync is unchanged and must stay that way,
        // because the two do different jobs and only one of them is safe to call
        // in any circumstance. FirstRunFetchStartsByItselfTests covers the other.
        var registry = new ModelRegistryService();
        Directory.CreateDirectory(Path.Combine(_store, Biggest(registry).Name));
        var said = new System.Collections.Generic.List<string>();
        using var loader = new BundleModelLoader(_store, registry);

        ModelSetup.ResumeUnfinishedAsync(loader, said.Add).GetAwaiter().GetResult();

        Assert.DoesNotContain(said, s => s.StartsWith("carrying on", StringComparison.Ordinal));
    }

    [Fact]
    public void The_biggest_unfinished_download_is_carried_on_first()
    {
        // Gigabytes on a phone, one at a time: the one that is actually blocking a
        // better answer goes first rather than last.
        var registry = new ModelRegistryService();
        var models = registry.AllModels
            .Where(m => m.Modality == ModelModality.Chat && m.BundleFiles is { Count: > 1 })
            .OrderByDescending(m => m.TotalBytes)
            .Take(2)
            .ToList();
        Assert.Equal(2, models.Count);

        foreach (var m in models)
        {
            var smallest = m.BundleFiles!.OrderBy(f => f.SizeBytes).First();
            Sparse(Path.Combine(_store, m.Name, smallest.Name), smallest.SizeBytes);
        }

        using var loader = new BundleModelLoader(_store, registry);
        var order = ModelSetup.Unfinished(loader).Select(u => u.Name).ToList();

        Assert.Equal(models[0].Name, order[0]);
    }
}
