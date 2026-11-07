// Rotation.cs
//
// S0's rotation chunks: for each activation width the fork rotates (prism.hadamard.*), one block of
// a real activation before and after the rotation, from logits.json's "fact" prompt at its last
// token. The graph callback watches two nodes per width: the product by "prism.hadamard.signs.<width>"
// - its output, with the signs taken back off, is the activation before the signs - and the product
// by "prism.hadamard.<block>" that follows it - the rotated activation. Only a width's first rotation
// is kept. HadamardChunkTests holds the C# rotation (PrismHadamard) to these, width by width.
//
// The callback reads ggml_tensor fields at the offsets the fork's ggml.h lays them out at: the type
// at 0, ne from 16, nb from 48, src[0] at 152, src[1] at 160, view_src at 232.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace CircleAI.Tools.BonsaiRef;

internal static unsafe class Rotation
{
    private const string Ggml = "ggml-base";
    private const string Signs = "prism.hadamard.signs.";
    private const string Rot = "prism.hadamard.";

    [DllImport(Ggml)] private static extern byte* ggml_get_name(nint tensor);
    [DllImport(Ggml)] private static extern void ggml_backend_tensor_get(nint tensor, void* data, nuint offset, nuint size);

    // Per width: the last token's activation before the signs, until its rotation is seen.
    private static readonly Dictionary<int, float[]> Before = [];
    private static readonly SortedDictionary<int, (int Block, float[] X, float[] Y)> Chunks = [];

    public static JsonObject Run(nint model, int[] promptIds, JsonObject source)
    {
        var p = Llama.llama_context_default_params();
        p.NCtx = 4096;
        p.NBatch = 512;
        p.NUbatch = 512;
        p.NThreads = 1;
        p.NThreadsBatch = 1;
        p.NoPerf = 1;
        p.CbEval = (nint)(delegate* unmanaged[Cdecl]<nint, byte, nint, byte>)&Eval;
        var ctx = Llama.llama_init_from_model(model, p);
        if (ctx == 0) throw new InvalidOperationException("llama_init_from_model returned NULL");
        try
        {
            fixed (int* ids = promptIds)
            {
                var rc = Llama.llama_decode(ctx, Llama.llama_batch_get_one(ids, promptIds.Length));
                if (rc != 0) throw new InvalidOperationException($"llama_decode(prompt) returned {rc}");
            }
        }
        finally
        {
            Llama.llama_free(ctx);
        }
        if (Chunks.Count == 0) throw new InvalidOperationException("the graph rotated nothing by a prism.hadamard.* tensor");

        var widths = new JsonArray();
        foreach (var (width, (block, x, y)) in Chunks)
        {
            widths.Add(new JsonObject
            {
                ["width"] = width,
                ["block"] = block,
                ["x"] = new JsonArray([.. x.Select(v => (JsonNode)v)]),
                ["y"] = new JsonArray([.. y.Select(v => (JsonNode)v)]),
            });
            Console.WriteLine($"  width {width,5}: block {block}");
        }
        return new JsonObject
        {
            ["_comment"] = "Per rotated width, the first block of the last prompt token's activation before the signs (x) and after the rotation (y), 1 thread.",
            ["_source"] = source,
            ["case"] = "fact",
            ["prompt_ids"] = new JsonArray([.. promptIds.Select(id => (JsonNode)id)]),
            ["widths"] = widths,
        };
    }

    private static string NameOf(nint tensor) => tensor == 0 ? "" : Marshal.PtrToStringUTF8((nint)ggml_get_name(tensor)) ?? "";

    private static nint Src(nint tensor, int i) => *(nint*)(tensor + 152 + i * 8);

    // The width a signs tensor's name gives, or 0.
    private static int WidthOf(nint signs)
    {
        var name = NameOf(signs);
        return name.StartsWith(Signs, StringComparison.Ordinal) && int.TryParse(name.AsSpan(Signs.Length), out var w) ? w : 0;
    }

    // The block a rotation tensor's name gives, or 0.
    private static int BlockOf(nint rot)
    {
        var name = NameOf(rot);
        return name.StartsWith(Rot, StringComparison.Ordinal) && !name.StartsWith(Signs, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(Rot.Length), out var b) ? b : 0;
    }

    // The row of `tensor` holding the last token, `count` values from the start of it.
    private static float[] LastRow(nint tensor, long rowValues, int count)
    {
        var values = *(long*)(tensor + 16) * *(long*)(tensor + 24) * *(long*)(tensor + 32) * *(long*)(tensor + 40);
        var row = new float[count];
        fixed (float* dst = row)
            ggml_backend_tensor_get(tensor, dst, (nuint)((values - rowValues) * sizeof(float)), (nuint)(count * sizeof(float)));
        return row;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Eval(nint tensor, byte ask, nint user)
    {
        if (*(int*)tensor != 0) return ask != 0 ? (byte)0 : (byte)1; // F32 nodes only
        var width = WidthOf(Src(tensor, 1));
        var block = width == 0 ? BlockOf(Src(tensor, 0)) : 0;
        var wanted = width > 0 || block > 0;
        if (ask != 0) return wanted ? (byte)1 : (byte)0;
        if (!wanted) return 1;

        if (width > 0)
        {
            // activation * signs, read from its own output: by the time this is called the product may
            // have run in the activation's buffer, which then holds the signed values. A sign is its own
            // inverse, so the output times the signs is the activation. The latest one each time: the
            // rotation below follows the product just before it.
            if (!Chunks.ContainsKey(width))
            {
                var signed = LastRow(tensor, width, width);
                var signs = new float[width];
                fixed (float* s = signs) ggml_backend_tensor_get(Src(tensor, 1), s, 0, (nuint)(width * sizeof(float)));
                if (signs.Any(v => MathF.Abs(v) != 1f)) throw new InvalidOperationException($"the signs of width {width} are not all +1 or -1");
                for (var i = 0; i < width; i++) signed[i] *= signs[i];
                Before[width] = signed;
            }
            return 1;
        }

        // rot x reshape(activation * signs): find the signs product the reshape (or copy) came from.
        var input = Src(tensor, 1);
        var from = *(nint*)(input + 232); // view_src
        if (from == 0) from = Src(input, 0);
        var w = WidthOf(Src(from, 1));
        if (w > 0 && Before.Remove(w, out var x) && !Chunks.ContainsKey(w))
            Chunks[w] = (block, x[..block], LastRow(tensor, w, block));
        return 1;
    }
}
