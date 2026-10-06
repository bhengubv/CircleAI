// FileArtifact.cs
//
// A file the user is about to open/run, described by its SHA-256 hash — not its
// contents. The library assesses by hash so it never has to hold or move file
// bytes: privacy-preserving and cheap on a low-end phone.

using System.Security.Cryptography;

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// Describes a file to assess. Identified by its SHA-256 digest so the assessment
/// is a pure hash lookup — the file's contents never enter the library beyond the
/// point where the hash is computed.
/// </summary>
/// <param name="FileName">Display name, used only in user-facing guidance.</param>
/// <param name="Sha256Hex">Lowercase SHA-256 hex digest of the file's contents.</param>
/// <param name="SizeBytes">File size in bytes, for context in guidance.</param>
public sealed record FileArtifact(string FileName, string Sha256Hex, long SizeBytes)
{
    /// <summary>
    /// Builds a <see cref="FileArtifact"/> from a file name and its raw content,
    /// computing the SHA-256 digest here (offline, BCL only). Prefer this so callers
    /// never have to implement hashing themselves.
    /// </summary>
    public static FileArtifact FromContent(string fileName, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        byte[] digest = SHA256.HashData(content);
        return new FileArtifact(fileName, Convert.ToHexString(digest).ToLowerInvariant(), content.Length);
    }
}
