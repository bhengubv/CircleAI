// ResumeRangeSemanticsTests.cs
//
// Resuming a download must be decided by what the server actually SENT, not by
// its status line.
//
// The two ModelScope endpoints we use disagree about how to answer a Range
// request, and only one of them says 206:
//
//   resolve/master/…           → 206 Partial Content
//   api/v1/…/repo?FilePath=…   → 200 OK, but WITH a Content-Range header
//
// Treating the second as "server ignored my Range" meant discarding the partial
// file and then writing the ranged TAIL into a fresh file as if it were whole.
// Alternating retries between the two endpoints stacked tail onto tail: a 450 MB
// weight file landed as 775 MB, failed its hash, and was deleted — leaving a
// model directory that every subsequent launch treated as already downloaded.
// The chat model was dead on the P30 Lite from that first interrupted fetch
// until this was found.

using System.Net;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ResumeRangeSemanticsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "circleai-resume-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly byte[] Whole = CreatePayload(64 * 1024);

    private static byte[] CreatePayload(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i % 251);   // position-dependent: duplication shows up
        return b;
    }

    /// <summary>Serves <see cref="Whole"/>, answering Range the way a given endpoint does.</summary>
    private sealed class RangeServer : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusForRange;
        public int Requests { get; private set; }

        public RangeServer(HttpStatusCode statusForRange) => _statusForRange = statusForRange;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range?.From is { } from)
            {
                var tail = Whole.Skip((int)from).ToArray();
                var partial = new HttpResponseMessage(_statusForRange)
                {
                    Content = new ByteArrayContent(tail),
                };
                partial.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(from, Whole.Length - 1, Whole.Length);
                return Task.FromResult(partial);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Whole),
            });
        }
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    private async Task<byte[]> ResumeWith(HttpStatusCode statusForRange, int alreadyOnDisk)
    {
        Directory.CreateDirectory(_root);
        var handler = new RangeServer(statusForRange);
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_root, http);

        // Simulate a fetch that died partway: the temp file holds a valid prefix.
        var modelDir = Path.Combine(_root, "M");
        Directory.CreateDirectory(modelDir);
        await File.WriteAllBytesAsync(Path.Combine(modelDir, "w.bin.tmp"), Whole.Take(alreadyOnDisk).ToArray());

        var spec = new[] { new BundleFileSpec("w.bin", Sha256Hex(Whole), Whole.Length) };
        await svc.EnsureBundleAsync("M", "any/repo", CircleAI.Core.ModelSource.HuggingFace,
                                    spec, (IProgress<double>?)null, CancellationToken.None);

        return await File.ReadAllBytesAsync(Path.Combine(modelDir, "w.bin"));
    }

    [Fact]
    public async Task Resuming_against_a_206_server_reconstructs_the_file_exactly()
    {
        var got = await ResumeWith(HttpStatusCode.PartialContent, alreadyOnDisk: 20_000);
        Assert.Equal(Whole.Length, got.Length);
        Assert.Equal(Whole, got);
    }

    [Fact]
    public async Task Resuming_against_a_200_with_ContentRange_ALSO_reconstructs_the_file_exactly()
    {
        // The case that was broken. The server honoured the range and merely said
        // 200 while doing so; the file must still come out byte-perfect.
        var got = await ResumeWith(HttpStatusCode.OK, alreadyOnDisk: 20_000);
        Assert.Equal(Whole.Length, got.Length);
        Assert.Equal(Whole, got);
    }

    [Fact]
    public async Task A_partial_file_never_produces_a_file_LONGER_than_the_original()
    {
        // The specific corruption seen on the phone: 450 MB expected, 775 MB
        // written, because tails were appended to tails.
        foreach (var status in new[] { HttpStatusCode.PartialContent, HttpStatusCode.OK })
        {
            var got = await ResumeWith(status, alreadyOnDisk: 50_000);
            Assert.True(got.Length <= Whole.Length,
                $"{status}: wrote {got.Length} bytes for a {Whole.Length}-byte file");
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* temp dir */ }
    }
}
