// FileLinkGrantStore.cs
//
// Grants that survive the service being killed.
//
// InMemoryLinkGrantStore forgets every link the moment the process dies, and on a
// 3 GB phone the service process dies often — so a person would re-approve every
// app every few minutes. This persists the grants to a JSON file: held in memory
// for speed, flushed on every change through a temp-file rename so a crash mid-
// write never leaves a half-written file. A missing or corrupt file loads as
// empty rather than throwing, because a lost grant costs one re-approval and an
// exception costs the whole link.

using System.Text.Json;

namespace CircleAI.Linking;

/// <summary>An <see cref="ILinkGrantStore"/> that persists to a JSON file.</summary>
public sealed class FileLinkGrantStore : ILinkGrantStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkGrant> _grants;

    /// <param name="path">Where the grants file lives. Its directory is created on first write.</param>
    public FileLinkGrantStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _grants = Load(path);
    }

    /// <inheritdoc />
    public Task<LinkGrant?> FindAsync(string callerPackage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerPackage);
        lock (_gate)
            return Task.FromResult(_grants.TryGetValue(callerPackage, out var grant) ? grant : null);
    }

    /// <inheritdoc />
    public Task SaveAsync(LinkGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.CallerPackage);
        lock (_gate) { _grants[grant.CallerPackage] = grant; Flush(); }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RevokeAsync(string callerPackage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerPackage);
        lock (_gate) { if (_grants.Remove(callerPackage)) Flush(); }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LinkGrant>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<LinkGrant>>(_grants.Values.ToList());
    }

    private void Flush()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_grants.Values.ToList(), JsonOptions));
            File.Move(tmp, _path, overwrite: true);   // never leave a half-written file
        }
        catch
        {
            // A grant that could not be persisted still works for this run; the
            // link feature must not fall over because storage was momentarily busy.
        }
    }

    private static Dictionary<string, LinkGrant> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, LinkGrant>(StringComparer.Ordinal);

            var list = JsonSerializer.Deserialize<List<LinkGrant>>(File.ReadAllText(path), JsonOptions)
                       ?? new List<LinkGrant>();

            var map = new Dictionary<string, LinkGrant>(StringComparer.Ordinal);
            foreach (var grant in list)
                if (grant is not null && !string.IsNullOrEmpty(grant.CallerPackage))
                    map[grant.CallerPackage] = grant;
            return map;
        }
        catch
        {
            return new Dictionary<string, LinkGrant>(StringComparer.Ordinal);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
