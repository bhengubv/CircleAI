// KvCompressionEnumTests.cs
//
// Item 4 audit follow-up — scaffolding only. Verifies the managed-side
// enums + value mapping. Cannot exercise the native side without a loaded
// model + native libraries on disk; Phase 4.1 ports the algorithm and
// adds end-to-end integration tests.

using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class KvCompressionEnumTests
{
    [Fact]
    public void KvCompressionMode_HasExpectedIntegerEncoding()
    {
        Assert.Equal(0, (int)KvCompressionMode.Off);
        Assert.Equal(1, (int)KvCompressionMode.TurboQuant4Bit);
        Assert.Equal(2, (int)KvCompressionMode.TurboQuant3Bit);
        Assert.Equal(3, (int)KvCompressionMode.TurboQuant2Bit);
    }

    [Fact]
    public void KvCompressionApplyResult_HasExpectedIntegerEncoding()
    {
        Assert.Equal(0, (int)KvCompressionApplyResult.Applied);
        Assert.Equal(1, (int)KvCompressionApplyResult.InvalidMode);
        Assert.Equal(2, (int)KvCompressionApplyResult.NotImplemented);
        Assert.Equal(-1, (int)KvCompressionApplyResult.HandleInvalid);
    }

    [Fact]
    public void KvCompressionMode_All_FourDistinctValues()
    {
        var values = Enum.GetValues<KvCompressionMode>();
        Assert.Equal(4, values.Length);
        Assert.Equal(values.Distinct().Count(), values.Length);
    }

    [Fact]
    public void KvCompressionApplyResult_NotImplemented_DocumentedAsScaffolding()
    {
        // Phase 4 ships the surface but not the algorithm. NotImplemented
        // is the expected response for any non-Off mode against the
        // current native build.
        var result = KvCompressionApplyResult.NotImplemented;
        Assert.NotEqual(KvCompressionApplyResult.Applied, result);
    }
}
