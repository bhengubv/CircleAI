// InMemoryCrm.cs
//
// (3.3.0) Real in-memory CRM: contact store with name/email substring
// search, deal pipeline indexed by stage, activity log per contact.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.CRM;

public sealed class InMemoryContactStore : IContactStore
{
    private readonly ConcurrentDictionary<string, Contact> _items = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public ValueTask UpsertAsync(Contact c, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (string.IsNullOrWhiteSpace(c.ContactId)) throw new ArgumentException("ContactId required");
        _items[c.ContactId] = c;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Contact?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        _items.TryGetValue(id, out var c);
        return ValueTask.FromResult(c);
    }

    public ValueTask<IReadOnlyList<Contact>> SearchAsync(string query, int topK = 20, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        var hits = _items.Values
            .Where(c => c.FullName.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (c.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(c => c.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<Contact>>(hits);
    }
}

public sealed class InMemoryDealPipeline : IDealPipeline
{
    private readonly ConcurrentDictionary<string, Deal> _items = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public ValueTask UpsertAsync(Deal d, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (string.IsNullOrWhiteSpace(d.DealId)) throw new ArgumentException("DealId required");
        _items[d.DealId] = d;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Deal?> GetAsync(string id, CancellationToken ct = default)
    {
        _items.TryGetValue(id, out var d);
        return ValueTask.FromResult(d);
    }

    public ValueTask<IReadOnlyList<Deal>> ListByStageAsync(string stage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stage)) throw new ArgumentException("stage required", nameof(stage));
        var hits = _items.Values
            .Where(d => string.Equals(d.Stage, stage, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Value)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<Deal>>(hits);
    }
}

public sealed class InMemoryActivityLog : IActivityLog
{
    private readonly ConcurrentDictionary<string, List<Activity>> _byContact = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public ValueTask AppendAsync(Activity a, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrWhiteSpace(a.ContactId)) throw new ArgumentException("ContactId required");
        lock (_lock)
        {
            var list = _byContact.GetOrAdd(a.ContactId, _ => new List<Activity>());
            list.Add(a);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Activity>> ReadForContactAsync(string contactId, int limit = 100, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contactId)) throw new ArgumentException("contactId required", nameof(contactId));
        lock (_lock)
        {
            if (!_byContact.TryGetValue(contactId, out var list)) return ValueTask.FromResult<IReadOnlyList<Activity>>(Array.Empty<Activity>());
            return ValueTask.FromResult<IReadOnlyList<Activity>>(
                list.OrderByDescending(a => a.AtUtc).Take(limit).ToArray());
        }
    }
}
