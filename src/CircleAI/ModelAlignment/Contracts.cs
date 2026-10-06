// Contracts.cs
//
// (2.6.0) Model-alignment surface. Pattern-port of OBLITERATUS. Targeted
// abliteration lives behind contracts so a host can apply / revert it
// deliberately — and so we can refuse to publish abliterated weights.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.ModelAlignment;

public sealed record AlignmentProfile(
    string                ProfileId,
    string                Description,
    IReadOnlyList<string> RefusalCategoriesRemoved,
    DateTimeOffset        CreatedAtUtc,
    bool                  IsReversible);

public sealed record AlignmentResult(
    string  ProfileId,
    bool    Success,
    string? FailureReason);

/// <summary>(2.6.0) Targeted abliteration toolkit. Apply / revert / list alignment profiles.</summary>
public interface IAlignmentToolkit
{
    string BackendId { get; }

    ValueTask<AlignmentResult> ApplyAsync(
        string            modelId,
        AlignmentProfile  profile,
        CancellationToken ct = default);

    ValueTask<AlignmentResult> RevertAsync(
        string            modelId,
        string            profileId,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<AlignmentProfile>> ListAppliedAsync(string modelId, CancellationToken ct = default);
}

/// <summary>(2.6.0) Refuses to upload / publish weights that carry alignment deltas.</summary>
public interface IAlignmentAuditor
{
    string BackendId { get; }

    /// <summary>Throw or refuse if the model has applied alignment profiles and the action is "publish upstream".</summary>
    ValueTask AssertOkToPublishAsync(string modelId, CancellationToken ct = default);
}
