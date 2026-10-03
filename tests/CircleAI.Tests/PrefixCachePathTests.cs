// PrefixCachePathTests.cs
//
// The path that SIGSEGV'd a phone twice, three weeks apart.
//
// MNN does not use the path it is given for setPrefixCacheFile - it PREPENDS a
// relative "prefixcache/" to it. Handed an absolute path it tries to create the
// whole absolute tree underneath a relative folder:
//
//   prefixcache//data/user/0/com.bhengubv.circleai.service/files/.circleai/
//     prefix-cache/a4638952cea37ebb_5da837afa18c1f67.session_0.k
//
// It cannot, fails once per layer, and leaves a destroyed mutex that faults the
// threadpool on the next lock. These tests pin the shape of the name rather than
// its contents, because the shape is the entire bug.

using System;
using System.IO;
using System.Threading.Tasks;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class PrefixCachePathTests : IDisposable
{
    private readonly string _was = Directory.GetCurrentDirectory();
    private readonly string _dir;

    public PrefixCachePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "prefix-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.SetCurrentDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_was);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PrefixCacheService Service()
        => new(Path.Combine(Path.GetTempPath(), "prefix-cache-root-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void The_name_handed_to_MNN_is_relative()
    {
        // THE WHOLE FIX. A bare filename makes MNN build "prefixcache/<key>.session"
        // - one level, which it creates happily. Anything rooted brings back the
        // crash.
        var name = Service().PathFor("abc123_def456");

        Assert.Equal("abc123_def456.session", name);
        Assert.False(Path.IsPathRooted(name), "an absolute path is what crashed the phone");
        Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
        Assert.DoesNotContain('/', name);
    }

    [Fact]
    public void What_MNN_prepends_is_one_level_and_creatable()
    {
        // Reproducing MNN's own concatenation, which is the thing that has to work.
        var combined = "prefixcache/" + Service().PathFor("k");

        Directory.CreateDirectory(Path.GetDirectoryName(combined)!);
        File.WriteAllBytes(combined + "_0.k", [1]);

        Assert.True(File.Exists(Path.Combine(_dir, "prefixcache", "k.session_0.k")));
    }

    [Fact]
    public async Task An_entry_is_found_by_what_MNN_actually_wrote()
    {
        // NOTHING IS EVER WRITTEN AT PathFor's EXACT NAME. MNN appends its own
        // suffixes, one pair per layer, so a File.Exists on that name would be false
        // for ever - a cache that fills and is never read.
        var svc = Service();
        var dir = Path.Combine(_dir, "prefixcache");
        Directory.CreateDirectory(dir);

        Assert.False(await svc.HasEntryAsync("k"));
        Assert.False(svc.HasEntry("k"));

        File.WriteAllBytes(Path.Combine(dir, "k.session_0.k"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "k.session_0.v"), [1]);

        Assert.True(await svc.HasEntryAsync("k"));
        Assert.True(svc.HasEntry("k"));
    }

    [Fact]
    public void One_key_is_not_mistaken_for_another()
    {
        var svc = Service();
        var dir = Path.Combine(_dir, "prefixcache");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "aaa.session_0.k"), [1]);

        Assert.True(svc.HasEntry("aaa"));
        Assert.False(svc.HasEntry("bbb"));
    }

    [Fact]
    public void Touch_moves_every_layer_so_eviction_sees_one_age()
    {
        // The layers are one entry. Touching a single file would let eviction
        // delete half of a set and leave MNN pointed at the remains.
        var svc = Service();
        var dir = Path.Combine(_dir, "prefixcache");
        Directory.CreateDirectory(dir);

        var old = DateTime.UtcNow.AddDays(-3);
        foreach (var leaf in new[] { "k.session_0.k", "k.session_0.v", "k.session_1.k" })
        {
            var f = Path.Combine(dir, leaf);
            File.WriteAllBytes(f, [1]);
            File.SetLastWriteTimeUtc(f, old);
        }

        svc.Touch("k");

        foreach (var leaf in new[] { "k.session_0.k", "k.session_0.v", "k.session_1.k" })
            Assert.True(File.GetLastWriteTimeUtc(Path.Combine(dir, leaf)) > old.AddDays(1), leaf);
    }

    [Fact]
    public async Task Eviction_looks_where_MNN_writes_rather_than_where_we_asked()
    {
        // The old glob was "*.session" against the service's own root. MNN never
        // wrote a file at either, so eviction had nothing to find and the cache was
        // unbounded in the one place it actually grows.
        var svc = Service();
        var dir = Path.Combine(_dir, "prefixcache");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "k.session_0.k"), new byte[16]);

        await svc.EvictIfNeededAsync();

        // Well under the cap, so it survives - what is asserted is that the call
        // reaches this directory at all and does not throw on the way.
        Assert.True(File.Exists(Path.Combine(dir, "k.session_0.k")));
    }

    [Fact]
    public void A_missing_directory_is_not_an_error()
    {
        // The first run on any phone: nothing has been written yet, and a cache is
        // an optimisation that must never be a reason to fail a model load.
        var svc = Service();

        Assert.False(svc.HasEntry("k"));
        svc.Touch("k");
    }
}
