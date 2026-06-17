// NullImplementations.cs
//
// (2.7.0) Defaults — no-op store, always-invalid validator, empty scaffold.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.SDD;

public sealed class NullSpecificationStore : ISpecificationStore
{
    public static readonly NullSpecificationStore Instance = new();
    public string BackendId => "null";
    public ValueTask UpsertAsync(Specification s, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<Specification?> GetAsync(string id, CancellationToken ct = default)
        => ValueTask.FromResult<Specification?>(null);
    public ValueTask<IReadOnlyList<Specification>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Specification>>(Array.Empty<Specification>());
}

public sealed class NullSpecificationValidator : ISpecificationValidator
{
    public static readonly NullSpecificationValidator Instance = new();
    public string BackendId => "null";
    public ValueTask<SpecValidationResult> ValidateAsync(Specification spec, CancellationToken ct = default)
        => ValueTask.FromResult(new SpecValidationResult(
            IsValid: false,
            Errors:  new[] { "No real validator wired." }));
}

public sealed class NullSpecToScaffold : ISpecToScaffold
{
    public static readonly NullSpecToScaffold Instance = new();
    public string BackendId => "null";
    public ValueTask<ScaffoldedProject> ScaffoldAsync(Specification spec, string target, CancellationToken ct = default)
        => ValueTask.FromResult(new ScaffoldedProject(
            ProjectId: Guid.Empty.ToString(),
            Files:     new Dictionary<string, ReadOnlyMemory<byte>>()));
}
