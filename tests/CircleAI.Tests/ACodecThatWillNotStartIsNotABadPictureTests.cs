// ACodecThatWillNotStartIsNotABadPictureTests.cs
//
// ImageDecoder.Decode DOCUMENTS TWO DIFFERENT FAILURES AND USED TO CONFLATE THEM:
//
//     /// <exception cref="PlatformNotSupportedException">This platform has no codec for the format.</exception>
//     /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
//
// A caller that catches the first - to fall back to asking for a PNG, or to tell
// somebody vision needs a codec - got the second instead, unhandled, on any machine
// whose codec would not start.
//
// FOUND ON 2026-10-24 BY A FULL net10 RUN. ImageDecoderPlatformTests'
// A_lossless_WebP_comes_back_pixel_for_pixel failed with "Expected
// PlatformNotSupportedException, Actual InvalidDataException" while the box was
// thrashing, and 14 of 14 in that class passed in 69 ms once it was quiet. The HRESULT
// was 0x88982F8B - COMPONENTINITIALIZEFAILURE, meaning the WebP extension IS installed
// and registered but could not be STARTED - and only COMPONENTNOTFOUND (0x88982F50)
// was recognised, so it fell through to the catch-all and came back as "Windows could
// not decode 56 byte(s) as an image". The bytes were a valid 56-byte lossless WebP.
//
// WHY THE EXISTING TEST COULD NEVER HAVE CAUGHT IT. It decodes for real, so it asserts
// whichever branch the machine allows: with a working codec it takes the happy path and
// the mapping below is never reached at all. A test that must arrange for a codec to
// fail in order to check what happens when a codec fails is a test of the machine.
// These go at WicImageDecoder.Explain instead - the one place that decides - so they
// run the same on a box with every codec and a box with none.

using System;
using System.IO;
using System.Linq;
using CircleAI.Vision;
using Xunit;

namespace CircleAI.Tests;

public sealed class ACodecThatWillNotStartIsNotABadPictureTests
{
    // The real HRESULTs, written out here rather than imported, so that a change to the
    // constants in the decoder has to disagree with a number in a test before it ships.
    private const int ComponentNotFound          = unchecked((int)0x88982F50);
    private const int InvalidRegistration        = unchecked((int)0x88982F8A);
    private const int ComponentInitializeFailure = unchecked((int)0x88982F8B);

    private const int UnknownImageFormat = unchecked((int)0x88982F07);
    private const int BadImage           = unchecked((int)0x88982F60);
    private const int BadHeader          = unchecked((int)0x88982F61);
    private const int BadStreamData      = unchecked((int)0x88982F70);
    private const int EFail              = unchecked((int)0x80004005);

    // ── the machine's fault ──────────────────────────────────────────────────

    [Theory]
    [InlineData(ComponentInitializeFailure)]   // registered, would not start — the 2026-10-24 failure
    [InlineData(InvalidRegistration)]          // registered wrongly
    [InlineData(ComponentNotFound)]            // missing outright, past the one step where that means bad bytes
    public void A_codec_Windows_cannot_give_us_is_the_platforms_fault(int hr)
        => Assert.IsType<PlatformNotSupportedException>(
            WicImageDecoder.Explain(hr, "IWICBitmapDecoder::GetFrame", 56, EncodedImageKind.WebP));

    [Fact]
    public void The_exact_HRESULT_that_broke_the_build_is_the_documented_type()
    {
        // THE REGRESSION, BY NUMBER. 0x88982F8B is the one that cost a full test run,
        // and the one thing worth asserting about it is the exception type a caller
        // catches - not the wording, which is free to improve.
        var ex = WicImageDecoder.Explain(
            ComponentInitializeFailure, "IWICImagingFactory::CreateDecoderFromStream", 56, EncodedImageKind.WebP);

        Assert.IsType<PlatformNotSupportedException>(ex);
    }

    [Fact]
    public void It_says_which_format_and_which_call_so_a_reader_knows_where_to_look()
    {
        var ex = WicImageDecoder.Explain(
            ComponentInitializeFailure, "IWICBitmapDecoder::GetFrame", 56, EncodedImageKind.WebP);

        Assert.Contains("WebP", ex.Message);
        Assert.Contains("IWICBitmapDecoder::GetFrame", ex.Message);
        Assert.Contains("88982F8B", ex.Message);
    }

    [Fact]
    public void It_does_not_blame_the_picture_when_the_codec_is_at_fault()
    {
        // THE WHOLE POINT, IN ONE ASSERTION. The old message was "Windows could not
        // decode 56 byte(s) as an image", which sends whoever reads it to look at the
        // picture - the one place the problem is not.
        var ex = WicImageDecoder.Explain(
            ComponentInitializeFailure, "IWICBitmapDecoder::GetFrame", 56, EncodedImageKind.WebP);

        Assert.DoesNotContain("could not decode", ex.Message);
    }

    // ── the picture's fault ──────────────────────────────────────────────────

    [Theory]
    [InlineData(UnknownImageFormat)]
    [InlineData(BadImage)]
    [InlineData(BadHeader)]
    [InlineData(BadStreamData)]
    [InlineData(EFail)]
    public void Bytes_that_are_not_a_decodable_image_stay_invalid_data(int hr)
        => Assert.IsType<InvalidDataException>(
            WicImageDecoder.Explain(hr, "IWICBitmapDecoder::GetFrame", 56, EncodedImageKind.Jpeg));

    [Fact]
    public void A_broken_picture_still_says_how_many_bytes_and_which_call_failed()
    {
        var ex = WicImageDecoder.Explain(BadHeader, "IWICBitmapDecoder::GetFrame", 56, EncodedImageKind.Jpeg);

        Assert.Contains("56 byte(s)", ex.Message);
        Assert.Contains("IWICBitmapDecoder::GetFrame", ex.Message);
        Assert.Contains("88982F61", ex.Message);
    }

    // ── and the contract the two of them make up ─────────────────────────────

    [Fact]
    public void Every_failure_is_one_of_the_two_documented_kinds()
    {
        // NOTHING ELSE MAY ESCAPE. ImageDecoder.Decode names exactly three exceptions -
        // ArgumentException for no bytes, and these two - and a caller handling all of
        // them should not be surprised by a fourth from a code nobody anticipated.
        int[] everyKindOfFailure =
        [
            ComponentNotFound, InvalidRegistration, ComponentInitializeFailure,
            UnknownImageFormat, BadImage, BadHeader, BadStreamData, EFail,
            unchecked((int)0x80070005),   // E_ACCESSDENIED
            unchecked((int)0x8007000E),   // E_OUTOFMEMORY
            unchecked((int)0x88982F8C),   // WINCODEC_ERR_INSUFFICIENTBUFFER
            -1,
        ];

        Assert.All(everyKindOfFailure, hr =>
        {
            var ex = WicImageDecoder.Explain(hr, "a step", 56, EncodedImageKind.Unknown);
            Assert.True(ex is PlatformNotSupportedException or InvalidDataException,
                $"0x{hr:X8} came back as {ex.GetType().Name}");
        });
    }

    [Fact]
    public void An_unknown_format_still_reads_as_a_sentence()
    {
        // Name(Unknown) is "image", so the message must not come out as "this Windows
        // could not give us a usable  codec" with a hole in it.
        var ex = WicImageDecoder.Explain(
            ComponentInitializeFailure, "a step", 56, EncodedImageKind.Unknown);

        Assert.Contains("image", ex.Message);
        Assert.DoesNotContain("  ", ex.Message);
    }

    [Fact]
    public void The_three_codec_codes_are_three_different_numbers()
    {
        // A copy-paste that gave two of them the same value would make one of them dead
        // and the mapping above would still pass.
        Assert.Equal(3, new[] { ComponentNotFound, InvalidRegistration, ComponentInitializeFailure }
            .Distinct().Count());
    }
}
