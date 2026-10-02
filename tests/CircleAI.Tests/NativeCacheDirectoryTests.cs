// NativeCacheDirectoryTests.cs
//
// MNN writes its caches to relative paths and this process had nowhere to put them.
// Both failures were measured on the device and both are the same cause:
//
//     E/MNNJNI: Can't open file:./mnn_cachefile.bin          (OpenCL kernels)
//     E/MNNJNI: Failed to create prefix cache file dir: prefixcache
//
// The second one left a destroyed mutex and SIGSEGV'd the threadpool, which is why
// kvcache_mmap has been off since September.

using System;
using System.IO;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class NativeCacheDirectoryTests
{
    [Fact]
    public void It_lands_beside_the_model_and_is_writable()
    {
        // THE WHOLE POINT IS THAT A RELATIVE PATH NOW RESOLVES SOMEWHERE REAL, so
        // the test writes one, the way MNN does.
        var dir = NativeCacheDirectory.Use(Path.GetTempPath());

        // Use() is once-per-process and other tests share this process, so it may
        // already point somewhere - either way the contract is the same.
        Assert.NotNull(dir);
        Assert.True(Directory.Exists(dir));

        var probe = "./mnn-cache-probe.bin";
        File.WriteAllBytes(probe, [1, 2, 3]);
        try
        {
            Assert.True(File.Exists(Path.Combine(dir!, "mnn-cache-probe.bin")));
        }
        finally { File.Delete(probe); }
    }

    [Fact]
    public void A_nested_relative_directory_can_be_created()
    {
        // MNN DOES NOT ASK PERMISSION, IT MKDIRS. "prefixcache/" is created relative
        // to the working directory, and failing that is what corrupted the mutex.
        var dir = NativeCacheDirectory.Use(Path.GetTempPath());
        Assert.NotNull(dir);

        Directory.CreateDirectory("prefixcache");
        try
        {
            Assert.True(Directory.Exists(Path.Combine(dir!, "prefixcache")));
        }
        finally { Directory.Delete(Path.Combine(dir!, "prefixcache"), recursive: true); }
    }

    [Fact]
    public void Asking_twice_does_not_move_it()
    {
        // TWO MODELS CAN LOAD AT ONCE and MNN resolves these paths lazily, during
        // load AND during run - moving the directory under a running model would be
        // worse than never setting it.
        var first  = NativeCacheDirectory.Use(Path.GetTempPath());
        var second = NativeCacheDirectory.Use(Path.Combine(Path.GetTempPath(), "somewhere-else"));

        Assert.Equal(first, second);
        Assert.Equal(first, NativeCacheDirectory.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_go_on_changes_nothing(string? path)
    {
        var before = NativeCacheDirectory.Current;
        Assert.Equal(before, NativeCacheDirectory.Use(path));
    }
}
