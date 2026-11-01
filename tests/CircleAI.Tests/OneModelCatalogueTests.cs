// OneModelCatalogueTests.cs
//
// OPEN-GAPS C3: two embedded model catalogues - the one the app chooses from, and a second copy the
// older readers read - had drifted once (31 models against 88). The older readers, LocalModelLoader
// and ModelDownloader, now read the live one: every model it holds, they know, and nothing else.

using System.Text.Json;
using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

public sealed class OneModelCatalogueTests
{
    // The live catalogue's names, read from the product's own embedded resource.
    private static List<string> LiveNames()
    {
        using var stream = typeof(LocalModelLoader).Assembly.GetManifestResourceStream("CircleAI.Core.Models.embedded_registry.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("Models").EnumerateArray()
            .Select(m => m.GetProperty("Name").GetString()!)
            .ToList();
    }

    [Fact]
    public void The_model_loader_knows_exactly_the_models_the_app_chooses_from()
    {
        var names = LiveNames();
        Assert.NotEmpty(names);
        using var loader = new LocalModelLoader(Path.GetTempPath());
        foreach (var name in names) Assert.False(string.IsNullOrEmpty(loader.GetModelPath(name)));
        Assert.Throws<FileNotFoundException>(() => loader.GetModelPath("Not-In-The-Live-Catalogue"));
    }

    [Fact]
    public async Task The_downloader_knows_every_model_the_app_chooses_from()
    {
        using var downloader = new ModelDownloader([new FakeModelSource()]);
        foreach (var name in LiveNames())
        {
            // A known bundle is sent to the bundle downloader; only an unknown name is "not in the registry".
            var refusal = await Assert.ThrowsAnyAsync<Exception>(() => downloader.DownloadModelAsync(name, Path.GetTempPath()));
            Assert.IsNotType<KeyNotFoundException>(refusal);
        }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => downloader.DownloadModelAsync("Not-In-The-Live-Catalogue", Path.GetTempPath()));
    }
}
