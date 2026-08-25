// BrowserSettings.cs
//
// Settings in a browser: the choices are real, the documents are not here.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
/// <remarks>
/// IN MEMORY, AND THE DOCUMENT LIST IS ALWAYS EMPTY - which is the honest answer
/// rather than a limitation to apologise for. The documents live on the phone with
/// the store that made them; a browser build keeps no copy of somebody's CV, and
/// showing a list here would mean it did.
/// </remarks>
public sealed class BrowserSettings : ISettings
{
    private AppSettings _settings = new();

    /// <inheritdoc />
    public Task<AppSettings> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(_settings);

    /// <inheritdoc />
    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredDocument>> DocumentsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoredDocument>>([]);

    /// <inheritdoc />
    public Task DeleteDocumentAsync(string path, CancellationToken ct = default)
        => Task.CompletedTask;
}
