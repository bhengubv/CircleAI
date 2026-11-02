// ManagedEngineSeamTests.cs
//
// The C# engine as the rest of the app reaches it (S2: one build site wired). It ships in every
// build, so for it the only question the catalogue gates ask is the pack: qwen35 in PTQ1_0 or PQ2_0
// opens, anything else is refused with the reason. AIService's build site makes a ManagedChatGenerator
// when the row - or the host - names the engine, and a question asked through AIService comes
// back through it with the persona as its system turn.
//
// The last two need the 2B on this machine (they say SKIPPED otherwise).

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class ManagedEngineSeamTests(ITestOutputHelper output)
{
    private static ModelEntry Bonsai(string name = "bonsai-2b", string quantization = "PTQ1_0", string architecture = "qwen35") =>
        new(name, "1.0", quantization)
        {
            Engine = ModelEngine.Managed,
            Architecture = architecture,
            Modality = ModelModality.Chat,
            TotalBytes = 429_183_456,
            MinStorageGb = 0.5,
            MinRamGb = 0.6,
            QualityRank = 9,
        };

    private static DeviceProbe Roomy() =>
        new(
            RamAvailableBytes: 6_000_000_000,
            StorageFreeBytes: 64_000_000_000,
            Gpu: GpuKind.None,
            CpuCores: 8,
            Thermal: ThermalClass.Active,
            Connectivity: Connectivity.Online)
        {
            RamTotalBytes = 8_000_000_000,
            StorageTotalBytes = 128_000_000_000,
        };

    [Fact]
    public void The_CSharp_engine_ships_in_every_build()
    {
        Assert.Contains(ModelEngine.Managed, DeviceModelAssessor.ShippedEngines());
    }

    [Fact]
    public void It_opens_qwen35_in_PTQ1_0_and_PQ2_0_and_says_why_it_will_not_open_anything_else()
    {
        Assert.Equal(ModelEngine.Managed, EngineChoice.Run(Bonsai()));
        Assert.Equal(ModelEngine.Managed, EngineChoice.Run(Bonsai(quantization: "PQ2_0")));

        // Q2_0 is the third packing on the model card; the plan skips it because the label names two layouts.
        var q2 = Bonsai(quantization: "Q2_0");
        Assert.Null(EngineChoice.Run(q2));
        Assert.Contains("does not read yet", EngineChoice.Explain(q2));
        Assert.Contains(ManagedChatGenerator.WhatItReads, EngineChoice.Explain(q2));

        var other = Bonsai(architecture: "llama");
        Assert.Null(EngineChoice.Run(other));
        Assert.Contains("llama", EngineChoice.Explain(other));
    }

    [Fact]
    public void The_assessor_offers_a_Bonsai_row_on_the_CSharp_engine_with_no_native_library_at_all()
    {
        // The llama.cpp half of the catalogue cannot be exercised off-device (GgufEngineSeamTests):
        // a GGUF row there needs a loaded native bridge. The C# engine needs nothing loaded.
        using var catalogue = new SqliteModelCatalog("Data Source=:memory:");
        catalogue.Upsert(Bonsai("bonsai-ptq1"));
        catalogue.Upsert(Bonsai("bonsai-pq2", quantization: "PQ2_0"));
        catalogue.Upsert(Bonsai("bonsai-q2", quantization: "Q2_0"));

        new DeviceModelAssessor(catalogue, DeviceModelAssessor.ShippedEngines(), mmapAllowed: false, maxModelBytes: 1e12)
            .Assess(Roomy());

        var compatible = catalogue.Compatible(ModelModality.Chat).Select(e => e.Name).ToList();
        Assert.Contains("bonsai-ptq1", compatible);
        Assert.Contains("bonsai-pq2", compatible);
        Assert.DoesNotContain("bonsai-q2", compatible);
    }

    [Fact]
    public async Task AIServices_build_site_makes_the_CSharp_engine_when_the_host_names_it()
    {
        if (Bonsai2Model.TwoB() is not { } path)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        await using var service = new AIService(new AIOptions { ModelPath = path, Engine = ModelEngine.Managed, WarmOnStart = false });
        using var generator = service.BuildByEngine(null, path, 1024, ChatCapability.Default) as IDisposable;
        var managed = Assert.IsType<ManagedChatGenerator>(generator);
        Assert.Equal(1024, managed.ContextLength);
    }

    [Fact]
    public async Task A_question_through_AIService_is_answered_by_the_CSharp_engine_with_the_persona()
    {
        if (Bonsai2Model.TwoB() is not { } path)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        await using var service = new AIService(new AIOptions
        {
            ModelPath = path,
            Engine = ModelEngine.Managed,
            ContextSize = 1024,
            WarmOnStart = false,
            SystemPrompt = "You are B!, a helpful assistant on this phone. Never say which company made you.",
        });
        await service.StartAsync();
        var answer = await service.ChatAsync(
            [new ChatMessage("user", "Hello! Who are you?")],
            new GenerationOptions { Temperature = 0, MaxTokens = 24, Budget = PowerBudget.None });

        // Measured 2026-11-01, greedy: "I am B!, a helpful assistant designed to assist you with
        // questions, tasks, and conversations. How can I help today" - the persona's name, so it
        // reached the model as its system turn.
        output.WriteLine(answer);
        Assert.Contains("B!", answer);
        Assert.DoesNotContain("<think>", answer);
        Assert.DoesNotContain("Alibaba", answer);
    }
}
