// InferenceServerFactory.cs
//
// WebApplicationFactory<TEntryPoint> wrapper that wires the stub bridge,
// stub embedder, and stub Companion resolver before the app boots.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Inference.Server.Endpoints;
using CircleAI.Inference.Server.Models;

namespace CircleAI.Inference.Server.Tests.TestFixtures;

public class InferenceServerFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-key-AAA";

    public StubCompanionSessionResolver CompanionResolver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CircleAIServer:RuntimeCacheRoot"]      = Path.Combine(Path.GetTempPath(), "circleai-test-runtime"),
                ["CircleAIServer:ModelStorageRoot"]      = Path.Combine(Path.GetTempPath(), "circleai-test-models"),
                ["CircleAIServer:MaxConcurrentRequests"] = "8",
                ["CircleAIServer:RequestTimeoutSeconds"] = "30",
                ["CircleAIServer:Auth:ApiKey:Enabled"]   = "true",
                ["CircleAIServer:Auth:ApiKey:HeaderName"]= "X-CircleAI-Api-Key",
                ["CircleAIServer:Auth:ApiKey:Keys:0"]    = TestApiKey,
                ["CircleAIServer:Auth:Jwt:Enabled"]      = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the (empty) Companion resolver with one tests can populate.
            services.AddSingleton<ICompanionSessionResolver>(CompanionResolver);

            // Pre-populate the registry with a chat bridge and an embedder.
            services.AddSingleton<IInferenceServerModelRegistry>(_ =>
            {
                var reg = new InferenceServerModelRegistry();
                reg.Register("qwen-test", new StubInferenceBridge("qwen-test"));
                reg.RegisterEmbedder("qwen-embed-test", new StubEmbedder());
                return reg;
            });
        });
    }

    public HttpClient AuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-CircleAI-Api-Key", TestApiKey);
        return client;
    }
}
