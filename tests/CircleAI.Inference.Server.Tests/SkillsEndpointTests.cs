// SkillsEndpointTests.cs
//
// GET /v1/skills[?q=] — search Circle AI's skills over HTTP.
//
// Two layers of proof:
//   * the ENDPOINT CONTRACT (auth gate, query passthrough, JSON shape) against a
//     deterministic in-memory store, so the test is stable and about the wire, not
//     the pack's content;
//   * a WIRING SMOKE against the real default store, proving the built-in pack is
//     registered and the endpoint serialises it. It asserts only shape, not
//     content — the pack can degrade to empty on a cold start (a known, device-safe
//     behaviour) and the endpoint still answers 200 with a valid skills array. The
//     pack's content is ConsumerSkillPackTests' job, not the endpoint's.

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Inference.Server.Tests.TestFixtures;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class SkillsEndpointTests : IClassFixture<InferenceServerFactory>
{
    private readonly InferenceServerFactory _factory;
    public SkillsEndpointTests(InferenceServerFactory factory) => _factory = factory;

    // --- endpoint contract, against a deterministic store ------------------------

    private HttpClient DeterministicClient()
    {
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<ISkillStore>(new FakeSkillStore())));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CircleAI-Api-Key", InferenceServerFactory.TestApiKey);
        return client;
    }

    [Fact]
    public async Task Skills_Requires_Auth()
    {
        using var unauth = _factory.CreateClient();
        var resp = await unauth.GetAsync("/v1/skills");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Skills_Authed_No_Query_Lists_Everything()
    {
        using var client = DeterministicClient();
        var resp = await client.GetAsync("/v1/skills");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"skills\"", json);
        Assert.Contains("apply-grant", json);
        Assert.Contains("open-bank", json);
    }

    [Fact]
    public async Task Skills_Authed_With_Query_Filters_To_Hits()
    {
        using var client = DeterministicClient();
        var resp = await client.GetAsync("/v1/skills?q=grant");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("apply-grant", json);   // matched on tag/name
        Assert.DoesNotContain("open-bank", json);
    }

    [Fact]
    public async Task Skills_Authed_With_Miss_Returns_Empty_Hits()
    {
        using var client = DeterministicClient();
        var resp = await client.GetAsync("/v1/skills?q=nonesuchskillxyz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("apply-grant", json);
        Assert.DoesNotContain("open-bank", json);
    }

    // --- wiring smoke, against the real default pack -----------------------------

    [Fact]
    public async Task Skills_DefaultPack_Is_Wired_And_Serialises()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.GetAsync("/v1/skills");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Shape only: the default ISkillStore resolved and the endpoint serialised
        // it. Content (which skills, how many) belongs to ConsumerSkillPackTests.
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"skills\"", json);
    }

    // A stable in-memory store so the endpoint contract is tested, not the pack.
    private sealed class FakeSkillStore : ISkillStore
    {
        private readonly IReadOnlyList<SkillSummary> _all = new[]
        {
            new SkillSummary("apply-grant", "Apply for a grant",
                "How to apply for a social grant", new[] { "rights", "grant" }, SkillSource.InMemory),
            new SkillSummary("open-bank", "Open a bank account",
                "Opening a no-fee bank account", new[] { "money", "bank" }, SkillSource.InMemory),
        };

        public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_all);

        public Task<IReadOnlyList<SkillSummary>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult<IReadOnlyList<SkillSummary>>(Array.Empty<SkillSummary>());

            var hits = _all.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            return Task.FromResult<IReadOnlyList<SkillSummary>>(hits);
        }

        public Task<SkillDetail?> GetAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<SkillDetail?>(null);

        public Task<SkillDetail> UpsertAsync(string? id, SkillDraft draft, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
