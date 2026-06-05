// SecurityCheckpointToStringTests.cs
//
// Verifies that the SecurityCheckpoint.ToString() override never leaks raw
// payload bytes and emits only the first 16 hex chars of PayloadSha256 —
// enough to correlate across logs without exfiltrating content if a
// structured logger reflects on records by default.

using System;
using System.Linq;
using System.Text;
using CircleAI.Security;
using Xunit;

namespace CircleAI.Security.Tests;

public sealed class SecurityCheckpointToStringTests
{
    [Fact]
    public void ToString_Does_Not_Leak_Raw_Payload_Bytes()
    {
        var secretBytes = Encoding.UTF8.GetBytes("BEARER_TOKEN_DO_NOT_LEAK_xyz123");
        var cp = SecurityCheckpoint.Create("uhid-1", "CircleAI.Memory", secretBytes);

        var rendered = cp.ToString();

        Assert.DoesNotContain("BEARER_TOKEN_DO_NOT_LEAK_xyz123", rendered);
        Assert.DoesNotContain("xyz123", rendered);
    }

    [Fact]
    public void ToString_Emits_Hash_Prefix_Plus_Byte_Length_Plus_Identifying_Fields()
    {
        var cp = SecurityCheckpoint.Create("uhid-9", "CircleAI.Companion",
            Encoding.UTF8.GetBytes("payload"));

        var rendered = cp.ToString();
        var expectedPrefix = Convert.ToHexString(cp.PayloadHash.AsSpan(0, 8));

        Assert.Contains("SecurityCheckpoint", rendered);
        Assert.Contains("uhid-9", rendered);
        Assert.Contains("CircleAI.Companion", rendered);
        Assert.Contains($"PayloadSha256={expectedPrefix}", rendered);
        Assert.Contains($"PayloadBytes={cp.Payload.Length}", rendered);
    }

    [Fact]
    public void ToString_Handles_Empty_Payload_Without_Throwing()
    {
        var cp = SecurityCheckpoint.Create("uhid-2", "CircleAI.Memory", Array.Empty<byte>());

        // Empty payload still yields a 32-byte SHA-256 hash, so the regular
        // prefix path applies — no "(empty)" sentinel for the byte-count case.
        var rendered = cp.ToString();
        Assert.Contains("PayloadBytes=0", rendered);
        Assert.DoesNotContain("(empty)", rendered);
    }
}
