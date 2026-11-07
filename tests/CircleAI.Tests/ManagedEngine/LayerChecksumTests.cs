// LayerChecksumTests.cs
//
// S0's per-layer checksums. The 2B's "fact" prompt runs through the C# engine, and each layer's
// output at the prompt's last token - the residual after its feed-forward, the fork's l_out - is
// held to the pinned fork's (fixtures/bonsai2/2B/layers.json, from bonsai-ref layers): its norm
// within 2%, with the first 16 values and the fork's own spread between its thread counts written
// out beside it. The logits test (Qwen35ModelTests) says whether the engine ends where the fork
// does; this one says, when it does not, which layer it parted at.
//
// Needs the 2B on this machine (says SKIPPED otherwise). Run it in Release.

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class LayerChecksumTests(ITestOutputHelper output)
{
    [Fact]
    public void Each_layer_of_the_2B_ends_where_the_forks_does()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var fixture = Bonsai2Model.Fixture("layers.json");
        int[] prompt = [.. fixture.RootElement.GetProperty("prompt_ids").EnumerateArray().Select(e => e.GetInt32())];
        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var model = Qwen35Model.Load(file, pool);
        var state = model.NewState(prompt.Length + 1);

        // Every layer's output at the last prompt token.
        var last = new Dictionary<int, float[]>();
        for (var i = 0; i < prompt.Length; i++)
        {
            if (i == prompt.Length - 1) model.LayerOutput = (il, x) => last[il] = (float[])x.Clone();
            model.Forward(prompt[i], state);
        }
        model.LayerOutput = null;

        var layers = fixture.RootElement.GetProperty("layers").EnumerateArray().ToList();
        Assert.Equal(model.Config.Layers, layers.Count);
        foreach (var layer in layers)
        {
            var il = layer.GetProperty("layer").GetInt32();
            var row = last[il];
            var norm = Math.Sqrt(row.Sum(v => (double)v * v));
            var forkNorm = layer.GetProperty("norm").GetDouble();
            var off = Math.Abs(norm - forkNorm) / forkNorm;
            var head = layer.GetProperty("head").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var worst = head.Select((v, j) => Math.Abs(v - row[j])).Max();
            output.WriteLine($"layer {il,2}: norm {norm,10:F4} against the fork's {forkNorm,10:F4} ({off:P3}); its first {head.Length} values off by at most {worst:F5}, the fork's own spread {layer.GetProperty("spread").GetDouble():F5}");
            Assert.True(off <= 0.02, $"layer {il}: norm {norm} against the fork's {forkNorm}");
        }
    }
}
