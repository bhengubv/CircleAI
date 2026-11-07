// HadamardChunkTests.cs
//
// S0's rotation chunks. For each activation width the pinned fork rotates in the 2B, one block of a
// real activation before the signs and after the rotation (fixtures/bonsai2/2B/rotation.json, from
// bonsai-ref rotation); the C# rotation (PrismHadamard's input transform for that width) turns the
// same block into the same values. A width whose transform regroups heads first is left to
// PrismHadamardTests: the fork's block there is taken after its regrouping.
//
// Needs the 2B on this machine (says SKIPPED otherwise).

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Rotation;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class HadamardChunkTests(ITestOutputHelper output)
{
    [Fact]
    public void Each_width_rotates_a_block_as_the_fork_does()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var file = GgufFile.Open(path);
        var hadamard = PrismHadamard.FromGguf(file);
        Assert.NotNull(hadamard);
        var transforms = hadamard!.FoldedWeights.Select(hadamard.InputOf).OfType<InputTransform>().ToList();

        using var fixture = Bonsai2Model.Fixture("rotation.json");
        var judged = 0;
        foreach (var chunk in fixture.RootElement.GetProperty("widths").EnumerateArray())
        {
            var width = chunk.GetProperty("width").GetInt32();
            var block = chunk.GetProperty("block").GetInt32();
            var x = chunk.GetProperty("x").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var y = chunk.GetProperty("y").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            Assert.Equal(hadamard.BlockSize, block);

            var transform = transforms.FirstOrDefault(t => t.Width == width && !t.Regroups);
            if (transform is null)
            {
                output.WriteLine($"width {width,5}: regrouped first; PrismHadamardTests holds it");
                continue;
            }

            // The block in place, every other block zero: blocks rotate on their own.
            var input = new float[width];
            x.CopyTo(input, 0);
            var rotated = new float[width];
            transform.Apply(input, rotated);

            var scale = Math.Max(1f, y.Max(MathF.Abs));
            var worst = 0f;
            for (var i = 0; i < block; i++) worst = MathF.Max(worst, MathF.Abs(rotated[i] - y[i]));
            output.WriteLine($"width {width,5}: block {block}, off by at most {worst:G4} against values up to {scale:G4}");
            Assert.True(worst <= 1e-4f * scale, $"width {width}: the block is off by {worst}");
            judged++;
        }
        Assert.True(judged > 0, "no width was judged");
    }
}
