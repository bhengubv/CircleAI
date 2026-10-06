// Contracts.cs
//
// (2.7.0) Spec-Driven Development contracts. spec-kit pattern-port.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.SDD;

public sealed record Specification(
    string                              SpecId,
    string                              Title,
    string                              Body,
    string?                             Schema,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record SpecValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed record ScaffoldedProject(
    string                                       ProjectId,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files);

/// <summary>(2.7.0) Persistent specification store.</summary>
public interface ISpecificationStore
{
    string BackendId { get; }

    ValueTask UpsertAsync(Specification spec, CancellationToken ct = default);
    ValueTask<Specification?> GetAsync(string specId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Specification>> ListAsync(CancellationToken ct = default);
}

/// <summary>(2.7.0) Validate a specification (e.g. against a JSON Schema).</summary>
public interface ISpecificationValidator
{
    string BackendId { get; }

    ValueTask<SpecValidationResult> ValidateAsync(Specification spec, CancellationToken ct = default);
}

/// <summary>(2.7.0) Codegen hook — turn a spec into a scaffolded project.</summary>
public interface ISpecToScaffold
{
    string BackendId { get; }

    ValueTask<ScaffoldedProject> ScaffoldAsync(
        Specification     spec,
        string            targetLanguage,
        CancellationToken ct = default);
}
