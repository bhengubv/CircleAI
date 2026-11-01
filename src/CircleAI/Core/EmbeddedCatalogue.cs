// EmbeddedCatalogue.cs
//
// THE ONE EMBEDDED MODEL LIST, for the older readers that take its entries by name.
//
// Models/embedded_registry.json is the catalogue ModelRegistryService reads and the app chooses
// from. LocalModelLoader and ModelDownloader read a second copy, Core/registry.json, in a shape of
// their own - and the two drifted: 31 models against 88 when OPEN-GAPS C3 was written, level again
// (96 and 96, every file hash the same) only because somebody copied them across by hand. Two owners
// of one fact stay level only while somebody remembers. Both readers now take the live list from
// here; registry.json is left in the repository, read by nothing.

using System.Text.Json;

namespace CircleAI.Core;

/// <summary>Reads the live embedded model catalogue's entries, keyed by model name.</summary>
internal static class EmbeddedCatalogue
{
    /// <summary>The manifest name of the live catalogue.</summary>
    internal const string ResourceName = "CircleAI.Core.Models.embedded_registry.json";

    /// <summary>
    /// Each entry of the live catalogue's <c>Models</c>, as <typeparamref name="T"/>, by its
    /// <c>Name</c>; entries that do not read as <typeparamref name="T"/> are left out.
    /// </summary>
    /// <exception cref="FileNotFoundException">The catalogue is not embedded in this build.</exception>
    internal static Dictionary<string, T> Read<T>(JsonSerializerOptions options) where T : class
    {
        using var stream = typeof(EmbeddedCatalogue).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("The embedded model catalogue is not in this build.", ResourceName);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var entries = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("Models", out var models) || models.ValueKind != JsonValueKind.Array)
            return entries;

        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object
                || !model.TryGetProperty("Name", out var name)
                || name.GetString() is not { Length: > 0 } id)
                continue;
            if (model.Deserialize<T>(options) is { } entry) entries[id] = entry;
        }
        return entries;
    }
}
