// Circle33PacaPluginsTests.cs
//
// (3.3.0) Tests for plugin registry + manifest validation.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaPluginsTests
{
    private static PluginManifest GoodManifest(string version = "1.0.0") => new(
        Name:               "com.paca.bdd",
        DisplayName:        "Behavioural Driven Dev",
        Version:            version,
        Description:        "Gherkin feature runner",
        ArtifactWasmUrl:    new Uri("https://example.com/bdd.wasm"),
        FrontendModuleUrl:  new Uri("https://example.com/bdd.js"),
        ExtensionPoints:    new[] { PluginExtensionPoint.Sidebar, PluginExtensionPoint.McpTool },
        McpTools:           new[] { "run_feature" },
        SqlMigrationFiles:  new[] { "001_init.sql" },
        Limits:             new PluginResourceLimits());

    [Fact]
    public async Task Install_StoresPlugin()
    {
        var runtime = new RecordingRuntime();
        var reg = new PacaPluginRegistry(runtime);
        var installed = await reg.InstallAsync(GoodManifest(), catalog: "default");

        Assert.True(installed.Enabled);
        Assert.True(runtime.InstallCalled);
        Assert.Single(reg.ListInstalled());
    }

    [Fact]
    public async Task Install_DuplicateName_Throws()
    {
        var reg = new PacaPluginRegistry(new RecordingRuntime());
        await reg.InstallAsync(GoodManifest(), "x");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reg.InstallAsync(GoodManifest(), "x"));
    }

    [Fact]
    public void Validate_InvalidName_Throws()
    {
        var manifest = GoodManifest() with { Name = "not-reverse-dns" };
        Assert.Throws<ArgumentException>(() => PacaPluginRegistry.ValidateManifest(manifest));
    }

    [Fact]
    public void Validate_UnparseableSemver_Throws()
    {
        var manifest = GoodManifest() with { Version = "abc" };
        Assert.Throws<ArgumentException>(() => PacaPluginRegistry.ValidateManifest(manifest));
    }

    [Fact]
    public async Task Upgrade_NewerVersion_Succeeds()
    {
        var runtime = new RecordingRuntime();
        var reg = new PacaPluginRegistry(runtime);
        await reg.InstallAsync(GoodManifest("1.0.0"), "default");
        await reg.UpgradeAsync(GoodManifest("1.1.0"), "default");

        Assert.True(runtime.UpgradeCalled);
        var upd = reg.Get("com.paca.bdd");
        Assert.Equal("1.1.0", upd!.Manifest.Version);
    }

    [Fact]
    public async Task Upgrade_OlderVersion_Throws()
    {
        var reg = new PacaPluginRegistry(new RecordingRuntime());
        await reg.InstallAsync(GoodManifest("1.5.0"), "default");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reg.UpgradeAsync(GoodManifest("1.4.0"), "default"));
    }

    [Fact]
    public async Task Uninstall_RemovesPlugin()
    {
        var runtime = new RecordingRuntime();
        var reg = new PacaPluginRegistry(runtime);
        await reg.InstallAsync(GoodManifest(), "default");

        await reg.UninstallAsync("com.paca.bdd", dropArtifacts: true);

        Assert.Empty(reg.ListInstalled());
        Assert.True(runtime.UninstallCalled);
    }

    [Fact]
    public async Task SetEnabled_Toggles()
    {
        var reg = new PacaPluginRegistry(new RecordingRuntime());
        await reg.InstallAsync(GoodManifest(), "default");
        reg.SetEnabled("com.paca.bdd", false);
        Assert.False(reg.Get("com.paca.bdd")!.Enabled);
    }

    [Fact]
    public void CompareSemver_DetectsOrdering()
    {
        Assert.True(PacaPluginRegistry.CompareSemver("2.0.0", "1.5.9") > 0);
        Assert.True(PacaPluginRegistry.CompareSemver("1.0.0", "1.0.0") == 0);
        Assert.True(PacaPluginRegistry.CompareSemver("1.0.0", "1.0.1") < 0);
    }

    [Fact]
    public void CompareSemver_StripsPrerelease()
    {
        Assert.Equal(0, PacaPluginRegistry.CompareSemver("1.2.3-alpha", "1.2.3-beta"));
    }

    private sealed class RecordingRuntime : IPluginRuntimeHost
    {
        public bool InstallCalled   { get; private set; }
        public bool UninstallCalled { get; private set; }
        public bool UpgradeCalled   { get; private set; }

        public ValueTask InstallAsync  (InstalledPlugin p,   CancellationToken ct = default) { InstallCalled = true;   return ValueTask.CompletedTask; }
        public ValueTask UninstallAsync(string id, bool dropArtifacts, CancellationToken ct = default) { UninstallCalled = true; return ValueTask.CompletedTask; }
        public ValueTask UpgradeAsync (InstalledPlugin from, InstalledPlugin to, CancellationToken ct = default) { UpgradeCalled = true; return ValueTask.CompletedTask; }
    }
}
