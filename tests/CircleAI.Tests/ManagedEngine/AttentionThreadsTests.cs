// AttentionThreadsTests.cs
//
// A compressed cache's query heads go out to the pool's threads. Each head writes only its own output
// and reads the cache through a reader of its own, so what comes out cannot depend on how many threads
// shared it, down to the last bit.

using CircleAI.Core;
using CircleAI.Core.Compression;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class AttentionThreadsTests
{
    private static readonly int[] Prompt = [1, 5, 9, 3, 30, 12, 7, 2, 11, 4, 8, 6, 10];

    private static float[] Logits(byte[] model, int threads, KvCompressionMode mode, TurboQuantForm form) => GgufBuilder.WithFile(model, path =>
    {
        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(threads);
        var m = Qwen35Model.Load(file, pool);
        var state = m.NewState(32, mode, form);
        var logits = new float[m.Config.Vocabulary];
        for (var i = 0; i < Prompt.Length; i++) m.Forward(Prompt[i], state, i == Prompt.Length - 1 ? logits : default);
        return logits;
    });

    [Theory]
    [InlineData(KvCompressionMode.TurboQuant4Bit, TurboQuantForm.Mse)]
    [InlineData(KvCompressionMode.TurboQuant3Bit, TurboQuantForm.Mse)]
    [InlineData(KvCompressionMode.TurboQuant4Bit, TurboQuantForm.Prod)]
    public void Threads_change_no_bit_of_the_logits(KvCompressionMode mode, TurboQuantForm form)
    {
        var model = SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 4);

        var alone = Logits(model, 1, mode, form);
        var shared = Logits(model, 3, mode, form);

        Assert.Equal(alone, shared);
    }
}
