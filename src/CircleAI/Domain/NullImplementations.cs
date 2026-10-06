// NullImplementations.cs
//
// (2.4.0) Fail-safe defaults for every Domain contract.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Domain;

public sealed class NullFoodEmbeddings : IFoodEmbeddings
{
    public static readonly NullFoodEmbeddings Instance = new();
    public string BackendId => "null";
    public ValueTask<float[]> EmbedAsync(Ingredient i, CancellationToken ct = default)
        => ValueTask.FromResult(new float[300]);
    public ValueTask<IReadOnlyList<Ingredient>> SubstitutesAsync(Ingredient i, int topK = 5, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Ingredient>>(Array.Empty<Ingredient>());
}

public sealed class NullFinanceRetrieval : IFinanceRetrieval
{
    public static readonly NullFinanceRetrieval Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<FinanceSnippet>> RetrieveAsync(string query, int topK = 5, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<FinanceSnippet>>(Array.Empty<FinanceSnippet>());
}

public sealed class NullFinancialAgent : IFinancialAgent
{
    public static readonly NullFinancialAgent Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<FinanceFinding>> ResearchAsync(string q, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<FinanceFinding>>(Array.Empty<FinanceFinding>());
}

public sealed class NullPresentationGenerator : IPresentationGenerator
{
    public static readonly NullPresentationGenerator Instance = new();
    public string BackendId => "null";
    public ValueTask<GeneratedPresentation> GenerateAsync(
        string topic, int targetSlideCount = 10, string? theme = null, CancellationToken ct = default)
        => ValueTask.FromResult(new GeneratedPresentation(
            Slides: Array.Empty<SlideOutline>(),
            Theme:  theme ?? "default",
            Format: "json"));
}

public sealed class NullJobSearchPipeline : IJobSearchPipeline
{
    public static readonly NullJobSearchPipeline Instance = new();
    public string BackendId => "null";
    public ValueTask<JobApplicationDraft> DraftApplicationAsync(string role, string profile, CancellationToken ct = default)
        => ValueTask.FromResult(new JobApplicationDraft(
            ResumeText:      "",
            CoverLetterText: "",
            KeyMatches:      Array.Empty<string>()));
}

public sealed class NullMemPalaceStore : IMemPalaceStore
{
    public static readonly NullMemPalaceStore Instance = new();
    public string BackendId => "null";
    public ValueTask UpsertAsync(MemoryItem i, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<MemoryHit>> RecallAsync(string q, int topK = 5, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MemoryHit>>(Array.Empty<MemoryHit>());
}

public sealed class NullHippoRagStore : IHippoRagStore
{
    public static readonly NullHippoRagStore Instance = new();
    public string BackendId => "null";
    public ValueTask IndexAsync(MemoryItem i, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<MemoryHit>> MultiHopRecallAsync(string q, int topK = 5, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MemoryHit>>(Array.Empty<MemoryHit>());
}

public sealed class NullSwarmCoordinator : ISwarmCoordinator
{
    public static readonly NullSwarmCoordinator Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<SwarmPeer>> ListPeersAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<SwarmPeer>>(Array.Empty<SwarmPeer>());
    public ValueTask<string?> ChooseDelegateAsync(string capability, CancellationToken ct = default)
        => ValueTask.FromResult<string?>(null);
}

public sealed class NullPersonalLoRA : IPersonalLoRA
{
    public static readonly NullPersonalLoRA Instance = new();
    public string BackendId => "null";
    public ValueTask<LoRATrainingSummary> TrainAsync(string id, IReadOnlyList<string> s, CancellationToken ct = default)
        => ValueTask.FromResult(new LoRATrainingSummary(id, 0, 0f));
    public ValueTask LoadAdapterAsync(string id, CancellationToken ct = default)   => ValueTask.CompletedTask;
    public ValueTask UnloadAdapterAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
}
