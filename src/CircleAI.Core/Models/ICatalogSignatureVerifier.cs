// ICatalogSignatureVerifier.cs
//
// Pluggable signature check for catalog JSON. Decoupled from the
// catalog client + registry service so a real Ed25519 verifier (or a
// future ECDSA / post-quantum variant) can slot in without rewriting
// either of them. Ships with NullCatalogSignatureVerifier as the
// default — until a public key is embedded as a resource the SDK
// keeps the same fail-closed posture the legacy
// ModelRegistryService.VerifySignature had, just routed through this
// interface so the failure becomes observable instead of a thrown
// NotSupportedException.

using System;

namespace CircleAI.Core.Models;

/// <summary>
/// Outcome of a signature verification attempt over a catalog payload.
/// </summary>
public enum CatalogSignatureResult
{
    /// <summary>Signature is present and valid for the configured public key.</summary>
    Valid       = 0,

    /// <summary>Signature is present but does not match the configured public key.</summary>
    Invalid     = 1,

    /// <summary>No signature was attached to the payload.</summary>
    Missing     = 2,

    /// <summary>
    /// No verifier is configured yet — applies when the SDK ships
    /// without an embedded public key. Treated by the catalog client
    /// the same as <see cref="Invalid"/> for cache-application decisions,
    /// but distinct so observers can tell "not signed" from "tampered."
    /// </summary>
    NotConfigured = 3,
}

/// <summary>
/// Verifies the cryptographic signature attached to a catalog JSON
/// payload. The catalog client invokes this before applying any
/// freshly fetched catalog to the on-disk cache.
/// </summary>
public interface ICatalogSignatureVerifier
{
    /// <summary>
    /// Verify that <paramref name="payload"/> was signed by the key
    /// configured for this verifier.
    /// </summary>
    /// <param name="payload">UTF-8 bytes of the catalog JSON (without signature wrapper).</param>
    /// <param name="signatureBase64">
    /// Base64-encoded detached signature. May be <c>null</c> or empty
    /// when no signature was provided — verifier returns
    /// <see cref="CatalogSignatureResult.Missing"/>.
    /// </param>
    CatalogSignatureResult Verify(ReadOnlySpan<byte> payload, string? signatureBase64);
}

/// <summary>
/// Default verifier — always returns <see cref="CatalogSignatureResult.NotConfigured"/>.
/// Ships as the registered default until a real Ed25519 verifier with
/// an embedded public key replaces it. Catalog client treats this as
/// "do not apply fetched catalog, keep cached version" — fail-closed.
/// </summary>
public sealed class NullCatalogSignatureVerifier : ICatalogSignatureVerifier
{
    public static readonly NullCatalogSignatureVerifier Instance = new();
    private NullCatalogSignatureVerifier() { }

    public CatalogSignatureResult Verify(ReadOnlySpan<byte> payload, string? signatureBase64) =>
        CatalogSignatureResult.NotConfigured;
}
