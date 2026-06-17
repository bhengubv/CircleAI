// JsonRenderParserTests.cs
//
// (2.0.2) Tests for the Generative UI JSON parser.

using System.Threading.Tasks;
using CircleAI.Hosting.GenerativeUI;
using Xunit;

namespace CircleAI.Tests;

public sealed class JsonRenderParserTests
{
    [Fact]
    public void Parse_SimpleCard_RoundTrips()
    {
        const string json = """
            { "kind": "card",
              "properties": { "title": "Hello", "caption": "world" } }
            """;
        var comp = JsonRenderParser.Parse(json, UiCatalogs.Default);
        Assert.Equal("card", comp.Kind);
        Assert.Equal("Hello", comp.Properties["title"]);
        Assert.Equal("world", comp.Properties["caption"]);
        Assert.Null(comp.Children);
    }

    [Fact]
    public void Parse_CardWithChildren_BuildsTree()
    {
        const string json = """
            { "kind": "card",
              "properties": { "title": "Outer" },
              "children": [
                { "kind": "textBlock", "properties": { "text": "Hi" } },
                { "kind": "button", "properties": { "label": "Tap", "action": "doit" } }
              ] }
            """;
        var comp = JsonRenderParser.Parse(json, UiCatalogs.Default);
        Assert.NotNull(comp.Children);
        Assert.Equal(2, comp.Children!.Count);
        Assert.Equal("textBlock", comp.Children[0].Kind);
        Assert.Equal("button", comp.Children[1].Kind);
    }

    [Fact]
    public void Parse_UnknownKind_Strict_Throws()
    {
        const string json = """{ "kind": "spaceship", "properties": {} }""";
        Assert.ThrowsAny<System.Exception>(() =>
            JsonRenderParser.Parse(json, UiCatalogs.Default, strict: true));
    }

    [Fact]
    public void Parse_UnknownKind_Permissive_DegradesToTextBlock()
    {
        const string json = """{ "kind": "spaceship", "properties": {} }""";
        var comp = JsonRenderParser.Parse(json, UiCatalogs.Default, strict: false);
        Assert.Equal("textBlock", comp.Kind);
        Assert.Contains("spaceship", (string?)comp.Properties["text"]);
    }

    [Fact]
    public void Parse_DisallowedProperty_Strict_Throws()
    {
        const string json = """
            { "kind": "button",
              "properties": { "label": "X", "action": "y", "evil": "yes" } }
            """;
        Assert.ThrowsAny<System.Exception>(() =>
            JsonRenderParser.Parse(json, UiCatalogs.Default, strict: true));
    }

    [Fact]
    public void Parse_ChildrenOnNonContainer_Strict_Throws()
    {
        const string json = """
            { "kind": "button",
              "properties": { "label": "X", "action": "y" },
              "children": [ { "kind": "textBlock", "properties": { "text": "no" } } ] }
            """;
        Assert.ThrowsAny<System.Exception>(() =>
            JsonRenderParser.Parse(json, UiCatalogs.Default, strict: true));
    }

    [Fact]
    public void DescribeCatalogForPrompt_ListsEveryKind()
    {
        var prompt = JsonRenderParser.DescribeCatalogForPrompt(UiCatalogs.Default);
        Assert.Contains("card", prompt);
        Assert.Contains("list", prompt);
        Assert.Contains("button", prompt);
        Assert.Contains("textBlock", prompt);
        Assert.Contains("image", prompt);
    }

    [Fact]
    public async Task RecordingRenderer_HoldsLastComponent()
    {
        var renderer = new RecordingGenerativeUIRenderer();
        var comp = JsonRenderParser.Parse(
            """{ "kind": "textBlock", "properties": { "text": "Hi" } }""",
            UiCatalogs.Default);
        await renderer.RenderAsync(comp);
        Assert.Equal(1, renderer.RenderCount);
        Assert.Equal(comp, renderer.LastRendered);
    }
}
