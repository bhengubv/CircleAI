// UhidKeyRing.cs
//
// Ephemeral session key management bound to a UHID identity.
//
// Each UHID session gets a fresh P-256 (NIST) key pair for ECDSA signing.
// When an anomaly is confirmed the watchdog calls GenerateFresh() — the old
// key is revoked and a new key ring is issued. All in-flight requests signed
// with the revoked key are rejected.
//
// Uses System.Security.Cryptography only — no external NuGet dependencies.
// P-256 is selected over Ed25519 for BCL compatibility across .NET 9 on all
// 10 language host platforms (Ed25519 is available in .NET 8+ but P-256 has
// wider toolchain support for cross-language interop).

namespace CircleAI.Security;

using System.Security.Cryptography;

/// <summary>
/// Ephemeral ECDSA (P-256) session key ring bound to a UHID identity.
/// Generate a fresh ring at session start or on anomaly confirmation.
/// Once revoked, the ring cannot sign; generate a new one.
/// </summary>
public sealed class UhidKeyRing : IDisposable
{
    private ECDsa? _key;
    private bool _revoked;
    private readonly object _lock = new();

    /// <summary>Unique ring identifier. Changes on every <see cref="GenerateFresh"/> call.</summary>
    public Guid RingId { get; private set; }

    /// <summary>The UHID identity this ring is bound to.</summary>
    public string UhidIdentityId { get; }

    /// <summary>UTC timestamp when this ring was generated.</summary>
    public DateTimeOffset GeneratedAt { get; private set; }

    /// <summary>UTC timestamp when this ring was revoked, or <c>null</c> if still active.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary><c>true</c> if this ring has been explicitly revoked.</summary>
    public bool IsRevoked => _revoked;

    /// <summary>
    /// The DER-encoded public key for this ring.
    /// Safe to share; corresponds to the private signing key.
    /// </summary>
    public byte[] PublicKeyDer { get; private set; } = [];

    private UhidKeyRing(string uhidIdentityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uhidIdentityId);
        UhidIdentityId = uhidIdentityId;
        RegenerateKey();
    }

    /// <summary>
    /// Creates a new <see cref="UhidKeyRing"/> for <paramref name="uhidIdentityId"/>
    /// with a freshly generated P-256 key pair.
    /// </summary>
    public static UhidKeyRing GenerateFresh(string uhidIdentityId) => new(uhidIdentityId);

    /// <summary>
    /// Rotates the ring: revokes the current key and generates a replacement.
    /// Returns a NEW <see cref="UhidKeyRing"/> — this instance remains revoked.
    /// </summary>
    /// <remarks>
    /// Prefer this pattern over mutating in place so call sites holding a
    /// reference to the old ring cannot accidentally sign with a rotated key.
    /// </remarks>
    public UhidKeyRing Rotate()
    {
        Revoke();
        return GenerateFresh(UhidIdentityId);
    }

    /// <summary>
    /// Signs <paramref name="data"/> with the current private key using
    /// ECDSA-SHA256. Throws <see cref="InvalidOperationException"/> if revoked.
    /// </summary>
    public byte[] Sign(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            if (_revoked)
                throw new InvalidOperationException(
                    $"UhidKeyRing {RingId} has been revoked — call Rotate() to get a fresh ring.");
            return _key.SignData(data, HashAlgorithmName.SHA256);
        }
    }

    /// <summary>
    /// Verifies an ECDSA-SHA256 <paramref name="signature"/> against
    /// <paramref name="data"/> using this ring's public key.
    /// Works even after revocation (so prior signatures can still be validated).
    /// </summary>
    public bool Verify(byte[] data, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        lock (_lock)
        {
            if (_key is null) return false;
            return _key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
    }

    /// <summary>
    /// Revokes this ring. After revocation <see cref="Sign"/> throws;
    /// <see cref="Verify"/> continues to work for historical validation.
    /// </summary>
    public void Revoke()
    {
        lock (_lock)
        {
            if (_revoked) return;
            _revoked = true;
            RevokedAt = DateTimeOffset.UtcNow;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void RegenerateKey()
    {
        lock (_lock)
        {
            _key?.Dispose();
            _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            RingId = Guid.NewGuid();
            GeneratedAt = DateTimeOffset.UtcNow;
            RevokedAt = null;
            _revoked = false;
            PublicKeyDer = _key.ExportSubjectPublicKeyInfo();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _key?.Dispose();
            _key = null;
        }
    }
}
