// ModelNotInstalledException.cs
//
// THE BRAIN LOADS WHAT IS THERE; IT DOES NOT FETCH.
//
// A brain start used to fall through to a download when the model it wanted was
// missing or failed its hash - inline, with no cancellation token, no metered-network
// gate, and inside the "loading <model>" breadcrumb, so a kill during that download
// counted as a strike against the model. With AIOptions.NeverFetch set, the brain
// throws this instead, and whoever owns downloads (the setup run, the fetch service)
// decides whether and when to get the files.

using System;

namespace CircleAI.Inference;

/// <summary>The model the brain was asked to load is not complete on this device.</summary>
public sealed class ModelNotInstalledException : Exception
{
    /// <summary>The model that is missing.</summary>
    public string ModelId { get; }

    /// <summary>True when files are present but failed verification (damaged), false when absent.</summary>
    public bool Damaged { get; }

    /// <summary>A missing or damaged model.</summary>
    public ModelNotInstalledException(string modelId, bool damaged, string? detail = null)
        : base(detail ?? (damaged
            ? $"Model '{modelId}' is on this device but its files are damaged."
            : $"Model '{modelId}' is not on this device."))
    {
        ModelId = modelId;
        Damaged = damaged;
    }
}
