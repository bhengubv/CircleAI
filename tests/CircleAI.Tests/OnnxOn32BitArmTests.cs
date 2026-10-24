// OnnxOn32BitArmTests.cs
//
// A 32-BIT ARM PROCESS CANNOT RUN ONNX RUNTIME'S CommonSubexpressionElimination PASS.
// On the P30's fresh install of the 32-bit pair on 2026-10-23 the service died twice
// in a row while the greeting warm-up opened its Piper voice: SIGBUS BUS_ADRALN in
// libonnxruntime.so, at `vldr s2, [r4]` with r4 one past a string object. The pass reads a float out of a
// protobuf string at an odd address. AArch32's VLDR faults on that; AArch64's LDR
// does not, which is why the 64-bit pair never saw it.
//
// The fix is one session-config entry, set on every session that
// OnnxSessionFactory.Create builds in a 32-bit ARM process. A test host is not 32-bit
// ARM, so the fault itself cannot happen here. What these tests prove:
//   - which processors get the entry,
//   - that it is set once per options object,
//   - that the runtime we ship really switches the pass off when it is set,
//   - that nothing in the product builds a session without going through the factory.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CircleAI.Voice;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace CircleAI.Tests;

public sealed class OnnxOn32BitArmTests
{
    [Theory]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Armv6)]
    public void A_32_bit_arm_process_skips_the_pass_that_faults(Architecture processor) =>
        Assert.Equal("CommonSubexpressionElimination", OnnxSessionFactory.PassesToSkip(processor));

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.X86)]
    public void Every_other_processor_keeps_every_pass(Architecture processor) =>
        Assert.Null(OnnxSessionFactory.PassesToSkip(processor));

    [Fact]
    public void Options_shared_by_three_graphs_are_fitted_once()
    {
        // The wake word's encoder, decoder and joiner share one options object, and
        // ONNX Runtime warns every time a config key is set a second time.
        using var shared = new SessionOptions();
        Assert.True(OnnxSessionFactory.FitToProcessor(shared, Architecture.Arm));
        Assert.False(OnnxSessionFactory.FitToProcessor(shared, Architecture.Arm));
        Assert.False(OnnxSessionFactory.FitToProcessor(shared, Architecture.Arm));

        using var other = new SessionOptions();
        Assert.False(OnnxSessionFactory.FitToProcessor(other, Architecture.Arm64));
        Assert.True(OnnxSessionFactory.FitToProcessor(other, Architecture.Arm));
    }

    // ── the runtime we ship honours the entry ────────────────────────────────

    [Fact]
    public void The_entry_really_switches_that_pass_off_in_the_runtime_we_ship()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-cse-");
        try
        {
            var model = Path.Combine(dir.FullName, "two-identical-relus.onnx");
            File.WriteAllBytes(model, TwoIdenticalRelus());

            // With the pass on, the two identical Relus become one. If this fails, the
            // graph no longer gives the pass anything to do and the next line proves nothing.
            Assert.Equal(1, ReluNodesAfterOptimising(model, Path.Combine(dir.FullName, "on.onnx"), Architecture.X64));

            // Fitted for 32-bit ARM, the pass did not run, so both Relus are still there.
            Assert.Equal(2, ReluNodesAfterOptimising(model, Path.Combine(dir.FullName, "off.onnx"), Architecture.Arm));
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int ReluNodesAfterOptimising(string model, string saved, Architecture processor)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
            OptimizedModelFilePath = saved,
        };
        OnnxSessionFactory.FitToProcessor(options, processor);
        using (OnnxSessionFactory.Create(model, options)) { }

        // NodeProto.op_type is field 4: tag 0x22, then the length, then the name.
        byte[] relu = [0x22, 0x04, (byte)'R', (byte)'e', (byte)'l', (byte)'u'];
        var bytes = File.ReadAllBytes(saved);
        var count = 0;
        for (var i = 0; i <= bytes.Length - relu.Length; i++)
            if (bytes.AsSpan(i, relu.Length).SequenceEqual(relu)) count++;
        return count;
    }

    /// <summary>
    /// The smallest graph the pass has work in: two identical Relus of X feeding one
    /// Add. It is written as raw protobuf because nothing in the repo writes ONNX, and
    /// a few bytes here beat checking in a model file for one test.
    /// </summary>
    private static byte[] TwoIdenticalRelus()
    {
        // TypeProto { tensor_type { elem_type: FLOAT, shape { dim { dim_value: 1 } } } }
        var floatOfOne = Message(1, Int(1, 1), Message(2, Message(1, Int(1, 1))));
        byte[] Value(string name) => Join(Text(1, name), Message(2, floatOfOne));
        byte[] Node(string op, string output, params string[] inputs) =>
            Join([.. inputs.Select(i => Text(1, i)), Text(2, output), Text(4, op)]);

        var graph = Join(
            Message(1, Node("Relu", "A", "X")),
            Message(1, Node("Relu", "B", "X")),
            Message(1, Node("Add", "Y", "A", "B")),
            Text(2, "two-identical-relus"),
            Message(11, Value("X")),
            Message(12, Value("Y")));

        // ModelProto { ir_version: 8, opset_import { version: 13 }, graph }
        return Join(Int(1, 8), Message(8, Int(2, 13)), Message(7, graph));
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var low = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? low : (byte)(low | 0x80));
        }
        while (value != 0);
        return [.. bytes];
    }

    private static byte[] Key(int field, int wire) => Varint((ulong)((field << 3) | wire));

    private static byte[] Int(int field, long value) => Join(Key(field, 0), Varint((ulong)value));

    private static byte[] Message(int field, params byte[][] body)
    {
        var bytes = Join(body);
        return Join(Key(field, 2), Varint((ulong)bytes.Length), bytes);
    }

    private static byte[] Text(int field, string text) => Message(field, Encoding.UTF8.GetBytes(text));

    // ── nothing builds a session without the factory ─────────────────────────

    [Fact]
    public void Every_onnx_session_in_the_product_is_built_by_the_factory()
    {
        var root = Repo();
        var built = new Regex(@"new\s+InferenceSession\s*\(|InferenceSession\??\s+\w+(\(\))?\s*(=|=>)\s*new\s*\(");

        var direct = new List<string>();
        foreach (var top in new[] { "src", "samples" })
        foreach (var file in SourceFiles(Path.Combine(root, top)))
        {
            var hits = built.Matches(File.ReadAllText(file)).Count;
            for (var i = 0; i < hits; i++) direct.Add(Path.GetRelativePath(root, file));
        }

        var factory = Path.Combine("src", "CircleAI", "Voice", "OnnxSessionFactory.cs");
        Assert.True(direct.Count == 1 && direct[0] == factory,
            "sessions must be built by OnnxSessionFactory.Create, or a 32-bit ARM phone dies building them. Built directly: "
            + string.Join(", ", direct));
    }

    private static IEnumerable<string> SourceFiles(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs")) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.')) continue;
            foreach (var file in SourceFiles(sub)) yield return file;
        }
    }

    private static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CircleAI.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
