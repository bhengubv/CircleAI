// EndToEndIntegrationTests.cs
//
// Glue check — verifies that the documented happy path
// (ICapabilityProbe -> IBackendSelector -> INativeRuntimeFetcher)
// composes into a runnable install for a representative host shape.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;
using CircleAI.Runtime.NativeRuntimes;
using Xunit;

namespace CircleAI.Runtime.Tests;

public sealed class EndToEndIntegrationTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly string _cacheRoot;

    public EndToEndIntegrationTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(),
            "circleai-runtime-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task NvidiaWorkstation_Linux_X64_Probes_To_Cuda_And_Fetches_Linux_X64_Cuda_Bundle()
    {
        // 1. Synthesise a probe result for a Linux x64 host with an RTX 4080.
        var profile = new HostProfile(
            OperatingSystemKind.Linux, "Ubuntu 22.04",
            ArchitectureKind.X64, "AMD Ryzen 9 7950X",
            32, 16, 64 * GiB,
            new GpuInfo(GpuVendor.Nvidia, "RTX 4080", 16 * GiB, "555.42.03"),
            null,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        // 2. Selector picks the backend.
        var selection = new BackendSelector().Select(profile, CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Cuda, selection.Backend);
        Assert.True(selection.ActualTier >= CapabilityTier.Tier2_Medium);

        // 3. Registry returns a real bundle for that tuple.
        var bundle = NativeRuntimeRegistry.LoadEmbedded()
            .Find(profile.Os, profile.Arch, selection.Backend);
        Assert.NotNull(bundle);
        Assert.Equal("modelscope.cn", bundle!.PrimaryUri.Host);

        // 4. Fetcher (mocked) downloads + extracts + returns runnable install.
        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            [bundle.MnnBridgeLibraryName] = "BRIDGE-PAYLOAD",
            [bundle.MnnCoreLibraryName]   = "CORE-PAYLOAD",
        });
        var handler = new FakeHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var registry = SingleBundleRegistry(bundle);
        using var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var install = await fetcher.EnsureRuntimeAsync(
            profile.Os, profile.Arch, selection.Backend);
        Assert.True(File.Exists(install.MnnBridgePath));
        Assert.True(File.Exists(install.MnnCorePath));
        Assert.Equal("BRIDGE-PAYLOAD", await File.ReadAllTextAsync(install.MnnBridgePath));
    }

    [Fact]
    public async Task AppleSilicon_Macbook_Probes_To_Metal_And_Fetches_MacOS_Arm64_Metal_Bundle()
    {
        var profile = new HostProfile(
            OperatingSystemKind.MacOS, "14.5",
            ArchitectureKind.Arm64, "Apple M3 Pro",
            12, 12, 36 * GiB,
            new GpuInfo(GpuVendor.Apple, "Apple M3 Pro GPU", 36 * GiB, null),
            new NpuInfo(NpuVendor.AppleNeuralEngine, "Apple Neural Engine"),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var selection = new BackendSelector().Select(profile, CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Metal, selection.Backend);

        var bundle = NativeRuntimeRegistry.LoadEmbedded()
            .Find(profile.Os, profile.Arch, selection.Backend);
        Assert.NotNull(bundle);

        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            [bundle!.MnnBridgeLibraryName] = "MAC-BRIDGE",
            [bundle.MnnCoreLibraryName]    = "MAC-CORE",
        });
        var handler = new FakeHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var registry = SingleBundleRegistry(bundle);
        using var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var install = await fetcher.EnsureRuntimeAsync(
            profile.Os, profile.Arch, selection.Backend);
        Assert.True(File.Exists(install.MnnBridgePath));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static NativeRuntimeRegistry SingleBundleRegistry(NativeRuntimeBundle bundle)
    {
        var json = $"{{\"mnn_versions\":[{{\"version\":\"{bundle.MnnVersion}\",\"bundles\":[" +
                   $"{{\"os\":\"{bundle.Os}\",\"arch\":\"{bundle.Arch}\",\"backend\":\"{bundle.Backend}\"," +
                   $"\"url\":\"{bundle.PrimaryUri}\",\"mnnbridge_lib\":\"{bundle.MnnBridgeLibraryName}\"," +
                   $"\"mnn_lib\":\"{bundle.MnnCoreLibraryName}\"}}]}}]}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return NativeRuntimeRegistry.LoadFromStream(stream);
    }

    private static byte[] MakeZipArchiveBytes(IDictionary<string, string> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var kv in entries)
            {
                var e = zip.CreateEntry(kv.Key);
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes(kv.Value);
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request, cancellationToken));
    }
}
