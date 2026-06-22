// Circle32VisionCloudTests.cs
//
// (3.2.0) Tests for CircleAI.Vision.Cloud — OpenAI DALL-E generator,
// Stability AI generator, NullImageGenerator, and the fallback chain
// behaviour. Fail-soft when no API key (no exception, empty list).

using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CircleAI.Vision.Cloud;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle32VisionCloudTests
{
    // ── OpenAiImageGenerator ──────────────────────────────────────────

    [Fact]
    public void OpenAi_GeneratorId_AndLabel_AreStable()
    {
        var g = new OpenAiImageGenerator(new HttpClient(), new OpenAiImageOptions());
        Assert.Equal("openai-images", g.GeneratorId);
        Assert.Contains("OpenAI", g.DisplayLabel);
        Assert.Contains("dall-e-3", g.DisplayLabel);
    }

    [Fact]
    public void OpenAi_IsConfigured_RequiresApiKey()
    {
        var noKey  = new OpenAiImageGenerator(new HttpClient(), new OpenAiImageOptions());
        var hasKey = new OpenAiImageGenerator(new HttpClient(), new OpenAiImageOptions { ApiKey = "sk-test" });
        Assert.False(noKey.IsConfigured);
        Assert.True(hasKey.IsConfigured);
        Assert.Contains("not configured", noKey.StatusMessage);
        Assert.Contains("Ready", hasKey.StatusMessage);
    }

    [Fact]
    public async Task OpenAi_NoKey_ReturnsEmpty()
    {
        var g = new OpenAiImageGenerator(new HttpClient(), new OpenAiImageOptions());
        var artifacts = await g.GenerateAsync(new ImageGenerationRequest("a cat"));
        Assert.Empty(artifacts);
    }

    // ── StabilityImageGenerator ───────────────────────────────────────

    [Fact]
    public void Stability_GeneratorId_AndLabel_AreStable()
    {
        var g = new StabilityImageGenerator(new HttpClient(), new StabilityImageOptions());
        Assert.Equal("stability", g.GeneratorId);
        Assert.Contains("Stability", g.DisplayLabel);
        Assert.Contains("sd3.5-large", g.DisplayLabel);
    }

    [Fact]
    public void Stability_IsConfigured_RequiresApiKey()
    {
        var noKey  = new StabilityImageGenerator(new HttpClient(), new StabilityImageOptions());
        var hasKey = new StabilityImageGenerator(new HttpClient(),
            new StabilityImageOptions { ApiKey = "sk-stab" });
        Assert.False(noKey.IsConfigured);
        Assert.True(hasKey.IsConfigured);
    }

    [Fact]
    public async Task Stability_NoKey_ReturnsEmpty()
    {
        var g = new StabilityImageGenerator(new HttpClient(), new StabilityImageOptions());
        var artifacts = await g.GenerateAsync(new ImageGenerationRequest("a dog"));
        Assert.Empty(artifacts);
    }

    // ── NullImageGenerator ────────────────────────────────────────────

    [Fact]
    public async Task Null_AlwaysEmpty()
    {
        var artifacts = await NullImageGenerator.Instance.GenerateAsync(
            new ImageGenerationRequest("anything"));
        Assert.Empty(artifacts);
        Assert.False(NullImageGenerator.Instance.IsConfigured);
        Assert.Equal("null", NullImageGenerator.Instance.GeneratorId);
    }

    // ── ImageGeneratorFallbackChain ───────────────────────────────────

    [Fact]
    public void Chain_Empty_IsNotConfigured()
    {
        var chain = new ImageGeneratorFallbackChain(System.Array.Empty<IImageGenerator>());
        Assert.False(chain.IsConfigured);
        Assert.Contains("No configured", chain.StatusMessage);
    }

    [Fact]
    public void Chain_AllUnconfigured_IsNotConfigured()
    {
        var chain = new ImageGeneratorFallbackChain(new IImageGenerator[]
        {
            new OpenAiImageGenerator(new HttpClient(),    new OpenAiImageOptions()),
            new StabilityImageGenerator(new HttpClient(), new StabilityImageOptions()),
        });
        Assert.False(chain.IsConfigured);
    }

    [Fact]
    public void Chain_OneConfigured_IsConfigured()
    {
        var chain = new ImageGeneratorFallbackChain(new IImageGenerator[]
        {
            new OpenAiImageGenerator(new HttpClient(),    new OpenAiImageOptions()),  // not configured
            new StabilityImageGenerator(new HttpClient(),
                new StabilityImageOptions { ApiKey = "sk-stab" }),                    // configured
        });
        Assert.True(chain.IsConfigured);
        Assert.Contains("stability", chain.StatusMessage);
    }

    [Fact]
    public async Task Chain_NoneConfigured_ReturnsEmpty()
    {
        var chain = new ImageGeneratorFallbackChain(new IImageGenerator[]
        {
            new OpenAiImageGenerator(new HttpClient(),    new OpenAiImageOptions()),
            new StabilityImageGenerator(new HttpClient(), new StabilityImageOptions()),
        });
        var artifacts = await chain.GenerateAsync(new ImageGenerationRequest("nothing"));
        Assert.Empty(artifacts);
    }

    [Fact]
    public void Chain_GeneratorId_IsStable()
    {
        var chain = new ImageGeneratorFallbackChain(System.Array.Empty<IImageGenerator>());
        Assert.Equal("fallback-chain", chain.GeneratorId);
    }

    // ── ImageGenerationRequest defaults ───────────────────────────────

    [Fact]
    public void Request_Defaults()
    {
        var r = new ImageGenerationRequest("hello");
        Assert.Equal(1024, r.Size);
        Assert.Equal(1, r.Count);
        Assert.Null(r.NegativePrompt);
        Assert.Null(r.Style);
    }
}
