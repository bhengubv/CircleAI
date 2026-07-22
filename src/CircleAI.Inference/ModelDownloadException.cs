#nullable enable

// ModelDownloadException.cs
//
// Carries the DIAGNOSIS with the failure, so callers and UI layers stop having
// to pattern-match on exception text to work out whether the user is offline,
// the mirror is dead, or the file is corrupt.

using System;

namespace CircleAI.Inference;

/// <summary>
/// A model download failed. <see cref="Diagnosis"/> says why, in a form a caller
/// can branch on and a UI can show.
/// </summary>
public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message, NetworkDiagnosis diagnosis, Exception? inner)
        : base(message, inner)
        => Diagnosis = diagnosis;

    /// <summary>Classified cause — see <see cref="NetworkDiagnosis"/>.</summary>
    public NetworkDiagnosis Diagnosis { get; }

    /// <summary>
    /// A sentence to show a person: what happened and what (if anything) they
    /// can do. Never leaks a stack trace or a Java exception name.
    /// </summary>
    public string UserMessage =>
        string.IsNullOrEmpty(Diagnosis.Remedy)
            ? "The model could not be downloaded right now. Please try again later."
            : Diagnosis.Remedy;
}
