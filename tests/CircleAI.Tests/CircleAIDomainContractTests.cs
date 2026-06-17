// CircleAIDomainContractTests.cs
//
// (2.4.0) Contract surface tests for the Domain pack.

using System.Threading.Tasks;
using CircleAI.Domain;
using Xunit;

namespace CircleAI.Tests;

public sealed class CircleAIDomainContractTests
{
    [Fact]
    public async Task NullFoodEmbeddings_ReturnsZeroVectorAndEmptySubs()
    {
        var v = await NullFoodEmbeddings.Instance.EmbedAsync(new Ingredient("salt"));
        Assert.Equal(300, v.Length);
        var subs = await NullFoodEmbeddings.Instance.SubstitutesAsync(new Ingredient("salt"));
        Assert.Empty(subs);
    }

    [Fact]
    public async Task NullFinanceRetrieval_ReturnsEmpty()
        => Assert.Empty(await NullFinanceRetrieval.Instance.RetrieveAsync("x"));

    [Fact]
    public async Task NullFinancialAgent_ReturnsEmpty()
        => Assert.Empty(await NullFinancialAgent.Instance.ResearchAsync("x"));

    [Fact]
    public async Task NullPresentationGenerator_ReturnsEmptyDeckWithTheme()
    {
        var p = await NullPresentationGenerator.Instance.GenerateAsync("x", theme: "geek");
        Assert.Empty(p.Slides);
        Assert.Equal("geek", p.Theme);
    }

    [Fact]
    public async Task NullJobSearchPipeline_ReturnsEmpty()
    {
        var d = await NullJobSearchPipeline.Instance.DraftApplicationAsync("role", "profile");
        Assert.Equal("", d.ResumeText);
        Assert.Empty(d.KeyMatches);
    }

    [Fact]
    public async Task NullMemPalace_NoopUpsertAndEmptyRecall()
    {
        await NullMemPalaceStore.Instance.UpsertAsync(new MemoryItem("a", "text"));
        Assert.Empty(await NullMemPalaceStore.Instance.RecallAsync("x"));
    }

    [Fact]
    public async Task NullHippoRag_NoopIndexAndEmptyRecall()
    {
        await NullHippoRagStore.Instance.IndexAsync(new MemoryItem("a", "text"));
        Assert.Empty(await NullHippoRagStore.Instance.MultiHopRecallAsync("x"));
    }

    [Fact]
    public async Task NullSwarmCoordinator_NoPeersAndNoDelegate()
    {
        Assert.Empty(await NullSwarmCoordinator.Instance.ListPeersAsync());
        Assert.Null(await NullSwarmCoordinator.Instance.ChooseDelegateAsync("inference"));
    }

    [Fact]
    public async Task NullPersonalLoRA_TrainReturnsZeroSteps()
    {
        var s = await NullPersonalLoRA.Instance.TrainAsync("u1", new[] { "hello" });
        Assert.Equal("u1", s.AdapterId);
        Assert.Equal(0,   s.StepsTrained);
    }
}
