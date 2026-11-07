// Layers.cs
//
// The per-layer checksums of S0. One prompt - logits.json's "fact" case, its own token ids - runs
// through the fork with the graph's eval callback on, and each layer's output ("l_out-<il>", the
// residual after the feed-forward) is read at the prompt's last token: its norm, its sum and its
// first 16 values, at 1 thread, and how far 2 and 4 threads move that row (the largest difference
// over all of it). LayerChecksumTests holds the C# engine to these layer by layer, so a difference
// shows at the layer it starts in rather than only, many layers later, in the logits.
//
// The callback reads a ggml_tensor by the fields every ggml since 2023 lays out the same way: the
// type at 0, ne[4] from 16, nb[4] from 48. The name comes through ggml_get_name and the data
// through ggml_backend_tensor_get, whichever backend holds it.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircleAI.Tools.BonsaiRef;

internal static unsafe class Layers
{
    private const string Ggml = "ggml-base";
    private const string Name = "l_out-";
    private const int Head = 16;

    [DllImport(Ggml)] private static extern byte* ggml_get_name(nint tensor);
    [DllImport(Ggml)] private static extern void ggml_backend_tensor_get(nint tensor, void* data, nuint offset, nuint size);

    // The rows one run read, by layer. The callback runs on the thread that called llama_decode.
    private static Dictionary<int, float[]>? _rows;

    /// <summary>The prompt ids of case <paramref name="name"/> in a logits.json fixture.</summary>
    public static int[] PromptOf(string logitsPath, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(logitsPath));
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().First(e => e.GetProperty("name").GetString() == name);
        return [.. c.GetProperty("prompt_ids").EnumerateArray().Select(e => e.GetInt32())];
    }

    public static JsonObject Run(nint model, int[] promptIds, JsonObject source)
    {
        var one = Capture(model, 1, promptIds);
        var two = Capture(model, 2, promptIds);
        var four = Capture(model, 4, promptIds);

        var layers = new JsonArray();
        foreach (var (il, row) in one.OrderBy(e => e.Key))
        {
            double squares = 0, sum = 0;
            foreach (var v in row)
            {
                squares += (double)v * v;
                sum += v;
            }
            var spread = Math.Max(MaxAbsDiff(row, two[il]), MaxAbsDiff(row, four[il]));
            layers.Add(new JsonObject
            {
                ["layer"] = il,
                ["norm"] = Math.Sqrt(squares),
                ["sum"] = sum,
                ["head"] = new JsonArray([.. row.Take(Head).Select(v => (JsonNode)v)]),
                ["spread"] = spread,
            });
            Console.WriteLine($"  layer {il,2}: norm {Math.Sqrt(squares),10:F4}  sum {sum,10:F4}  spread at 2/4 threads {spread:F6}");
        }

        return new JsonObject
        {
            ["_comment"] = "Each layer's output (l_out) at the last prompt token: its norm, sum and first 16 values on 1 thread, and the largest change 2 and 4 threads make to that row.",
            ["_source"] = source,
            ["case"] = "fact",
            ["prompt_ids"] = new JsonArray([.. promptIds.Select(id => (JsonNode)id)]),
            ["layers"] = layers,
        };
    }

    private static Dictionary<int, float[]> Capture(nint model, int threads, int[] promptIds)
    {
        _rows = [];
        var p = Llama.llama_context_default_params();
        p.NCtx = 4096;
        p.NBatch = 512;
        p.NUbatch = 512;
        p.NThreads = threads;
        p.NThreadsBatch = threads;
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
        if (_rows.Count == 0) throw new InvalidOperationException($"the graph named no {Name}* tensor");
        return _rows;
    }

    // Asked first whether a tensor is wanted, then called again once it is computed.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Eval(nint tensor, byte ask, nint user)
    {
        var name = Marshal.PtrToStringUTF8((nint)ggml_get_name(tensor)) ?? "";
        var wanted = name.StartsWith(Name, StringComparison.Ordinal) && *(int*)tensor == 0; // GGML_TYPE_F32
        if (ask != 0) return wanted ? (byte)1 : (byte)0;
        if (!wanted) return 1;

        var width = *(long*)(tensor + 16);
        var tokens = *(long*)(tensor + 24);
        var rowBytes = *(ulong*)(tensor + 56);
        var row = new float[width];
        fixed (float* dst = row)
            ggml_backend_tensor_get(tensor, dst, (nuint)((ulong)(tokens - 1) * rowBytes), (nuint)(width * sizeof(float)));
        _rows![int.Parse(name.AsSpan(Name.Length))] = row;
        return 1;
    }

    private static float MaxAbsDiff(float[] a, float[] b)
    {
        var worst = 0f;
        for (var i = 0; i < a.Length; i++) worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
        return worst;
    }
}
