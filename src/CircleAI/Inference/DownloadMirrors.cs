// DownloadMirrors.cs
//
// ANOTHER MIRROR, AFTER A STALL (S6).
//
// Every bundle file has two addresses - the primary and the fallback ModelDownloadService
// builds - and a download that stalls on the first carries on from the second with its bytes
// kept. When both give up, Wolverine resumes the download later, and this is how it asks for
// the address that did not stall to be tried FIRST: a hook the host sets, asked per model. The
// bytes already on disk are resumed either way; only the order of the two addresses changes.

namespace CircleAI.Inference;

/// <summary>Which of a bundle's two addresses a download starts from.</summary>
public static class DownloadMirrors
{
    /// <summary>
    /// True for a model whose download should start from its fallback address. Set by the host;
    /// null - and any answer that throws - keeps the usual order.
    /// </summary>
    public static Func<string, bool>? PreferAlternate { get; set; }

    /// <summary>Whether <paramref name="modelId"/>'s download starts from the fallback address now.</summary>
    public static bool StartsFromAlternate(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        try { return PreferAlternate?.Invoke(modelId) == true; }
        catch { return false; }
    }
}
