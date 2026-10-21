// EncodedImage.cs
//
// What an encoded image IS, read from its first bytes, and the one sentence a caller
// gets when this platform cannot decode it.
//
// WHY SNIFF AT ALL, when every platform codec sniffs for itself. Because the codecs
// answer two different questions with the same word. WIC's "no component found" means
// both "these bytes are not an image" and "this Windows has no WebP decoder installed";
// ImageIO's empty image source means both "garbage" and "this iOS predates WebP". The
// first is the caller's problem and the second is the machine's, they have different
// fixes, and only the signature can tell them apart.

using System;

namespace CircleAI.Vision;

/// <summary>The encoded formats <see cref="ImageDecoder"/> recognises by signature.</summary>
internal enum EncodedImageKind
{
    /// <summary>No signature this library knows. A platform codec may still read it.</summary>
    Unknown,
    Png,
    Jpeg,
    WebP,
    Gif,
    Bmp,
    Tiff,
    /// <summary>HEIF or AVIF: an ISO-BMFF container with an image brand.</summary>
    Heif,
}

internal static class EncodedImage
{
    /// <summary>Recognises the format from its signature. Reads at most the first twelve bytes.</summary>
    public static EncodedImageKind Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G'
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return EncodedImageKind.Png;

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return EncodedImageKind.Jpeg;

        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8))
            return EncodedImageKind.WebP;

        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)))
            return EncodedImageKind.Gif;

        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M')
            return EncodedImageKind.Bmp;

        if (b.Length >= 4 && (b[..4].SequenceEqual("II*\0"u8) || b[..4].SequenceEqual("MM\0*"u8)))
            return EncodedImageKind.Tiff;

        if (b.Length >= 12 && b.Slice(4, 4).SequenceEqual("ftyp"u8) && IsHeifBrand(b.Slice(8, 4)))
            return EncodedImageKind.Heif;

        return EncodedImageKind.Unknown;
    }

    /// <summary>The format's name as a person would say it.</summary>
    public static string Name(EncodedImageKind kind) => kind switch
    {
        EncodedImageKind.Png => "PNG",
        EncodedImageKind.Jpeg => "JPEG",
        EncodedImageKind.WebP => "WebP",
        EncodedImageKind.Gif => "GIF",
        EncodedImageKind.Bmp => "BMP",
        EncodedImageKind.Tiff => "TIFF",
        EncodedImageKind.Heif => "HEIF",
        _ => "image",
    };

    /// <summary>The exception message for a format this platform has no codec for.</summary>
    /// <param name="kind">What the bytes are.</param>
    /// <param name="why">The platform's half of the sentence: what is missing, and where it comes from.</param>
    public static string NoCodec(EncodedImageKind kind, string why) =>
        $"Decoding this {Name(kind)} needs a platform codec, and {why}. PNG is read everywhere by PngReader, "
        + "and every pixel operation - letterbox, crop, resize, tensor fill, PNG encode - works here, so a caller "
        + "that already has the pixels can hand these APIs a decoded RasterImage instead.";

    private static bool IsHeifBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
        || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)
        || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8)
        || brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);
}
