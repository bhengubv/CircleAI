// Contracts.cs
//
// (2.4.0) The CircleAI.Domain contract surface. One namespace covers
// every domain-specialist plug point that the 2.4.x line will eventually
// implement. Real backends land in 2.4.1 when EPICure / quant-mind /
// dexter / presenton / career-ops / mempalace / HippoRAG / MiroFish are
// vendored.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Domain;

// ─── Food (EPICure) ──────────────────────────────────────────────────────

/// <summary>One ingredient with optional canonical form + quantity.</summary>
public sealed record Ingredient(string Name, string? Canonical = null, string? Quantity = null);

/// <summary>(2.4.0) Food / ingredient embedding store (EPICure-backed).</summary>
public interface IFoodEmbeddings
{
    string BackendId { get; }

    ValueTask<float[]> EmbedAsync(Ingredient ingredient, CancellationToken ct = default);

    ValueTask<IReadOnlyList<Ingredient>> SubstitutesAsync(
        Ingredient        ingredient,
        int               topK = 5,
        CancellationToken ct   = default);
}

// ─── Finance (quant-mind + dexter) ───────────────────────────────────────

public sealed record FinanceSnippet(string Text, string Source, float Score);

/// <summary>(2.4.0) Quant-finance RAG retrieval.</summary>
public interface IFinanceRetrieval
{
    string BackendId { get; }

    ValueTask<IReadOnlyList<FinanceSnippet>> RetrieveAsync(
        string            query,
        int               topK = 5,
        CancellationToken ct   = default);
}

public sealed record FinanceFinding(
    string                 Subject,
    string                 Summary,
    IReadOnlyList<string>  Citations);

/// <summary>(2.4.0) Autonomous financial-research agent (dexter pattern).</summary>
public interface IFinancialAgent
{
    string BackendId { get; }

    ValueTask<IReadOnlyList<FinanceFinding>> ResearchAsync(
        string            question,
        CancellationToken ct = default);
}

// ─── Presentations (presenton) ──────────────────────────────────────────

public sealed record SlideOutline(string Title, string Body, IReadOnlyList<string>? Bullets = null);

public sealed record GeneratedPresentation(
    IReadOnlyList<SlideOutline> Slides,
    string                      Theme,
    string                      Format);

/// <summary>(2.4.0) AI presentation generator (presenton pattern).</summary>
public interface IPresentationGenerator
{
    string BackendId { get; }

    ValueTask<GeneratedPresentation> GenerateAsync(
        string            topic,
        int               targetSlideCount = 10,
        string?           theme            = null,
        CancellationToken ct               = default);
}

// ─── Job search (career-ops, TheJobCenter target) ───────────────────────

public sealed record JobApplicationDraft(
    string                ResumeText,
    string                CoverLetterText,
    IReadOnlyList<string> KeyMatches);

/// <summary>(2.4.0) Job-search pipeline — resume + cover letter + match (career-ops).</summary>
public interface IJobSearchPipeline
{
    string BackendId { get; }

    ValueTask<JobApplicationDraft> DraftApplicationAsync(
        string            roleDescription,
        string            candidateProfileText,
        CancellationToken ct = default);
}

// ─── Memory upgrades (mempalace + HippoRAG) ─────────────────────────────

public sealed record MemoryItem(string Id, string Text, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record MemoryHit(MemoryItem Item, float Score);

/// <summary>(2.4.0) MemPalace-pattern long-term memory.</summary>
public interface IMemPalaceStore
{
    string BackendId { get; }

    ValueTask UpsertAsync(MemoryItem item, CancellationToken ct = default);
    ValueTask<IReadOnlyList<MemoryHit>> RecallAsync(string query, int topK = 5, CancellationToken ct = default);
}

/// <summary>(2.4.0) HippoRAG-pattern memory + knowledge-graph + Personalized PageRank.</summary>
public interface IHippoRagStore
{
    string BackendId { get; }

    ValueTask IndexAsync(MemoryItem item, CancellationToken ct = default);
    ValueTask<IReadOnlyList<MemoryHit>> MultiHopRecallAsync(string query, int topK = 5, CancellationToken ct = default);
}

// ─── Swarm (MiroFish) ───────────────────────────────────────────────────

public sealed record SwarmPeer(string PeerId, string Capability, float Health);

/// <summary>(2.4.0) Multi-device coordination over AetherNet (MiroFish-pattern).</summary>
public interface ISwarmCoordinator
{
    string BackendId { get; }

    ValueTask<IReadOnlyList<SwarmPeer>> ListPeersAsync(CancellationToken ct = default);

    ValueTask<string?> ChooseDelegateAsync(
        string            capability,
        CancellationToken ct = default);
}

// ─── Personal LoRA (RT-10, conditional) ─────────────────────────────────

public sealed record LoRATrainingSummary(string AdapterId, int StepsTrained, float FinalLoss);

/// <summary>(2.4.0) On-device personalisation via LoRA fine-tuning (RT-10).</summary>
public interface IPersonalLoRA
{
    string BackendId { get; }

    ValueTask<LoRATrainingSummary> TrainAsync(
        string                      adapterId,
        IReadOnlyList<string>       conversationSamples,
        CancellationToken           ct = default);

    ValueTask LoadAdapterAsync(string adapterId, CancellationToken ct = default);
    ValueTask UnloadAdapterAsync(string adapterId, CancellationToken ct = default);
}
