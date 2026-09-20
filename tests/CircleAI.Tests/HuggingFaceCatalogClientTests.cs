// HuggingFaceCatalogClientTests.cs
//
// The second internet discovery host. Proves the integrity handling the live API
// forced: an LFS file's SHA-256 is read from lfs.oid, a small non-LFS file's
// SHA-256 is COMPUTED from the bytes (HF only exposes a git SHA-1 there), a
// non-free licence is skipped, and a refresh announces as an "internet" source
// (so it both reaches the runtime catalogue and propagates to mesh peers).

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public class HuggingFaceCatalogClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<Uri, (int Status, string Body)> Respond = _ => (404, string.Empty);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = Respond(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }
    }

    const string LfsSha = "d426c65a5159c938ccc237cdfbd982137f276804f27b414ca0ecf3fc0a660f8c";

    const string Tree = """
        [
          {"type":"file","path":"config.json","oid":"aaaa1111","size":5},
          {"type":"file","path":"llm.mnn.weight","oid":"bbbb2222","size":90,
           "lfs":{"oid":"d426c65a5159c938ccc237cdfbd982137f276804f27b414ca0ecf3fc0a660f8c","size":90}}
        ]
        """;

    static HuggingFaceCatalogClient Client(FakeHandler handler)
        => new(new HuggingFaceCatalogOptions(), new HttpClient(handler));

    [Fact]
    public async Task Fetch_reads_the_LFS_sha_and_computes_the_non_LFS_sha()
    {
        var handler = new FakeHandler
        {
            Respond = uri =>
            {
                var p = uri.PathAndQuery;
                if (p.Contains("/resolve/main/config.json")) return (200, "hello");
                if (p.Contains("/tree/main")) return (200, Tree);
                if (p.StartsWith("/api/models?author=")) return (200, "[{\"id\":\"taobao-mnn/Test-MNN\"}]");
                if (p.StartsWith("/api/models/")) return (200, "{\"cardData\":{\"license\":\"apache-2.0\"}}");
                return (404, string.Empty);
            }
        };
        using var client = Client(handler);

        var reg = await client.FetchAsync();

        Assert.NotNull(reg);
        var m = Assert.Single(reg!.Models);
        Assert.Equal("Test-MNN", m.Name);
        Assert.Equal(ModelSource.HuggingFace, m.Source);
        Assert.Equal(ModelEngine.Mnn, m.Engine);
        Assert.Equal("taobao-mnn/Test-MNN", m.Repo);
        Assert.True(m.QualityRank > 0);   // size-derived, so it is selectable

        Assert.Equal(2, m.BundleFiles!.Count);
        Assert.Equal(LfsSha, m.BundleFiles.Single(f => f.Name == "llm.mnn.weight").Sha256);   // from lfs.oid

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))).ToLowerInvariant();
        Assert.Equal(expected, m.BundleFiles.Single(f => f.Name == "config.json").Sha256);    // computed, not the git SHA-1
    }

    [Fact]
    public async Task A_non_free_licence_is_skipped()
    {
        var handler = new FakeHandler
        {
            Respond = uri =>
            {
                var p = uri.PathAndQuery;
                if (p.Contains("/resolve/main/")) return (200, "x");
                if (p.Contains("/tree/main")) return (200, Tree);
                if (p.StartsWith("/api/models?author=")) return (200, "[{\"id\":\"taobao-mnn/Good-MNN\"},{\"id\":\"taobao-mnn/Bad-MNN\"}]");
                if (p.Contains("/api/models/taobao-mnn/Bad-MNN")) return (200, "{\"cardData\":{\"license\":\"gemma\"}}");
                if (p.StartsWith("/api/models/")) return (200, "{\"cardData\":{\"license\":\"apache-2.0\"}}");
                return (404, string.Empty);
            }
        };
        using var client = Client(handler);

        var reg = await client.FetchAsync();

        var m = Assert.Single(reg!.Models);
        Assert.Equal("Good-MNN", m.Name);   // the gemma-licensed one never entered
    }

    [Fact]
    public async Task RefreshAsync_announces_the_result_as_an_internet_source()
    {
        var handler = new FakeHandler
        {
            Respond = uri =>
            {
                var p = uri.PathAndQuery;
                if (p.Contains("/resolve/main/config.json")) return (200, "hello");
                if (p.Contains("/tree/main")) return (200, Tree);
                if (p.StartsWith("/api/models?author=")) return (200, "[{\"id\":\"taobao-mnn/Test-MNN\"}]");
                if (p.StartsWith("/api/models/")) return (200, "{\"cardData\":{\"license\":\"apache-2.0\"}}");
                return (404, string.Empty);
            }
        };
        using var client = Client(handler);

        var arrived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnArrived(ModelRegistry r, string source)
        {
            if (r.Models.Any(m => m.Name == "Test-MNN")) arrived.TrySetResult(source);
        }
        ModelCatalogue.CatalogueArrived += OnArrived;
        try
        {
            var ok = await client.RefreshAsync();
            Assert.True(ok);
            Assert.Equal("internet", await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            ModelCatalogue.CatalogueArrived -= OnArrived;
            ModelCatalogue.Forget();
        }
    }
}
