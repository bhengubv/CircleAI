// AnimatedPngEncoder.cs
//
// (Rendering 1.0) A real, in-box IVideoEncoder that muxes a frame stream into
// an APNG — a genuinely playable, full-colour animated file with ZERO native
// dependencies (it reuses the PNG chunk writer + ZLibStream). This is the
// honest pure-managed "clip": browsers and many chat/preview surfaces render
// it directly.
//
// It is NOT H.264/MP4. Social platforms that demand MP4 need a real video
// encoder — on de-Googled Android that is AOSP MediaCodec (or FFmpeg) wired
// through this same IVideoEncoder seam from the hosting layer. NullVideoEncoder
// marks that gap. Frames are consumed lazily and streamed straight to the
// output, so only one frame is resident at a time.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Media.Rendering;

/// <summary>Encodes a frame sequence as an animated PNG (APNG).</summary>
public sealed class AnimatedPngEncoder : IVideoEncoder
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly AnimatedPngEncoder Instance = new();

    public string BackendId => "apng";
    public string OutputMimeType => "image/apng";

    public ValueTask<EncodedClip> EncodeAsync(
        IEnumerable<PixelBuffer> frames,
        ClipEncodeOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(options);
        return ValueTask.FromResult(Encode(frames, options, ct));
    }

    private static EncodedClip Encode(IEnumerable<PixelBuffer> frames, ClipEncodeOptions options, CancellationToken ct)
    {
        int delayDen = Math.Clamp(options.FrameRate <= 0 ? 12 : options.FrameRate, 1, 65535);
        int loop = Math.Max(0, options.LoopCount);

        using var e = frames.GetEnumerator();
        if (!e.MoveNext())
            return new EncodedClip(ReadOnlyMemory<byte>.Empty, "image/apng", 0, options.Size, options.FrameRate, "apng");

        var first = e.Current ?? throw new InvalidOperationException("Encoder received a null frame.");
        int w = first.Width, h = first.Height;

        using var ms = new MemoryStream();
        ms.Write(ImageCodecs.PngSignature, 0, ImageCodecs.PngSignature.Length);

        // IHDR (dimensions taken from the first frame).
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), h);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        ImageCodecs.WriteChunk(ms, 'I', 'H', 'D', 'R', ihdr);

        // acTL — num_frames is patched at the end once the true count is known.
        long acTLStart = ms.Position;
        Span<byte> actl = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl[..4], 0);
        BinaryPrimitives.WriteInt32BigEndian(actl.Slice(4, 4), loop);
        ImageCodecs.WriteChunk(ms, 'a', 'c', 'T', 'L', actl);

        uint seq = 0;
        int count = 0;

        // Frame 0: fcTL + IDAT (the default image doubles as the first frame).
        WriteFctl(ms, ref seq, w, h, delayDen);
        ImageCodecs.WriteChunk(ms, 'I', 'D', 'A', 'T', ImageCodecs.ZlibCompress(ImageCodecs.BuildFilteredScanlines(first)));
        count++;

        // Frames 1..n: fcTL + fdAT (sequence-number-prefixed).
        while (e.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            var frame = e.Current ?? throw new InvalidOperationException("Encoder received a null frame.");
            if (frame.Width != w || frame.Height != h)
                throw new InvalidOperationException("All APNG frames must share the first frame's dimensions.");

            WriteFctl(ms, ref seq, w, h, delayDen);
            byte[] comp = ImageCodecs.ZlibCompress(ImageCodecs.BuildFilteredScanlines(frame));
            byte[] fdat = new byte[4 + comp.Length];
            BinaryPrimitives.WriteUInt32BigEndian(fdat.AsSpan(0, 4), seq++);
            System.Buffer.BlockCopy(comp, 0, fdat, 4, comp.Length);
            ImageCodecs.WriteChunk(ms, 'f', 'd', 'A', 'T', fdat);
            count++;
        }

        ImageCodecs.WriteChunk(ms, 'I', 'E', 'N', 'D', ReadOnlySpan<byte>.Empty);

        // Patch acTL.num_frames and recompute its CRC.
        byte[] buf = ms.GetBuffer();
        int off = (int)acTLStart;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(off + 8, 4), count);
        uint crc = 0xFFFFFFFFu;
        crc = Crc32.Update(crc, buf.AsSpan(off + 4, 4)); // "acTL"
        crc = Crc32.Update(crc, buf.AsSpan(off + 8, 8)); // num_frames + num_plays
        crc ^= 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off + 16, 4), crc);

        return new EncodedClip(ms.ToArray(), "image/apng", count, new RenderSize(w, h), options.FrameRate, "apng");
    }

    private static void WriteFctl(Stream ms, ref uint seq, int w, int h, int delayDen)
    {
        Span<byte> f = stackalloc byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(f[..4], seq++);
        BinaryPrimitives.WriteInt32BigEndian(f.Slice(4, 4), w);
        BinaryPrimitives.WriteInt32BigEndian(f.Slice(8, 4), h);
        BinaryPrimitives.WriteInt32BigEndian(f.Slice(12, 4), 0); // x offset
        BinaryPrimitives.WriteInt32BigEndian(f.Slice(16, 4), 0); // y offset
        BinaryPrimitives.WriteUInt16BigEndian(f.Slice(20, 2), 1);               // delay_num
        BinaryPrimitives.WriteUInt16BigEndian(f.Slice(22, 2), (ushort)delayDen); // delay_den
        f[24] = 0; // dispose_op = NONE
        f[25] = 0; // blend_op   = SOURCE
        ImageCodecs.WriteChunk(ms, 'f', 'c', 'T', 'L', f);
    }
}
