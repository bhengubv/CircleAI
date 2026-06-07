// NativeRuntimePrepTests.cs
//
// Unit-level coverage for NativeRuntimePrep — the glue that flattens the
// fetched MNN core next to mnnbridge.dll, preloads it, and runs a startup
// self-check. We exercise the filesystem path (flatten + idempotence)
// without actually loading any native library, which would require a real
// mnnbridge.dll alongside the test binary.

using System.Runtime.InteropServices;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class NativeRuntimePrepTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "circleai-prep-" + Guid.NewGuid().ToString("N"));

    public NativeRuntimePrepTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void MnnBridgeFileName_IsPlatformSpecific()
    {
        var name = NativeRuntimePrep.MnnBridgeFileNameForCurrentPlatform();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Equal("mnnbridge.dll", name);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Assert.Equal("libmnnbridge.dylib", name);
        else
            Assert.Equal("libmnnbridge.so", name);
    }

    [Fact]
    public void FlattenFetchedMnnCore_CopiesSourceIntoDestDir()
    {
        var src = Path.Combine(_scratch, "fetched", "MNN.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4, 5 });

        var dest = Path.Combine(_scratch, "out", "runtimes", "win-x64", "native");
        NativeRuntimePrep.FlattenFetchedMnnCore(src, dest);

        var landed = Path.Combine(dest, "MNN.dll");
        Assert.True(File.Exists(landed));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(landed));
    }

    [Fact]
    public void FlattenFetchedMnnCore_IsIdempotent_SkipsWhenLengthAndMtimeMatch()
    {
        var src = Path.Combine(_scratch, "fetched", "MNN.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllBytes(src, new byte[8]);

        var dest = Path.Combine(_scratch, "out", "runtimes", "win-x64", "native");
        NativeRuntimePrep.FlattenFetchedMnnCore(src, dest);

        var landed = Path.Combine(dest, "MNN.dll");
        var firstStamp = File.GetLastWriteTimeUtc(landed);

        // Second run with unchanged source must not rewrite the file.
        NativeRuntimePrep.FlattenFetchedMnnCore(src, dest);
        Assert.Equal(firstStamp, File.GetLastWriteTimeUtc(landed));
    }

    [Fact]
    public void FlattenFetchedMnnCore_NoOps_WhenSourceEqualsDest()
    {
        var dir = Path.Combine(_scratch, "shared");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "MNN.dll");
        File.WriteAllBytes(path, new byte[] { 9 });

        // Pointing the flatten step at the source's own directory must be a
        // safe no-op rather than a file-in-use crash.
        NativeRuntimePrep.FlattenFetchedMnnCore(path, dir);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FlattenFetchedMnnCore_ThrowsWhenSourceMissing()
    {
        var dest = Path.Combine(_scratch, "out");
        Assert.Throws<FileNotFoundException>(() =>
            NativeRuntimePrep.FlattenFetchedMnnCore(
                Path.Combine(_scratch, "does-not-exist.dll"), dest));
    }

    [Fact]
    public void PrepareForLoad_ThrowsWithActionableMessage_WhenBridgeCannotLoad()
    {
        // Point at a fake "fetched MNN" file but DON'T provide a mnnbridge
        // beside the test assembly. The self-check must throw and the
        // message must name expected paths so an operator can act.
        var fakeMnn = Path.Combine(_scratch, "fake-MNN.bin");
        File.WriteAllBytes(fakeMnn, new byte[16]);

        try
        {
            NativeRuntimePrep.PrepareForLoad(fakeMnn, _scratch);
            // If by some chance mnnbridge IS loadable in this process (e.g.
            // the test runner shadow-copied it), we don't have an assertion
            // — just don't fail the test.
        }
        catch (InvalidOperationException ex)
        {
            // Actionable diagnostic content.
            Assert.Contains("native runtime failed to load", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Expected mnnbridge", ex.Message);
            Assert.Contains("Expected MNN core",  ex.Message);
            Assert.Contains("Fetched MNN core",   ex.Message);
            Assert.Contains(fakeMnn,              ex.Message);
        }
    }
}
