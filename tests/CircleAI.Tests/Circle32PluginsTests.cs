// Circle32PluginsTests.cs
//
// (3.2.0) Tests for CircleAI.Plugins — PluginEvents pub/sub, registry
// JSON round-trip + permission granting/revoking, marketplace parsing,
// PermissionedPluginContext gating, PluginLoader empty-folder safety.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle32PluginsTests
{
    // ── PluginEvents ──────────────────────────────────────────────────

    [Fact]
    public void Events_Subscribe_ReceivesPayload()
    {
        var ev = new PluginEvents();
        object? received = null;
        using var sub = ev.Subscribe("foo", payload => received = payload);
        ev.Raise("foo", "hello");
        Assert.Equal("hello", received);
    }

    [Fact]
    public void Events_DifferentName_NotReceived()
    {
        var ev = new PluginEvents();
        var hits = 0;
        using var sub = ev.Subscribe("foo", _ => hits++);
        ev.Raise("bar", "ignored");
        Assert.Equal(0, hits);
    }

    [Fact]
    public void Events_Dispose_Unsubscribes()
    {
        var ev = new PluginEvents();
        var hits = 0;
        var sub = ev.Subscribe("foo", _ => hits++);
        ev.Raise("foo", 1);
        sub.Dispose();
        ev.Raise("foo", 2);
        Assert.Equal(1, hits);
    }

    [Fact]
    public void Events_HandlerThrows_OthersStillRun()
    {
        var ev = new PluginEvents();
        var second = 0;
        using var a = ev.Subscribe("foo", _ => throw new InvalidOperationException("boom"));
        using var b = ev.Subscribe("foo", _ => second++);
        ev.Raise("foo", null);
        Assert.Equal(1, second);
    }

    // ── PluginRegistry ────────────────────────────────────────────────

    [Fact]
    public void Registry_RegisterThenGet_RoundTrips()
    {
        var dir = NewTempDir();
        try
        {
            var reg = new PluginRegistry(dir, NullLogger.Instance);
            reg.Register("foo", "Foo Plugin", "1.0.0", new[] { "workspace.read" });
            var got = reg.Get("foo");
            Assert.NotNull(got);
            Assert.Equal("Foo Plugin", got!.DisplayName);
            Assert.Contains("workspace.read", got.Permissions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Registry_Persists_AcrossInstances()
    {
        var dir = NewTempDir();
        try
        {
            new PluginRegistry(dir).Register("bar", "Bar", "2.0.0", Array.Empty<string>());
            var reloaded = new PluginRegistry(dir);
            Assert.NotNull(reloaded.Get("bar"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Registry_SetEnabled_Persists()
    {
        var dir = NewTempDir();
        try
        {
            var reg = new PluginRegistry(dir);
            reg.Register("baz", "Baz", "1.0.0", Array.Empty<string>());
            Assert.True(reg.SetEnabled("baz", true));
            Assert.True(reg.Get("baz")!.Enabled);
            Assert.False(reg.SetEnabled("nope", true));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Registry_Grant_Revoke_Permission()
    {
        var dir = NewTempDir();
        try
        {
            var reg = new PluginRegistry(dir);
            reg.Register("qux", "Qux", "1.0.0", Array.Empty<string>());
            Assert.True(reg.GrantPermission("qux", "workspace.read"));
            Assert.Contains("workspace.read", reg.Get("qux")!.Permissions);
            Assert.True(reg.RevokePermission("qux", "workspace.read"));
            Assert.Empty(reg.Get("qux")!.Permissions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Registry_Uninstall_Removes()
    {
        var dir = NewTempDir();
        try
        {
            var reg = new PluginRegistry(dir);
            reg.Register("uninst", "Uninst", "1.0.0", Array.Empty<string>());
            Assert.True(reg.Uninstall("uninst"));
            Assert.Null(reg.Get("uninst"));
            Assert.False(reg.Uninstall("uninst"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── PluginMarketplace ─────────────────────────────────────────────

    [Fact]
    public void Marketplace_MissingFile_Empty()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var market = new PluginMarketplace(path);
        Assert.Empty(market.List());
    }

    [Fact]
    public void Marketplace_ParsesEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                [{"id":"a","displayName":"A","version":"1.0.0","description":"","author":"","downloadUrl":"","permissions":[]}]
                """);
            var market = new PluginMarketplace(path);
            var list = market.List();
            Assert.Single(list);
            Assert.Equal("a", list[0].Id);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── PermissionedPluginContext ─────────────────────────────────────

    [Fact]
    public void Context_DefaultPermissions_WorkspaceHidden()
    {
        var inner = new PluginContext(() => "C:/some/path", new PluginEvents(), NullLogger.Instance);
        var ctx = new PermissionedPluginContext(inner, Array.Empty<string>());
        Assert.Null(ctx.WorkspacePath);
    }

    [Fact]
    public void Context_WorkspaceRead_Exposes()
    {
        var inner = new PluginContext(() => "C:/some/path", new PluginEvents(), NullLogger.Instance);
        var ctx = new PermissionedPluginContext(inner, new[] { "workspace.read" });
        Assert.Equal("C:/some/path", ctx.WorkspacePath);
    }

    [Fact]
    public void Context_NoEventsPermission_SilentBus()
    {
        var hostBus = new PluginEvents();
        var hits = 0;
        using var hostSub = hostBus.Subscribe("e", _ => hits++);
        var inner = new PluginContext(() => null, hostBus, NullLogger.Instance);

        var noPerm = new PermissionedPluginContext(inner, Array.Empty<string>());
        // The plugin's view of the bus is silent — subscribing here is
        // a no-op, AND the host's bus is untouched by the plugin.
        using var silentSub = noPerm.Events.Subscribe("e", _ => hits++);
        noPerm.Events.Raise("e", "no-op");
        Assert.Equal(0, hits);

        // Host can still raise on its own bus.
        hostBus.Raise("e", "real");
        Assert.Equal(1, hits);
    }

    [Fact]
    public void Context_EventsSubscribePermission_GetsRealBus()
    {
        var hostBus = new PluginEvents();
        var inner = new PluginContext(() => null, hostBus, NullLogger.Instance);
        var ctx = new PermissionedPluginContext(inner, new[] { "events.subscribe" });
        Assert.Same(hostBus, ctx.Events);
    }

    // ── PluginLoader ──────────────────────────────────────────────────

    [Fact]
    public void Loader_NonexistentDir_Empty()
    {
        var loader = new PluginLoader(NullLogger.Instance);
        Assert.Empty(loader.Discover(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Loader_EmptyDir_Empty()
    {
        var dir = NewTempDir();
        try
        {
            var loader = new PluginLoader(NullLogger.Instance);
            Assert.Empty(loader.Discover(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Loader_FolderWithoutDll_ReportsError()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "empty-plugin"));
            var results = new PluginLoader(NullLogger.Instance).Discover(dir).ToList();
            Assert.Single(results);
            Assert.Null(results[0].Plugin);
            Assert.NotNull(results[0].Error);
            Assert.Contains("No .dll", results[0].Error!);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "circleai-plugins-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
