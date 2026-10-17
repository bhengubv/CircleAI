// GraphPrecisionTests.cs
//
// THE GUARD DEFEATED ITSELF FOR FOUR MONTHS, AND NO TEST COULD HAVE SEEN IT.
//
// ZipformerKwsSpotter's constructor carried this warning, written by somebody
// who had already been bitten once:
//
//     "its output distribution is flat enough that keyword tokens sat around
//      p=0.01 where the float graphs put them above 0.3. Nothing errors; the
//      wake word simply never fires. Float is the default and int8 is opt-in."
//
// And three lines below it, the rule that was supposed to enforce that:
//
//     return all.FirstOrDefault(p => IsInt8(p) == preferQuantized) ?? all[0];
//
// The bundle we actually ship contains ONLY encoder.int8.onnx. The filter
// matched nothing. The `?? all[0]` handed back the int8 encoder anyway. The
// precaution became a comment describing the bug instead of preventing it.
//
// Measured on a P30 on 2026-10-11: "Hey B" compiles to "_HE Y _B", the first two
// tokens matched 1,796 times, and "_B" was never emitted once in ~14,000 frames
// - peak 0.0320, the comment's own int8 figure rather than its float one.
//
// THE RULE WAS UNTESTABLE, WHICH IS WHY IT WAS UNTESTED. It was a local function
// inside a constructor that needs three ONNX graphs, a tokens file and a bpe
// model present on disk, so no test project could construct one or reach it. It
// is a pure static over file names now, and the case that mattered - a bundle
// shipping only ONE precision - is the first assertion below.

using System;
using System.Collections.Generic;
using System.IO;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class GraphPrecisionTests
{
    /// <summary>What the shipped KWS-Zipformer-HeyB bundle actually contains.</summary>
    /// <remarks>
    /// From registry.json: an int8 encoder and FLOAT decoder and joiner. A mixed
    /// bundle, which is why "does it ship both?" was never obviously false.
    /// </remarks>
    private static readonly string[] AsShipped =
    [
        "/models/kws-hey-b/encoder.int8.onnx",
        "/models/kws-hey-b/decoder.onnx",
        "/models/kws-hey-b/joiner.onnx",
    ];

    private static readonly string[] BothPrecisions =
    [
        "/models/kws/encoder.onnx",
        "/models/kws/encoder.int8.onnx",
        "/models/kws/decoder.onnx",
        "/models/kws/decoder.int8.onnx",
        "/models/kws/joiner.onnx",
        "/models/kws/joiner.int8.onnx",
    ];

    // ── the bug ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_bundle_with_only_int8_is_loaded_but_NOT_reported_as_asked_for()
    {
        // THE ASSERTION THAT WAS MISSING. The old rule returned a path and
        // nothing else, so "got what I asked for" and "fell back to the one
        // graph the comment warns about" were the same answer.
        var pick = ZipformerKwsSpotter.PickGraph(AsShipped, "encoder", preferQuantized: false);

        Assert.Equal("/models/kws-hey-b/encoder.int8.onnx", pick.Path);
        Assert.True(pick.IsQuantized, "it IS the int8 graph, whatever was asked for");
        Assert.False(pick.AsAsked, "and the caller must be able to find that out");
    }

    [Fact]
    public void The_shipped_bundle_is_mixed_so_only_the_encoder_falls_back()
    {
        // Why this went unnoticed: the decoder and joiner ARE float, so any
        // spot-check of "is the bundle float?" that looked at the wrong file
        // said yes.
        Assert.True(ZipformerKwsSpotter.PickGraph(AsShipped, "decoder", false).AsAsked);
        Assert.True(ZipformerKwsSpotter.PickGraph(AsShipped, "joiner", false).AsAsked);
        Assert.False(ZipformerKwsSpotter.PickGraph(AsShipped, "encoder", false).AsAsked);
    }

    // ── the rule itself ──────────────────────────────────────────────────────

    [Fact]
    public void When_both_precisions_ship_float_is_what_float_asks_for()
    {
        var pick = ZipformerKwsSpotter.PickGraph(BothPrecisions, "encoder", preferQuantized: false);

        Assert.Equal("/models/kws/encoder.onnx", pick.Path);
        Assert.False(pick.IsQuantized);
        Assert.True(pick.AsAsked);
    }

    [Fact]
    public void When_both_precisions_ship_int8_is_available_to_whoever_wants_it()
    {
        var pick = ZipformerKwsSpotter.PickGraph(BothPrecisions, "encoder", preferQuantized: true);

        Assert.Equal("/models/kws/encoder.int8.onnx", pick.Path);
        Assert.True(pick.IsQuantized);
        Assert.True(pick.AsAsked);
    }

    [Fact]
    public void The_int8_name_sorting_first_does_not_decide_anything()
    {
        // The original defect this guard was written for: "encoder.int8.onnx"
        // sorts before "encoder.onnx", so take-the-first quietly ran quantized.
        // Order must not matter.
        string[] int8First = ["/m/encoder.int8.onnx", "/m/encoder.onnx"];
        string[] floatFirst = ["/m/encoder.onnx", "/m/encoder.int8.onnx"];

        Assert.Equal("/m/encoder.onnx", ZipformerKwsSpotter.PickGraph(int8First, "encoder", false).Path);
        Assert.Equal("/m/encoder.onnx", ZipformerKwsSpotter.PickGraph(floatFirst, "encoder", false).Path);
    }

    [Fact]
    public void A_bundle_with_only_float_when_int8_was_asked_for_is_also_flagged()
    {
        // The symmetric case. Benign in effect - float is the safer graph - but
        // the caller still asked for something it did not get, and a rule that
        // only reports the failure it was written for is half a rule.
        string[] floatOnly = ["/m/encoder.onnx"];
        var pick = ZipformerKwsSpotter.PickGraph(floatOnly, "encoder", preferQuantized: true);

        Assert.False(pick.IsQuantized);
        Assert.False(pick.AsAsked);
    }

    [Fact]
    public void A_missing_graph_is_an_exception_and_not_a_quiet_substitution()
    {
        // The one case the old rule got right, kept: no encoder at all must
        // throw rather than hand back a decoder that happens to be in the list.
        string[] noEncoder = ["/m/decoder.onnx", "/m/joiner.onnx"];

        Assert.Throws<FileNotFoundException>(
            () => ZipformerKwsSpotter.PickGraph(noEncoder, "encoder", false));
    }

    [Fact]
    public void Kind_matching_does_not_confuse_decoder_with_encoder()
    {
        var files = new List<string> { "/m/encoder.onnx", "/m/decoder.onnx", "/m/joiner.onnx" };

        Assert.Equal("/m/encoder.onnx", ZipformerKwsSpotter.PickGraph(files, "encoder", false).Path);
        Assert.Equal("/m/decoder.onnx", ZipformerKwsSpotter.PickGraph(files, "decoder", false).Path);
        Assert.Equal("/m/joiner.onnx", ZipformerKwsSpotter.PickGraph(files, "joiner", false).Path);
    }

    [Fact]
    public void The_kind_is_carried_through_so_a_warning_can_name_the_graph()
    {
        // "loaded a different file" means nothing at two in the morning.
        // "wanted the float encoder" means something.
        Assert.Equal("encoder", ZipformerKwsSpotter.PickGraph(AsShipped, "encoder", false).Kind);
    }
}
