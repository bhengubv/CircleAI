// InMemoryModelAlignment.cs
//
// (3.3.0) Real in-memory alignment toolkit + auditor. ApplyAsync only
// allows reversible profiles (matches our "no permanent abliteration"
// licence stance); the auditor REFUSES to publish any model that has
// applied alignment profiles. Hosts that need different policy can
// swap auditors.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.ModelAlignment;

public sealed class InMemoryAlignmentToolkit : IAlignmentToolkit
{
    private readonly ConcurrentDictionary<string, List<AlignmentProfile>> _byModel = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public ValueTask<AlignmentResult> ApplyAsync(string modelId, AlignmentProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required");
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsReversible)
            return ValueTask.FromResult(new AlignmentResult(profile.ProfileId, false, "Non-reversible alignment refused by InMemoryAlignmentToolkit"));

        lock (_lock)
        {
            var list = _byModel.GetOrAdd(modelId, _ => new List<AlignmentProfile>());
            list.Add(profile);
        }
        return ValueTask.FromResult(new AlignmentResult(profile.ProfileId, true, null));
    }

    public ValueTask<AlignmentResult> RevertAsync(string modelId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))   throw new ArgumentException("modelId required");
        if (string.IsNullOrWhiteSpace(profileId)) throw new ArgumentException("profileId required");
        lock (_lock)
        {
            if (!_byModel.TryGetValue(modelId, out var list))
                return ValueTask.FromResult(new AlignmentResult(profileId, false, "Unknown model"));
            var removed = list.RemoveAll(p => p.ProfileId == profileId);
            return ValueTask.FromResult(removed > 0
                ? new AlignmentResult(profileId, true,  null)
                : new AlignmentResult(profileId, false, "Profile not applied to this model"));
        }
    }

    public ValueTask<IReadOnlyList<AlignmentProfile>> ListAppliedAsync(string modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required", nameof(modelId));
        lock (_lock)
        {
            if (!_byModel.TryGetValue(modelId, out var list)) return ValueTask.FromResult<IReadOnlyList<AlignmentProfile>>(Array.Empty<AlignmentProfile>());
            return ValueTask.FromResult<IReadOnlyList<AlignmentProfile>>(list.ToArray());
        }
    }
}

/// <summary>(3.3.0) Refuses to publish weights that carry alignment deltas. Wired by default.</summary>
public sealed class RefuseAlignedPublishAuditor : IAlignmentAuditor
{
    private readonly IAlignmentToolkit _toolkit;

    public RefuseAlignedPublishAuditor(IAlignmentToolkit toolkit)
        => _toolkit = toolkit ?? throw new ArgumentNullException(nameof(toolkit));

    public string BackendId => "refuse-aligned";

    public async ValueTask AssertOkToPublishAsync(string modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required", nameof(modelId));
        var applied = await _toolkit.ListAppliedAsync(modelId, ct).ConfigureAwait(false);
        if (applied.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot publish '{modelId}': {applied.Count} alignment profile(s) applied — " +
                $"this would distribute weights with safety modifications.");
        }
    }
}
