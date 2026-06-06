// NativeRuntimeFetcherTests.cs
//
// Verifies the cache-hit fast path, SHA-256 validation, atomic extraction,
// archive cleanup on failure, the fallback URI sequence, and the
// platform-aware recursive search for the MNN binary inside the real
// Alibaba bundle layout (deeply nested, e.g.
// lib/x64/Release/Dynamic/MD/MNN.dll on Windows or
// Dynamic/MNN.framework/Versions/A/MNN on macOS).
//
// Uses a fake HttpMessageHandler so no real network call is required.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;
using CircleAI.Runtime.NativeRuntimes;
using Xunit;

namespace CircleAI.Runtime.Tests;

public sealed class NativeRuntimeFetcherTests : IDisposable
{
    private readonly string _cacheRoot;

    public NativeRuntimeFetcherTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(),
            "circleai-runtime-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true); } catch { }
    }

    // ── Cache-hit fast path ───────────────────────────────────────────────────

    [Fact]
    public async Task EnsureRuntime_Returns_Cached_Install_Without_HTTP_When_Mnn_Already_On_Disk()
    {
        var bundle = MakeBundle(BackendKind.Cpu, sha: null);
        var registry = SingleBundleRegistry(bundle);
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, NeverCalledHttp());

        // Pre-populate at the real-layout nested path that the Alibaba
        // Windows bundle uses, so the fast path proves it can find MNN
        // anywhere under the extract root.
        var expectedDir = GetExpectedExtractDir(bundle);
        var nested = Path.Combine(expectedDir, "lib", "x64", "Release", "Dynamic", "MD");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, bundle.MnnCoreLibraryName), "stub");

        var progress = new TrackingProgress();
        var install = await fetcher.EnsureRuntimeAsync(
            bundle.Os, bundle.Arch, bundle.Backend, progress, CancellationToken.None);

        Assert.Equal(expectedDir, install.ExtractedRoot);
        Assert.True(File.Exists(install.MnnCorePath));
        Assert.EndsWith(bundle.MnnCoreLibraryName, install.MnnCorePath);
        Assert.Equal(1.0, progress.Reports.Last());
    }

    [Fact]
    public async Task IsRuntimeCached_Returns_True_When_Mnn_Is_Findable_At_Any_Depth()
    {
        var bundle = MakeBundle(BackendKind.Cpu, sha: null);
        var registry = SingleBundleRegistry(bundle);
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, NeverCalledHttp());

        Assert.False(await fetcher.IsRuntimeCachedAsync(bundle.Os, bundle.Arch, bundle.Backend));

        var dir = GetExpectedExtractDir(bundle);
        var nested = Path.Combine(dir, "lib", "x64", "Release", "Dynamic", "MD");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, bundle.MnnCoreLibraryName), "stub");

        Assert.True(await fetcher.IsRuntimeCachedAsync(bundle.Os, bundle.Arch, bundle.Backend));
    }

    // ── Slow path: download + extract ─────────────────────────────────────────

    [Fact]
    public async Task EnsureRuntime_Downloads_Then_Finds_Mnn_At_Real_Nested_Path()
    {
        // Build a zip mirroring Alibaba's real Windows layout:
        // top-level dir / lib / x64 / Release / Dynamic / MD / MNN.dll
        var bundle = MakeBundle(BackendKind.Cpu, sha: null);
        var registry = SingleBundleRegistry(bundle);

        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            ["mnn_3.5.0_windows_x64_cpu_opencl/lib/x64/Release/Dynamic/MD/MNN.dll"] = "MNN-MD",
            ["mnn_3.5.0_windows_x64_cpu_opencl/lib/x64/Release/Dynamic/MT/MNN.dll"] = "MNN-MT",
            ["mnn_3.5.0_windows_x64_cpu_opencl/lib/x64/Release/Static/MD/MNN.lib"]  = "STATIC-LIB",
        });

        var handler = new FakeHandler((req, ct) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var install = await fetcher.EnsureRuntimeAsync(bundle.Os, bundle.Arch, bundle.Backend);
        Assert.True(File.Exists(install.MnnCorePath));
        Assert.EndsWith("MNN.dll", install.MnnCorePath);

        // Windows preference: MD CRT should win over MT.
        var p = install.MnnCorePath.Replace('\\', '/');
        Assert.Contains("/Dynamic/MD/", p);
        Assert.DoesNotContain("/Dynamic/MT/", p);

        Assert.Equal("MNN-MD", await File.ReadAllTextAsync(install.MnnCorePath));
    }

    [Fact]
    public async Task EnsureRuntime_Throws_When_Mnn_Is_Missing_From_Bundle()
    {
        // Bundle that doesn't contain MNN.dll anywhere — fetcher must
        // raise a clear error citing the searched name.
        var bundle = MakeBundle(BackendKind.Cpu, sha: null);
        var registry = SingleBundleRegistry(bundle);

        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            ["mnn_3.5.0_windows_x64_cpu_opencl/lib/x64/Release/Dynamic/MD/SomethingElse.dll"] = "not-mnn",
        });

        var handler = new FakeHandler((req, ct) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.EnsureRuntimeAsync(bundle.Os, bundle.Arch, bundle.Backend));
        Assert.Contains("missing the MNN core library", ex.Message);
        Assert.Contains("MNN.dll", ex.Message);
    }

    [Fact]
    public async Task EnsureRuntime_Finds_Macos_Framework_Binary_Named_MNN_Under_MNN_framework_Path()
    {
        // Real macOS bundle ships the binary at Dynamic/MNN.framework/Versions/A/MNN
        // — a framework binary with no extension. Verify the fetcher's
        // macOS-specific branch finds it.
        var bundle = new NativeRuntimeBundle(
            MnnVersion: "9.9.9",
            Os: OperatingSystemKind.MacOS,
            Arch: ArchitectureKind.Arm64,
            Backend: BackendKind.Metal,
            PrimaryUri: new Uri("https://test.example/macos.zip"),
            FallbackUri: null,
            ArchiveSha256Hex: null,
            MnnCoreLibraryName: "MNN");
        var registry = SingleBundleRegistry(bundle);

        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            ["mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal/Dynamic/MNN.framework/Versions/A/MNN"] = "FRAMEWORK-MNN",
            ["mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal/Dynamic/MNN.framework/Versions/A/Resources/Info.plist"] = "<plist/>",
        });

        var handler = new FakeHandler((req, ct) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var install = await fetcher.EnsureRuntimeAsync(bundle.Os, bundle.Arch, bundle.Backend);
        Assert.True(File.Exists(install.MnnCorePath));
        var p = install.MnnCorePath.Replace('\\', '/');
        Assert.Contains("/MNN.framework/Versions/A/MNN", p);
        Assert.Equal("FRAMEWORK-MNN", await File.ReadAllTextAsync(install.MnnCorePath));
    }

    // ── SHA-256 verification ─────────────────────────────────────────────────

    [Fact]
    public async Task EnsureRuntime_Rejects_Download_When_Sha256_Mismatches()
    {
        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            ["nested/Dynamic/MD/MNN.dll"] = "CORE",
        });

        var wrongSha = new string('A', 64);
        var bundle = MakeBundle(BackendKind.Cpu, sha: wrongSha);
        var registry = SingleBundleRegistry(bundle);

        var handler = new FakeHandler((req, ct) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.EnsureRuntimeAsync(bundle.Os, bundle.Arch, bundle.Backend));
        Assert.Contains("SHA-256 mismatch", ex.Message);

        var leaked = Directory.EnumerateFiles(_cacheRoot, "*.partial").ToList();
        Assert.Empty(leaked);
    }

    [Fact]
    public async Task EnsureRuntime_Accepts_Download_With_Matching_Sha256()
    {
        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            ["nested/Dynamic/MD/MNN.dll"] = "CORE",
        });
        var sha = Convert.ToHexString(SHA256.HashData(archive));
        var bundle = MakeBundle(BackendKind.Cpu, sha: sha);
        var registry = SingleBundleRegistry(bundle);

        var handler = new FakeHandler((req, ct) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var install = await fetcher.EnsureRuntimeAsync(bundle.Os, bundle.Arch, bundle.Backend);
        Assert.True(File.Exists(install.MnnCorePath));
    }

    // ── Unregistered tuple ───────────────────────────────────────────────────

    [Fact]
    public async Task EnsureRuntime_Throws_When_Tuple_Not_Registered()
    {
        var bundle = MakeBundle(BackendKind.Cpu, sha: null);
        var registry = SingleBundleRegistry(bundle);
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, NeverCalledHttp());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.EnsureRuntimeAsync(
                OperatingSystemKind.IOS, ArchitectureKind.X86, BackendKind.Cambricon));
        Assert.Contains("No native runtime bundle registered", ex.Message);
    }

    // ── Fallback URI ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureRuntime_Falls_Back_To_Secondary_URI_When_Primary_Fails()
    {
        var archive = MakeZipArchiveBytes(new Dictionary<string, string>
        {
            ["nested/Dynamic/MD/MNN.dll"] = "CORE",
        });

        var bundle = new NativeRuntimeBundle(
            "9.9.9", OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu,
            new Uri("https://primary.invalid/runtime.zip"),
            new Uri("https://fallback.invalid/runtime.zip"),
            null, "MNN.dll");
        var registry = SingleBundleRegistry(bundle);

        var calls = new List<Uri>();
        var handler = new FakeHandler((req, ct) =>
        {
            calls.Add(req.RequestUri!);
            if (req.RequestUri!.Host.Contains("primary"))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        });
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        var install = await fetcher.EnsureRuntimeAsync(bundle.Os, bundle.Arch, bundle.Backend);
        Assert.True(File.Exists(install.MnnCorePath));
        Assert.Equal(2, calls.Count);
        Assert.Contains("primary",  calls[0].Host);
        Assert.Contains("fallback", calls[1].Host);
    }

    [Fact]
    public async Task EnsureRuntime_Throws_When_Both_Primary_And_Fallback_Fail()
    {
        var bundle = new NativeRuntimeBundle(
            "9.9.9", OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu,
            new Uri("https://primary.invalid/runtime.zip"),
            new Uri("https://fallback.invalid/runtime.zip"),
            null, "MNN.dll");
        var registry = SingleBundleRegistry(bundle);
        var handler = new FakeHandler((req, ct) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        await Assert.ThrowsAsync<AggregateException>(() =>
            fetcher.EnsureRuntimeAsync(bundle.Os, bundle.Arch, bundle.Backend));
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureRuntime_Honours_Cancellation()
    {
        var bundle = MakeBundle(BackendKind.Cpu, sha: null);
        var registry = SingleBundleRegistry(bundle);
        var handler = new FakeHandler((req, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        });
        var fetcher = new NativeRuntimeFetcher(_cacheRoot, registry, new HttpClient(handler));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fetcher.EnsureRuntimeAsync(bundle.Os, bundle.Arch, bundle.Backend, ct: cts.Token));
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    [Fact]
    public void ListAvailableBundles_Returns_All_Registered_Bundles()
    {
        var bundle1 = MakeBundle(BackendKind.Cpu,  sha: null);
        var bundle2 = MakeBundle(BackendKind.Cuda, sha: null);
        var registry = TwoBundleRegistry(bundle1, bundle2);
        var fetcher  = new NativeRuntimeFetcher(_cacheRoot, registry, NeverCalledHttp());

        var listed = fetcher.ListAvailableBundles();
        Assert.Equal(2, listed.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NativeRuntimeBundle MakeBundle(BackendKind backend, string? sha) =>
        new("9.9.9",
            OperatingSystemKind.Windows, ArchitectureKind.X64, backend,
            new Uri($"https://test.example/{backend}-runtime.zip"),
            null, sha,
            "MNN.dll");

    private static NativeRuntimeRegistry SingleBundleRegistry(NativeRuntimeBundle bundle)
    {
        var json = BuildRegistryJson(new[] { bundle });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return NativeRuntimeRegistry.LoadFromStream(stream);
    }

    private static NativeRuntimeRegistry TwoBundleRegistry(
        NativeRuntimeBundle a, NativeRuntimeBundle b)
    {
        var json = BuildRegistryJson(new[] { a, b });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return NativeRuntimeRegistry.LoadFromStream(stream);
    }

    private static string BuildRegistryJson(IEnumerable<NativeRuntimeBundle> bundles)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"mnn_versions\": [");
        var groups = bundles.GroupBy(b => b.MnnVersion);
        bool firstGroup = true;
        foreach (var g in groups)
        {
            if (!firstGroup) sb.Append(',');
            firstGroup = false;
            sb.Append($"{{\"version\":\"{g.Key}\",\"bundles\":[");
            bool first = true;
            foreach (var b in g)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{')
                  .Append($"\"os\":\"{b.Os}\",")
                  .Append($"\"arch\":\"{b.Arch}\",")
                  .Append($"\"backend\":\"{b.Backend}\",")
                  .Append($"\"url\":\"{b.PrimaryUri}\",")
                  .Append($"\"mnn_lib\":\"{b.MnnCoreLibraryName}\"");
                if (b.ArchiveSha256Hex is not null) sb.Append($",\"sha256\":\"{b.ArchiveSha256Hex}\"");
                if (b.FallbackUri is not null) sb.Append($",\"fallback_url\":\"{b.FallbackUri}\"");
                sb.Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private string GetExpectedExtractDir(NativeRuntimeBundle b) =>
        Path.Combine(_cacheRoot,
            $"{b.MnnVersion}-{b.Os.ToString().ToLowerInvariant()}-{b.Arch.ToString().ToLowerInvariant()}-{b.Backend.ToString().ToLowerInvariant()}");

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

    private static HttpClient NeverCalledHttp() =>
        new(new FakeHandler((req, ct) =>
            throw new InvalidOperationException("HTTP should NOT have been called.")));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request, cancellationToken));
    }

    private sealed class TrackingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = new();
        public void Report(double value) => Reports.Add(value);
    }
}
