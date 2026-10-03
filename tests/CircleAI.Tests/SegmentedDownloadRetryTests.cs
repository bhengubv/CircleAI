// SegmentedDownloadRetryTests.cs
//
// A 21.3 GB weight file reached 9 732 062 702 bytes on a Circle OS device and
// STOPPED. Eight sockets quiet, the process alive, the resume marker untouched for
// eighteen minutes, and not one line in the log.
//
// TWO FAULTS, BOTH IN THE SEGMENTED PATH ONLY:
//
//   1. It called Stream.ReadAsync raw. The SEQUENTIAL path has gone through
//      ReadOrStallAsync since a download sat silent once before - the remark on
//      StallTimeout says "nothing was wrong except that nobody noticed" - and the
//      segmented path, added later for exactly the files big enough to need it,
//      never got it. A read that never returns is not an error anybody can catch,
//      so the whole retry-and-resume machinery sat there unreachable.
//
//   2. One failing segment abandoned the other seven. The catch returned false to
//      fall back to the sequential path, which sees a file already preallocated to
//      full length and starts from zero. Nine and a half gigabytes, thrown away by
//      a hiccup - when each segment is independently resumable from its own offset,
//      which is the entire reason the marker exists.
//
// The file here is just over the 64 MB threshold that turns the parallel path on,
// which is the only way to reach the code under test.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class SegmentedDownloadRetryTests : IDisposable
{
    private const int Total = 68 * 1024 * 1024;     // over ParallelThresholdBytes (64 MB)

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "circleai-seg-" + Guid.NewGuid().ToString("N"));

    public SegmentedDownloadRetryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A deterministic body, so a segment landing at the wrong offset is visible.</summary>
    private static byte Byte(long offset) => (byte)(offset % 251);

    [Fact]
    public async Task A_segment_whose_socket_dies_is_reconnected_rather_than_losing_the_download()
    {
        // THE HICCUP THAT USED TO COST EVERYTHING. One segment's first request dies
        // mid-body; every other segment is untouched.
        var handler = new RangeHandler { BreakSegmentAtByte = 1024 };
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_dir, http);

        var path = await svc.EnsureModelAsync(
            "weight", new Uri("https://example.invalid/llm.mnn.weight"), null, null, default);

        Assert.Equal(Total, new FileInfo(path).Length);
        await AssertWholeFileIsRight(path);
        Assert.True(handler.Breaks > 0, "the test never actually broke a connection");
    }

    [Fact]
    public async Task Every_byte_lands_at_its_own_offset()
    {
        // Eight sockets writing into one file at eight offsets: a segment off by so
        // much as a byte is 68 MB of garbage that only a SHA-256 would catch, hours
        // later, on a phone.
        var handler = new RangeHandler();
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_dir, http);

        var path = await svc.EnsureModelAsync(
            "clean", new Uri("https://example.invalid/llm.mnn.weight"), null, null, default);

        await AssertWholeFileIsRight(path);
    }

    [Fact]
    public async Task The_resume_marker_is_cleared_once_the_file_is_whole()
    {
        // A marker outliving its download is the exact state that cost three days:
        // housekeeping reads it as a live download and refuses to tidy, and a resume
        // reads it as progress into a file that is already finished.
        var handler = new RangeHandler();
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_dir, http);

        await svc.EnsureModelAsync(
            "tidy", new Uri("https://example.invalid/llm.mnn.weight"), null, null, default);

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.parts", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_resume_does_not_read_a_preallocated_file_as_a_finished_one()
    {
        // THE BUG THE FIRST FIX UNCOVERED. The segmented path preallocates the temp
        // file to the full size, so a 45%-fetched 21.3 GB .tmp measures 21.3 GB.
        // Taking that as "already have" makes the resume skip the parallel path
        // (nothing left to split) and ask the server for a range past the end.
        //
        // It was invisible for as long as housekeeping deleted the .tmp every hour.
        // Keeping those bytes - which is the whole point - is what exposed it.
        var temp = Path.Combine(_dir, "half.tmp");
        using (var fs = new FileStream(temp, FileMode.Create)) fs.SetLength(Total);
        File.WriteAllLines(DownloadSidecar.For(temp),
        [
            $"0,{Total / 4},{Total / 2 - 1}",
            $"{Total / 2},{Total / 2},{Total - 1}",
        ]);

        var handler = new RangeHandler();
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_dir, http);

        var path = await svc.EnsureModelAsync(
            "half", new Uri("https://example.invalid/llm.mnn.weight"), null, null, default);

        // It finished rather than asking for bytes past the end of the file.
        Assert.Equal(Total, new FileInfo(path).Length);
        await AssertWholeFileIsRight(path);
    }

    [Fact]
    public async Task A_failed_parallel_attempt_keeps_its_bytes_instead_of_truncating_them()
    {
        // THE WORST OF THE FAMILY, AND THE ONE THAT ACTUALLY DESTROYED THE BYTES.
        // Measured on a Circle OS device, 2026-10-04: 13 666 174 991 of
        // 21 346 165 150 bytes were down when Android froze the process. The
        // segments died, the parallel attempt gave up, and the SEQUENTIAL fallback
        // opened FileMode.Create over a file with nothing contiguous at the front.
        // Thirteen and a half gigabytes, truncated, and the next marker was a fresh
        // plan from zero. A download that has to be resumed four times still
        // finishes; one that restarts every time never does.
        var handler = new RangeHandler { FailEverySegment = true };
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_dir, http);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureModelAsync(
            "kept", new Uri("https://example.invalid/llm.mnn.weight"), null, null, default));

        var marker = Directory.EnumerateFiles(_dir, "*.parts", SearchOption.AllDirectories).Single();
        Assert.True(DownloadSidecar.Written(marker) > 0, "gave up without keeping anything");
    }

    [Fact]
    public async Task A_segment_that_ends_early_is_not_mistaken_for_one_that_finished()
    {
        // A server closing cleanly part way returns 0 from the read, the loop exits,
        // and the segment used to return as though its range were complete - so
        // WhenAll succeeded, the marker was DELETED, and the caller was handed a
        // file with a hole in it. The SHA-256 catches that hours later, on a phone,
        // with nothing left to resume from.
        var handler = new RangeHandler { EndEverySegmentEarly = true };
        using var http = new HttpClient(handler);
        using var svc = new ModelDownloadService(_dir, http);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureModelAsync(
            "short", new Uri("https://example.invalid/llm.mnn.weight"), null, null, default));

        // Not presented as finished, and what did arrive is still resumable.
        Assert.False(File.Exists(Path.Combine(_dir, "short", "llm.mnn.weight")));
        var marker = Directory.EnumerateFiles(_dir, "*.parts", SearchOption.AllDirectories).Single();
        Assert.True(DownloadSidecar.Written(marker) > 0);
    }

    [Fact]
    public async Task A_read_that_never_returns_becomes_an_ordinary_failure()
    {
        // THE FAULT ITSELF, at a tenth of a second instead of forty-five. A socket
        // that goes quiet must raise something catchable; a read that blocks forever
        // is a download nobody can rescue, which is what the device was doing.
        using var silent = new SilentStream();
        var buf = new byte[4096];

        var read = ModelDownloadService.ReadOrStallForTesting(
            silent, buf, TimeSpan.FromMilliseconds(100), default);

        await Assert.ThrowsAsync<IOException>(async () => await read);
    }

    [Fact]
    public async Task Somebody_pressing_stop_is_not_mistaken_for_a_dead_network()
    {
        // A person cancelling must not be retried at them.
        using var silent = new SilentStream();
        using var cts = new CancellationTokenSource();
        var buf = new byte[4096];

        var read = ModelDownloadService.ReadOrStallForTesting(
            silent, buf, TimeSpan.FromSeconds(30), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
    }

    private static async Task AssertWholeFileIsRight(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var buf = new byte[81_920];
        long at = 0;
        int n;
        while ((n = await fs.ReadAsync(buf)) > 0)
        {
            for (var i = 0; i < n; i++, at++)
                if (buf[i] != Byte(at))
                    Assert.Fail($"byte {at} is {buf[i]}, expected {Byte(at)}");
        }
        Assert.Equal(Total, at);
    }

    /// <summary>Serves byte ranges out of a generated body, optionally dropping one.</summary>
    private sealed class RangeHandler : HttpMessageHandler
    {
        /// <summary>Kill the first request for the segment covering this offset, after a few bytes.</summary>
        public long? BreakSegmentAtByte { get; init; }

        /// <summary>Every segment dies part way through, every time: the frozen-process case.</summary>
        public bool FailEverySegment { get; init; }

        /// <summary>Every segment's body ends cleanly before its range does.</summary>
        public bool EndEverySegmentEarly { get; init; }

        private int _broken;
        public int Breaks => _broken;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = range?.From ?? 0;
            var to = range?.To ?? Total - 1;

            var breakIt = FailEverySegment
                       || (BreakSegmentAtByte is { } b
                           && from <= b && b <= to
                           && Interlocked.CompareExchange(ref _broken, 1, 0) == 0);

            var body = breakIt || EndEverySegmentEarly
                ? new RangeStream(from, to, breakAfter: 64 * 1024, endCleanly: EndEverySegmentEarly)
                : new RangeStream(from, to, breakAfter: null, endCleanly: false);

            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(body),
            };
            resp.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, Total);
            resp.Content.Headers.ContentLength = to - from + 1;
            return Task.FromResult(resp);
        }
    }

    /// <summary>The generated body for one range, which can die or end short part way through.</summary>
    private sealed class RangeStream(long from, long to, long? breakAfter, bool endCleanly) : Stream
    {
        private long _at = from;
        private long _served;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (breakAfter is { } limit && _served >= limit)
            {
                // Cleanly: EOF, which is the case that used to read as "finished".
                if (endCleanly) return 0;
                throw new IOException("the connection went away");
            }

            if (_at > to) return 0;

            var n = (int)Math.Min(count, to - _at + 1);
            if (breakAfter is { } cap) n = (int)Math.Min(n, cap - _served);
            for (var i = 0; i < n; i++) buffer[offset + i] = Byte(_at + i);

            _at += n;
            _served += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => to - from + 1;
        public override long Position { get => _at - from; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin w) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>A connection that accepted the request and then said nothing, ever.</summary>
    private sealed class SilentStream : Stream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin w) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
