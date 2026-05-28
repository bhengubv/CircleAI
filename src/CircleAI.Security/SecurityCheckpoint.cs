// SecurityCheckpoint.cs
//
// A cryptographically-bound snapshot of trusted local state.
//
// When CircleAI detects an anomaly, the watchdog may roll back to the last
// verified checkpoint. A checkpoint is:
//   - IMMUTABLE once created (sealed record)
//   - SELF-VERIFYING (SHA-256 of Payload, verified on restore)
//   - TAGGED with the UHID that created it (identity binding)
//
// The payload is deliberately opaque (byte[]) so any module can checkpoint
// its own serialised state without this package taking a dependency on it.

namespace CircleAI.Security;

using System.Security.Cryptography;

/// <summary>
/// An immutable, self-verifying snapshot of trusted local state.
/// Created before a risky operation; used for rollback if an
/// <see cref="AnomalySignal"/> is confirmed.
/// </summary>
/// <param name="Id">Unique checkpoint identifier.</param>
/// <param name="UhidIdentityId">
/// The UHID of the local user whose state is captured.
/// Binds the checkpoint to a specific identity.
/// </param>
/// <param name="ModuleLabel">
/// Label for the module or subsystem that created this checkpoint
/// (e.g. <c>"CircleAI.Companion"</c>, <c>"CircleAI.Memory"</c>).
/// </param>
/// <param name="Payload">Opaque serialised state payload.</param>
/// <param name="PayloadHash">
/// SHA-256 hash of <paramref name="Payload"/>, computed at creation time.
/// Verified by <see cref="Verify"/> before restoring.
/// </param>
/// <param name="CreatedAt">UTC timestamp of checkpoint creation.</param>
public sealed record SecurityCheckpoint(
    Guid Id,
    string UhidIdentityId,
    string ModuleLabel,
    byte[] Payload,
    byte[] PayloadHash,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Creates a new checkpoint, computing <see cref="PayloadHash"/> automatically.
    /// </summary>
    public static SecurityCheckpoint Create(
        string uhidIdentityId,
        string moduleLabel,
        byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uhidIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleLabel);
        ArgumentNullException.ThrowIfNull(payload);

        var hash = SHA256.HashData(payload);
        return new SecurityCheckpoint(
            Guid.NewGuid(), uhidIdentityId, moduleLabel,
            payload, hash, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Verifies that <see cref="Payload"/> has not been tampered with since
    /// the checkpoint was created.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the current SHA-256 of <see cref="Payload"/> matches
    /// <see cref="PayloadHash"/>; <c>false</c> if the payload was modified.
    /// </returns>
    public bool Verify()
    {
        var current = SHA256.HashData(Payload);
        return CryptographicOperations.FixedTimeEquals(current, PayloadHash);
    }
}
