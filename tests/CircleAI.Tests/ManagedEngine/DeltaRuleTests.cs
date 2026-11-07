// DeltaRuleTests.cs
//
// The plan's unit test of the delta rule against a scalar reference. Qwen35Model.DeltaRule - one
// value head of a Gated DeltaNet layer, its gates worked out, as the fork's fused op runs it - is
// held to the rule written out plainly from its definition, in double precision:
//     S <- decay * S;   delta = beta * (v - S^T k);   S <- S + k delta^T;   o = S^T q / sqrt(d)
// step after step, so the state each step leaves is where the next begins, as a prompt's tokens run
// through a layer; q and k are L2-normalised as the layer normalises them.

using CircleAI.Inference.Managed.Model;
using Xunit;

namespace CircleAI.Tests;

public sealed class DeltaRuleTests
{
    private static double[] Gaussian(Random random, int n)
    {
        var v = new double[n];
        for (var i = 0; i < n; i++)
            v[i] = Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());
        return v;
    }

    private static float[] Unit(double[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => (float)(x / norm))];
    }

    [Theory]
    [InlineData(128, 1)] // the 2B's state width
    [InlineData(128, 2)]
    [InlineData(64, 3)]
    [InlineData(24, 4)] // not whole vectors: the plain path
    public unsafe void One_head_follows_the_rule_written_out_step_after_step(int width, int seed)
    {
        const int steps = 24;
        var random = new Random(seed);
        var state = new float[width * width];     // row j is column j of the state, as the layer keeps it
        var reference = new double[width, width]; // reference[i, j]: the state's row i, column j
        var delta = new float[width];
        var output = new float[width];
        var scale = 1.0f / MathF.Sqrt(width);

        for (var t = 0; t < steps; t++)
        {
            var q = Unit(Gaussian(random, width));
            var k = Unit(Gaussian(random, width));
            float[] v = [.. Gaussian(random, width).Select(x => (float)x)];
            var beta = (float)random.NextDouble();                    // a sigmoid's range
            var decay = (float)Math.Exp(-3.0 * random.NextDouble());  // exp(g), g at most 0

            fixed (float* m = state)
            fixed (float* pq = q)
            fixed (float* pk = k)
            fixed (float* pv = v)
            fixed (float* pd = delta)
            fixed (float* po = output)
                Qwen35Model.DeltaRule(m, pq, pk, pv, beta, decay, scale, width, pd, po);

            // The rule, written out, from the same single-precision inputs the layer reads.
            for (var i = 0; i < width; i++)
                for (var j = 0; j < width; j++) reference[i, j] *= decay;
            var error = new double[width];
            for (var j = 0; j < width; j++)
            {
                double recalled = 0;
                for (var i = 0; i < width; i++) recalled += reference[i, j] * k[i];
                error[j] = beta * (v[j] - recalled);
            }
            for (var i = 0; i < width; i++)
                for (var j = 0; j < width; j++) reference[i, j] += k[i] * error[j];
            for (var j = 0; j < width; j++)
            {
                double read = 0;
                for (var i = 0; i < width; i++) read += reference[i, j] * q[i];
                read *= scale;
                Assert.True(Math.Abs(output[j] - read) <= 1e-4 * (1 + Math.Abs(read)), $"step {t}, o[{j}]: {output[j]} against {read}");
            }
        }

        // And the state it leaves is the rule's, cell for cell.
        for (var i = 0; i < width; i++)
            for (var j = 0; j < width; j++)
                Assert.True(Math.Abs(state[j * width + i] - reference[i, j]) <= 1e-4 * (1 + Math.Abs(reference[i, j])),
                    $"state[{i}, {j}]: {state[j * width + i]} against {reference[i, j]}");
    }
}
