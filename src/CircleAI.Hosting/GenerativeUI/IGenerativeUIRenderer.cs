// IGenerativeUIRenderer.cs
//
// (2.0.2) Generative UI plug point — consumer-provided. The hosting
// layer feeds parsed UiComponent records here; the renderer materialises
// them into a native UI (MAUI controls, HTML DOM, terminal layout,
// React tree, etc).
//
// Pattern inspired by bhengubv/json-render. We don't depend on the npm
// framework — we just adopt its "AI emits JSON constrained to a typed
// catalog, host renders" contract.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.GenerativeUI;

/// <summary>
/// One UI element produced by a generative-UI model. Fields are
/// intentionally generic — concrete shapes (`UiCard`, `UiButton`,
/// `UiList`, …) are pre-defined records that wrap a <see cref="UiComponent"/>.
/// </summary>
/// <param name="Kind">Catalog identifier, e.g. <c>"card"</c>, <c>"button"</c>, <c>"list"</c>.</param>
/// <param name="Properties">Bag of property values keyed by JSON property name.</param>
/// <param name="Children">Optional nested components.</param>
public sealed record UiComponent(
    string                              Kind,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyList<UiComponent>?         Children = null);

/// <summary>
/// Catalog entry — declares the allowed kinds + their properties. The
/// LLM is constrained (via system prompt or response-format schema) to
/// emit only kinds present in the catalog.
/// </summary>
/// <param name="Kind">e.g. "card".</param>
/// <param name="Description">One-line description used in the prompt.</param>
/// <param name="AllowedProperties">Property names + JSON Schema type strings.</param>
/// <param name="AllowsChildren">Whether the component may contain nested components.</param>
public sealed record UiCatalogEntry(
    string                              Kind,
    string                              Description,
    IReadOnlyDictionary<string, string> AllowedProperties,
    bool                                AllowsChildren = false);

/// <summary>
/// Pre-canned component catalogs the hosting layer can ship out of the
/// box. Consumers may extend / replace.
/// </summary>
public static class UiCatalogs
{
    /// <summary>
    /// Minimal "chat assistant tool output" catalog. Covers card / list /
    /// button / textBlock / image. Mirrors json-render's most-used set.
    /// </summary>
    public static readonly IReadOnlyList<UiCatalogEntry> Default = new[]
    {
        new UiCatalogEntry(
            "card",
            "A bordered container with a title and body. May contain children.",
            new Dictionary<string, string>
            {
                ["title"]   = "string",
                ["caption"] = "string?",
            },
            AllowsChildren: true),
        new UiCatalogEntry(
            "list",
            "An ordered or unordered list. Children are the list items.",
            new Dictionary<string, string>
            {
                ["ordered"] = "boolean",
            },
            AllowsChildren: true),
        new UiCatalogEntry(
            "button",
            "A tappable button. Emit an action identifier when clicked.",
            new Dictionary<string, string>
            {
                ["label"]  = "string",
                ["action"] = "string",
                ["style"]  = "string?",
            }),
        new UiCatalogEntry(
            "textBlock",
            "Inline text content, optionally markdown.",
            new Dictionary<string, string>
            {
                ["text"]      = "string",
                ["markdown"]  = "boolean?",
            }),
        new UiCatalogEntry(
            "image",
            "An image displayed from a URL or data-URI.",
            new Dictionary<string, string>
            {
                ["src"] = "string",
                ["alt"] = "string?",
            }),
    };
}

/// <summary>
/// (2.0.2) Renderer contract. Consumers implement this in their host
/// (MAUI, Web, Server, CLI) to materialise <see cref="UiComponent"/>
/// records into a native UI.
/// </summary>
public interface IGenerativeUIRenderer
{
    /// <summary>
    /// Render a single root component. The host owns the materialisation
    /// (e.g. MAUI: build a Grid; Web: emit HTML; Server: serialise to JSON
    /// for downstream).
    /// </summary>
    ValueTask RenderAsync(UiComponent root, CancellationToken ct = default);
}

/// <summary>
/// Default no-op renderer for tests and headless server scenarios. Holds
/// the last rendered component for assertion / inspection.
/// </summary>
public sealed class RecordingGenerativeUIRenderer : IGenerativeUIRenderer
{
    public UiComponent? LastRendered { get; private set; }
    public int RenderCount { get; private set; }

    public ValueTask RenderAsync(UiComponent root, CancellationToken ct = default)
    {
        LastRendered = root;
        RenderCount++;
        return ValueTask.CompletedTask;
    }
}
