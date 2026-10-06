// Contracts.cs — (2.8.0) CRM contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.CRM;

public sealed record Contact(string ContactId, string FullName, string? Email, string? Phone, string? CompanyId);
public sealed record Company(string CompanyId, string Name, string? Industry);
public sealed record Deal(string DealId, string CompanyId, string Name, decimal Value, string Currency, string Stage);
public sealed record Activity(string ActivityId, string ContactId, string Kind, string Body, DateTimeOffset AtUtc);

public interface IContactStore
{
    string BackendId { get; }
    ValueTask UpsertAsync(Contact c, CancellationToken ct = default);
    ValueTask<Contact?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Contact>> SearchAsync(string query, int topK = 20, CancellationToken ct = default);
}

public interface IDealPipeline
{
    string BackendId { get; }
    ValueTask UpsertAsync(Deal d, CancellationToken ct = default);
    ValueTask<Deal?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Deal>> ListByStageAsync(string stage, CancellationToken ct = default);
}

public interface IActivityLog
{
    string BackendId { get; }
    ValueTask AppendAsync(Activity a, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Activity>> ReadForContactAsync(string contactId, int limit = 100, CancellationToken ct = default);
}
