// EndToEndIntegrationTests.cs
//
// Glue check — verifies that the documented happy path
// (ICapabilityProbe -> IBackendSelector -> INativeRuntimeFetcher)
// composes into a runnable install for a representative host shape,
// using the real nested layout Alibaba MNN bundles ship with.

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
    public async Task AmdLinuxWorkstation_X64_Probes_To_Vulkan_And_Fetches_Linux_X64_OpenCL_Bundle()
    {
        // 1. Synthesise a probe result for a Linux x64 host with an AMD RX 7800.
        //    AMD maps to Vulkan in the selector; Linux x64 ships with CPU+OpenCL
        //    in the real Alibaba bundle — same archive carries both libraries.
        //    Vulkan is not pre-built for Linux x64, so the realistic accelerator
        //    path is OpenCL (closest pre-built native surface from the same
        //    bundle).
        var profile = new HostProfile(
            OperatingSystemKind.Linux, "Ubuntu 22.04",
            ArchitectureKind.X64, "AMD Ryzen 9 7950X",
            32, 16, 64 * GiB,
            new GpuInfo(GpuVendor.Amd, "Radeon RX 7800 XT", 16 * GiB, "24.10"),
            null,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        // 2. Selector picks Vulkan for AMD with VRAM headroom.
        var selection = new BackendSelector().Select(profile, CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Vulkan, selection.Backend);
        Assert.True(selection.ActualTier >= CapabilityTier.Tier2_Medium);

        // 3. Registry: pick the OpenCL bundle (realistic fallback when
        //    Vulkan isn't pre-built).
        var bundle = NativeRuntimeRegistry.LoadEmbedded()
            .Find(profile.Os, profile.Arch, BackendKind.OpenCL);
        Assert.NotNull(bundle);
        Assert.Equal("github.com", bundle!.PrimaryUri.Host);

        // 4. Fetcher (mocked) downloads + extracts. Place MNN at a nested
        //    path mirroring Alibaba's real Linux layout.
        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            [$"mnn_3.5.0_linux_x64_cpu_opencl/lib/x64/{bundle.MnnCoreLibraryName}"] = "LINUX-MNN",
        });
        var handler = new FakeHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var registry = SingleBundleRegistry(bundle);
        using var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var install = await fetcher.EnsureRuntimeAsync(
            profile.Os, profile.Arch, BackendKind.OpenCL);
        Assert.True(File.Exists(install.MnnCorePath));
        Assert.EndsWith(bundle.MnnCoreLibraryName, install.MnnCorePath);
        Assert.Equal("LINUX-MNN", await File.ReadAllTextAsync(install.MnnCorePath));
    }

    [Fact]
    public async Task AppleSilicon_Macbook_Probes_To_Metal_And_Fetches_MacOS_Arm64_Framework_Binary()
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
        Assert.Equal("dec927b86f32ef4351c5af527d54ec0afe0bef0b9b1b2bf94e59e3ae55bf42eb",
            bundle!.ArchiveSha256Hex);
        // macOS bundle resolves the framework binary, no extension.
        Assert.Equal("MNN", bundle.MnnCoreLibraryName);

        // Real macOS layout: Dynamic/MNN.framework/Versions/A/MNN
        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            ["mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal/Dynamic/MNN.framework/Versions/A/MNN"] = "MAC-FRAMEWORK-MNN",
            ["mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal/Dynamic/MNN.framework/Versions/A/Resources/Info.plist"] = "<plist/>",
        });
        var handler = new FakeHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var registry = SingleBundleRegistry(bundle);
        using var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var install = await fetcher.EnsureRuntimeAsync(
            profile.Os, profile.Arch, selection.Backend);
        Assert.True(File.Exists(install.MnnCorePath));
        Assert.Contains("/MNN.framework/Versions/A/MNN",
            install.MnnCorePath.Replace('\\', '/'));
        Assert.Equal("MAC-FRAMEWORK-MNN", await File.ReadAllTextAsync(install.MnnCorePath));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static NativeRuntimeRegistry SingleBundleRegistry(NativeRuntimeBundle bundle)
    {
        var json = $"{{\"mnn_versions\":[{{\"version\":\"{bundle.MnnVersion}\",\"bundles\":[" +
                   $"{{\"os\":\"{bundle.Os}\",\"arch\":\"{bundle.Arch}\",\"backend\":\"{bundle.Backend}\"," +
                   $"\"url\":\"{bundle.PrimaryUri}\"," +
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
