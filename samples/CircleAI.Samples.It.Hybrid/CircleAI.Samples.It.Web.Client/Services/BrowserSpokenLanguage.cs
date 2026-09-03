// BrowserSpokenLanguage.cs
//
// In a browser the choice lasts as long as the tab.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
/// <remarks>
/// IN MEMORY, DELIBERATELY. Persisting it would need localStorage, and the choice
/// only matters to a head that can actually answer in the language. This one
/// cannot speak at all, so remembering the pick beyond the page would be storing
/// a preference nothing acts on.
/// </remarks>
public sealed class BrowserSpokenLanguage : ISpokenLanguage
{
    private string? _chosen;

    /// <inheritdoc />

    /// <inheritdoc />
    /// <remarks>A tab is not asked to guess: what it is set to is what it offers.</remarks>
    public IReadOnlyList<string> Suggested => [Current];

    public string Current => _chosen ?? "en";

    /// <inheritdoc />
    public string? Chosen => _chosen;

    /// <inheritdoc />
    public void Choose(string tag) => _chosen = tag;

    /// <inheritdoc />
    public void ClearChoice() => _chosen = null;
}
