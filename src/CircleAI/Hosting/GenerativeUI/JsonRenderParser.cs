// JsonRenderParser.cs
//
// (2.0.2) Parses an LLM-emitted JSON tree into UiComponent records.
// Validates against a UiCatalog so the LLM can't smuggle untyped
// components past the host.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CircleAI.Hosting.GenerativeUI;

/// <summary>
/// (2.0.2) Strict JSON -&gt; UiComponent parser. Rejects any kind not in
/// the catalog and any property not declared on its kind.
/// </summary>
public static class JsonRenderParser
{
    /// <summary>
    /// Parse one JSON document into a <see cref="UiComponent"/> tree.
    /// </summary>
    /// <param name="json">UTF-8 / UTF-16 JSON string.</param>
    /// <param name="catalog">Catalog whose <c>Kind</c>s constrain the tree.</param>
    /// <param name="strict">
    /// When true, unknown kinds throw. When false, unknown kinds become
    /// a textBlock with the raw JSON for debugging.
    /// </param>
    public static UiComponent Parse(
        string                        json,
        IReadOnlyList<UiCatalogEntry> catalog,
        bool                          strict = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentNullException.ThrowIfNull(catalog);

        using var doc = JsonDocument.Parse(json);
        var index = catalog.ToDictionary(c => c.Kind, StringComparer.OrdinalIgnoreCase);
        return ParseElement(doc.RootElement, index, strict);
    }

    private static UiComponent ParseElement(
        JsonElement                          el,
        IReadOnlyDictionary<string, UiCatalogEntry> catalog,
        bool                                 strict)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"Expected JSON object, got {el.ValueKind}.");

        string? kind = null;
        if (el.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String)
            kind = kindEl.GetString();
        if (string.IsNullOrEmpty(kind))
            throw new InvalidOperationException("Component missing required 'kind' field.");

        if (!catalog.TryGetValue(kind, out var entry))
        {
            if (strict)
                throw new InvalidOperationException($"Unknown component kind '{kind}'.");
            return new UiComponent(
                Kind: "textBlock",
                Properties: new Dictionary<string, object?>
                {
                    ["text"]     = $"[unknown kind '{kind}']",
                    ["markdown"] = false,
                });
        }

        var props = new Dictionary<string, object?>();
        if (el.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in propsEl.EnumerateObject())
            {
                if (strict && !entry.AllowedProperties.ContainsKey(p.Name))
                    throw new InvalidOperationException(
                        $"Component '{kind}' does not allow property '{p.Name}'.");
                props[p.Name] = ToManaged(p.Value);
            }
        }

        IReadOnlyList<UiComponent>? children = null;
        if (el.TryGetProperty("children", out var childEl) && childEl.ValueKind == JsonValueKind.Array)
        {
            if (!entry.AllowsChildren)
            {
                if (strict)
                    throw new InvalidOperationException(
                        $"Component '{kind}' does not allow children.");
            }
            else
            {
                var list = new List<UiComponent>();
                foreach (var c in childEl.EnumerateArray())
                    list.Add(ParseElement(c, catalog, strict));
                children = list;
            }
        }

        return new UiComponent(kind, props, children);
    }

    private static object? ToManaged(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String  => v.GetString(),
        JsonValueKind.Number  => v.TryGetInt64(out var i) ? i : v.GetDouble(),
        JsonValueKind.True    => true,
        JsonValueKind.False   => false,
        JsonValueKind.Null    => null,
        JsonValueKind.Array   => v.EnumerateArray().Select(ToManaged).ToArray(),
        JsonValueKind.Object  => v.EnumerateObject()
                                   .ToDictionary(o => o.Name, o => ToManaged(o.Value)),
        _                     => null,
    };

    /// <summary>
    /// Build a system-prompt snippet that describes the catalog to the
    /// model. Drop into your prompt to constrain emission.
    /// </summary>
    public static string DescribeCatalogForPrompt(IReadOnlyList<UiCatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You may respond with a single JSON object describing one UI component.");
        sb.AppendLine("Allowed shape: { \"kind\": string, \"properties\": { ... }, \"children\"?: [ ... ] }");
        sb.AppendLine();
        sb.AppendLine("Allowed kinds:");
        foreach (var e in catalog)
        {
            sb.AppendLine($"- {e.Kind} — {e.Description}");
            foreach (var (name, type) in e.AllowedProperties)
                sb.AppendLine($"    - {name}: {type}");
            if (e.AllowsChildren) sb.AppendLine("    - children: array of components");
        }
        return sb.ToString();
    }
}
