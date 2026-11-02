// CarriedSidecarsTests.cs
//
// A FILE THE ASSEMBLY CARRIES IS NEVER FETCHED. The voice sidecars ship embedded
// (EmbeddedVoiceConfigs) because most of their addresses were never published.
// The downloader wrote the embedded copy only when nothing was on disk, so a copy
// on disk that failed its pin went to the network instead - to the dead address.
// C4 made that real: mms-amh and mms-tir were re-pinned at their committed LF
// bytes, and a phone holding the old CRLF pair fails the pin on its next check.

using System.Net;
using System.Security.Cryptography;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class CarriedSidecarsTests : IDisposable
{
    private const string Sidecar = "mms-amh/model.onnx.json";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "circleai-carried-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Counts every request and answers each with a 404: the address that was never published.</summary>
    private sealed class DeadAddress : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Serves one payload, whole or from a Range's start.</summary>
    private sealed class Serving : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public int Requests { get; private set; }

        public Serving(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload.Skip((int)from).ToArray()),
            };
            if (from > 0)
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(from, _payload.Length - 1, _payload.Length);
            return Task.FromResult(response);
        }
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    // The same bytes with every line break written as CRLF - what a core.autocrlf checkout wrote.
    private static byte[] WithCrlf(byte[] lf)
    {
        var crlf = new List<byte>(lf.Length + 128);
        for (var i = 0; i < lf.Length; i++)
        {
            if (lf[i] == (byte)'\n' && (i == 0 || lf[i - 1] != (byte)'\r')) crlf.Add((byte)'\r');
            crlf.Add(lf[i]);
        }
        return crlf.ToArray();
    }

    private async Task<(byte[] OnDisk, int Requests)> EnsureSidecar(byte[]? alreadyOnDisk)
    {
        var carried = EmbeddedVoiceConfigs.TryGet(Sidecar)!;
        var path = Path.Combine(_root, "V", "mms-amh", "model.onnx.json");
        if (alreadyOnDisk is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, alreadyOnDisk);
        }

        var handler = new DeadAddress();
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_root, http);
        var spec = new[] { new BundleFileSpec(Sidecar, Sha256Hex(carried), carried.Length) };
        await svc.EnsureBundleAsync("V", "any/repo", CircleAI.Core.ModelSource.HuggingFace,
                                    spec, (IProgress<double>?)null, CancellationToken.None);
        return (await File.ReadAllBytesAsync(path), handler.Requests);
    }

    [Fact]
    public async Task A_stale_copy_of_a_sidecar_we_carry_is_rewritten_from_the_assembly_and_never_fetched()
    {
        // The pair a phone holds after C4: the same JSON, its line breaks CRLF.
        var carried = EmbeddedVoiceConfigs.TryGet(Sidecar)!;
        var stale = WithCrlf(carried);
        Assert.NotEqual(carried, stale);

        var (onDisk, requests) = await EnsureSidecar(stale);

        Assert.Equal(carried, onDisk);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task A_missing_sidecar_we_carry_is_written_from_the_assembly_and_never_fetched()
    {
        var (onDisk, requests) = await EnsureSidecar(alreadyOnDisk: null);

        Assert.Equal(EmbeddedVoiceConfigs.TryGet(Sidecar), onDisk);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task A_file_we_do_not_carry_that_fails_its_pin_is_still_fetched()
    {
        // The rewrite is for what the assembly carries; anything else that fails its pin
        // is fetched again, as before.
        var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var path = Path.Combine(_root, "W", "w.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        var handler = new Serving(payload);
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_root, http);
        await svc.EnsureBundleAsync("W", "any/repo", CircleAI.Core.ModelSource.HuggingFace,
                                    new[] { new BundleFileSpec("w.bin", Sha256Hex(payload), payload.Length) },
                                    (IProgress<double>?)null, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.True(handler.Requests > 0, "a file the assembly does not carry has to come from the network");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* temp dir */ }
    }
}
