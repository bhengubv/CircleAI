// CodingModelCatalog.cs
//
// The catalogue seam for on-device coding models. This is deliberately EMPTY by
// default: a real 3-7B coding model requires a downloaded, SHA-256-verified
// bundle that this build does not carry. We define the seam so the capability
// can light up the moment a host registers a real, hash-verified model — and we
// refuse to register one without a hash, so nobody can fake availability.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Inference;  // ChatCapability

namespace CircleAI.CodeAgent;

/// <summary>
/// A coding model that is installable / installed on this device. The
/// <see cref="Sha256"/> is load-bearing: it is the verification hash of the
/// downloaded bundle. An entry without one is not a model, it is a promise —
/// and <see cref="InMemoryCodingModelCatalog"/> refuses to register it.
/// </summary>
/// <param name="ModelId">Logical identifier resolvable by the host's loader.</param>
/// <param name="ParametersBillion">Model size in billions of parameters (3-7 for the target class).</param>
/// <param name="MinRamGb">Minimum available RAM to load + run this bundle.</param>
/// <param name="MinFreeStorageGb">Minimum free storage the bundle occupies on disk.</param>
/// <param name="TotalBytes">On-disk footprint of the full bundle after fetch.</param>
/// <param name="Sha256">SHA-256 of the bundle — the "downloaded hash" the gate verifies against.</param>
/// <param name="Capabilities">Capability flags this model declares (must include the coding floor's required flags).</param>
public sealed record CodingModelDescriptor(
    string         ModelId,
    int            ParametersBillion,
    double         MinRamGb,
    double         MinFreeStorageGb,
    long           TotalBytes,
    string         Sha256,
    ChatCapability Capabilities);

/// <summary>
/// Where the coding-capability gate discovers real, installable coding models.
/// The default (<see cref="EmptyCodingModelCatalog"/>) is empty on purpose.
/// </summary>
public interface ICodingModelCatalog
{
    /// <summary>Stable identifier for logs / diagnostics.</summary>
    string BackendId { get; }

    /// <summary>Coding models available to this device. Empty when none is installed.</summary>
    IReadOnlyList<CodingModelDescriptor> Available { get; }
}

/// <summary>
/// Fail-closed default: NO coding model is catalogued. This is the honest state
/// of the current build — we do not have a coding-model hash, so we ship none.
/// The gate reports Unavailable for the right reason: nothing to run.
/// </summary>
public sealed class EmptyCodingModelCatalog : ICodingModelCatalog
{
    /// <summary>Shared instance — the catalogue holds no state.</summary>
    public static readonly EmptyCodingModelCatalog Instance = new();

    /// <inheritdoc/>
    public string BackendId => "empty";

    /// <inheritdoc/>
    public IReadOnlyList<CodingModelDescriptor> Available => Array.Empty<CodingModelDescriptor>();
}

/// <summary>
/// Host-populated catalogue. A host that has fetched and verified a real coding
/// model registers it here (with its hash) to enable on-device coding. Refuses
/// unverifiable entries so "available" can never be faked.
/// </summary>
public sealed class InMemoryCodingModelCatalog : ICodingModelCatalog
{
    private readonly List<CodingModelDescriptor> _models;

    /// <summary>Create a catalogue, optionally seeded with already-verified descriptors.</summary>
    public InMemoryCodingModelCatalog(IEnumerable<CodingModelDescriptor>? seed = null)
    {
        _models = new List<CodingModelDescriptor>();
        if (seed is not null)
            foreach (var d in seed) Add(d);
    }

    /// <inheritdoc/>
    public string BackendId => "in-memory";

    /// <inheritdoc/>
    public IReadOnlyList<CodingModelDescriptor> Available => _models;

    /// <summary>
    /// Register a coding model. Throws when the descriptor carries no
    /// <see cref="CodingModelDescriptor.Sha256"/> — an unverifiable bundle must
    /// never be presented as available.
    /// </summary>
    public InMemoryCodingModelCatalog Add(CodingModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Sha256))
            throw new ArgumentException(
                "A coding model MUST carry a SHA-256 verification hash. Refusing to register an " +
                "unverifiable bundle — that would fake on-device availability.",
                nameof(descriptor));
        if (_models.Any(m => string.Equals(m.ModelId, descriptor.ModelId, StringComparison.OrdinalIgnoreCase)))
            return this; // idempotent by ModelId
        _models.Add(descriptor);
        return this;
    }
}
