// IModelCatalogObserver.cs
//
// The seam that makes the model catalogue's decisions VISIBLE — so the app's
// self-heal loop, and Wolverine on top of it, can show them. The catalogue lives
// below the self-heal loop (Core/Inference, under Hosting), so it cannot call up
// into ISelfHealer directly; instead it raises these neutral events, and a
// Hosting-side bridge turns each into a healing-log entry. This is what keeps the
// logging from being thin: a rejected feed, a device that can run nothing, a
// completed re-assessment — each one surfaces, rather than happening silently.
//
// Every method is a no-op on the NullModelCatalogObserver default, so the
// catalogue runs unobserved in tests and in hosts that do not wire Wolverine.

using CircleAI.Core;

namespace CircleAI.Core.Models;

/// <summary>Why a catalogue update was refused.</summary>
public enum CatalogFeedRejection
{
    /// <summary>The batch could not be parsed as a catalogue update (bad JSON or an unknown schema).</summary>
    Malformed,

    /// <summary>
    /// The offering source is not trusted — e.g. a mesh peer outside the curated
    /// trusted set. Reserved for the mesh update path; provenance is a source /
    /// node-identity decision, not a central certificate.
    /// </summary>
    Untrusted,
}

/// <summary>
/// Observes catalogue events worth surfacing to a person. Implemented by the host
/// (e.g. a bridge into the self-heal log); defaults to
/// <see cref="NullModelCatalogObserver"/>.
/// </summary>
public interface IModelCatalogObserver
{
    /// <summary>
    /// An update was refused and NO rows were written — the existing catalogue is
    /// untouched.
    /// </summary>
    /// <param name="reason">Why it was refused.</param>
    /// <param name="source">Where the update came from (a URL, "mesh", a filename).</param>
    /// <param name="detail">A human-readable one-liner.</param>
    void OnFeedRejected(CatalogFeedRejection reason, string source, string detail);

    /// <summary>An update was applied: <paramref name="upserted"/> rows written from <paramref name="source"/>.</summary>
    void OnFeedApplied(int upserted, string source);

    /// <summary>The catalogue was re-assessed against the device: <paramref name="compatible"/> of <paramref name="total"/> rows can run here.</summary>
    void OnCatalogueAssessed(int compatible, int total, DeviceProbe probe);

    /// <summary>
    /// After assessment, NO model is compatible for <paramref name="modality"/> — the
    /// "outdated model" symptom made visible: the device can run nothing catalogued,
    /// and needs a smaller model or a feed refresh.
    /// </summary>
    void OnNoCompatibleModel(ModelModality modality, int catalogued, DeviceProbe probe);
}

/// <summary>The do-nothing observer. The catalogue's default when a host wires none.</summary>
public sealed class NullModelCatalogObserver : IModelCatalogObserver
{
    public static readonly NullModelCatalogObserver Instance = new();
    private NullModelCatalogObserver() { }

    public void OnFeedRejected(CatalogFeedRejection reason, string source, string detail) { }
    public void OnFeedApplied(int upserted, string source) { }
    public void OnCatalogueAssessed(int compatible, int total, DeviceProbe probe) { }
    public void OnNoCompatibleModel(ModelModality modality, int catalogued, DeviceProbe probe) { }
}
