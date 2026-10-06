// NullImplementations.cs
//
// (2.6.0) Fail-closed defaults — null toolkit refuses to apply anything;
// null auditor always asserts ok-to-publish (since nothing was applied).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.ModelAlignment;

public sealed class NullAlignmentToolkit : IAlignmentToolkit
{
    public static readonly NullAlignmentToolkit Instance = new();
    public string BackendId => "null";

    public ValueTask<AlignmentResult> ApplyAsync(string modelId, AlignmentProfile profile, CancellationToken ct = default)
        => ValueTask.FromResult(new AlignmentResult(
            ProfileId:     profile.ProfileId,
            Success:       false,
            FailureReason: "NullAlignmentToolkit: no real backend wired."));

    public ValueTask<AlignmentResult> RevertAsync(string modelId, string profileId, CancellationToken ct = default)
        => ValueTask.FromResult(new AlignmentResult(
            ProfileId:     profileId,
            Success:       false,
            FailureReason: "NullAlignmentToolkit: nothing to revert."));

    public ValueTask<IReadOnlyList<AlignmentProfile>> ListAppliedAsync(string modelId, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AlignmentProfile>>(Array.Empty<AlignmentProfile>());
}

public sealed class NullAlignmentAuditor : IAlignmentAuditor
{
    public static readonly NullAlignmentAuditor Instance = new();
    public string BackendId => "null";

    public ValueTask AssertOkToPublishAsync(string modelId, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
