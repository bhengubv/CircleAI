// ModelIntegrityException.cs
//
// A DOWNLOAD THAT ARRIVED WHOLE AND WRONG (S6).
//
// A SHA-256 mismatch was an InvalidOperationException like a dozen other things, so nothing
// downstream could tell "the bytes are corrupt, fetch them again" from "the registry row has no
// repo". Its own type gives it its own fingerprint - CircleAI:ModelIntegrityException:<site> -
// and Wolverine's rule for it is a re-download. It derives from InvalidOperationException so
// every catch written for the old type still catches it.

namespace CircleAI.Inference;

/// <summary>A downloaded model file failed its SHA-256 check; the bad file has been deleted.</summary>
public sealed class ModelIntegrityException : InvalidOperationException
{
    /// <summary>A mismatch for <paramref name="fileName"/> of <paramref name="modelId"/>.</summary>
    public ModelIntegrityException(string modelId, string fileName, string message)
        : base(message)
    {
        ModelId = modelId;
        FileName = fileName;
    }

    /// <summary>The model whose file failed.</summary>
    public string ModelId { get; }

    /// <summary>The file that failed.</summary>
    public string FileName { get; }
}
