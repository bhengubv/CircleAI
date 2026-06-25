// RegistrySignatureVerifierTests.cs
//
// (3.3.0) Locks ECDSA P-256 verification of the model registry. Uses
// a freshly generated keypair per test so we don't carry a static key
// in version control.

using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public class RegistrySignatureVerifierTests
{
    [Fact]
    public void Verify_ValidIeeeP1363Signature_Returns_True()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki  = ecdsa.ExportSubjectPublicKeyInfo();
        var svc   = new ModelRegistryService(signingPublicKeyDer: spki);
        var json  = """{"models":[]}""";
        var sig   = ecdsa.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(InvokeVerify(svc, json, sig));
    }

    [Fact]
    public void Verify_ValidDerSignature_Returns_True()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki  = ecdsa.ExportSubjectPublicKeyInfo();
        var svc   = new ModelRegistryService(signingPublicKeyDer: spki);
        var json  = """{"models":[]}""";
        var sig   = ecdsa.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(InvokeVerify(svc, json, sig));
    }

    [Fact]
    public void Verify_TamperedPayload_Returns_False()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var svc = new ModelRegistryService(signingPublicKeyDer: ecdsa.ExportSubjectPublicKeyInfo());
        var sig = ecdsa.SignData(Encoding.UTF8.GetBytes("original"), HashAlgorithmName.SHA256);

        Assert.False(InvokeVerify(svc, "tampered", sig));
    }

    [Fact]
    public void Verify_DifferentKey_Returns_False()
    {
        using var signer   = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var svc = new ModelRegistryService(signingPublicKeyDer: signer.ExportSubjectPublicKeyInfo());
        var sig = attacker.SignData(Encoding.UTF8.GetBytes("payload"), HashAlgorithmName.SHA256);

        Assert.False(InvokeVerify(svc, "payload", sig));
    }

    [Fact]
    public void Verify_NoPublicKeyConfigured_Returns_False()
    {
        var svc = new ModelRegistryService(); // default ctor, no key
        Assert.False(InvokeVerify(svc, "anything", new byte[64]));
    }

    [Fact]
    public void Verify_EmptySignature_Returns_False()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var svc = new ModelRegistryService(signingPublicKeyDer: ecdsa.ExportSubjectPublicKeyInfo());
        Assert.False(InvokeVerify(svc, "data", Array.Empty<byte>()));
    }

    private static bool InvokeVerify(ModelRegistryService svc, string json, byte[] sig)
    {
        var method = typeof(ModelRegistryService).GetMethod(
            "VerifySignature",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(svc, new object[] { json, sig })!;
    }
}
