// CompanionMemoryWiringTests.cs
//
// (M1) Proves the one-call wiring resolves the whole graph-memory brain, and that
// the connector falls back to the model-free one when no model is registered.

using System.Threading.Tasks;
using CircleAI.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Companion.Tests;

public class CompanionMemoryWiringTests
{
    [Fact]
    public async Task AddCompanionMemoryGraph_ResolvesBrain_AndFallsBackToModelFreeConnector()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEpisodicMemoryStore>(new SqliteEpisodicStore("Data Source=:memory:"));
        services.AddCompanionMemoryGraph("Data Source=:memory:");

        // Async disposal — the encoder is a background component (IAsyncDisposable).
        await using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IRecall>());
        Assert.NotNull(sp.GetService<CompanionMemoryEncoder>());
        // No IAIService registered → the model-free connector is chosen.
        Assert.IsType<HeuristicKnowledgeGraphExtractor>(sp.GetService<IKnowledgeGraphExtractor>());
    }
}
