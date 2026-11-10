// LayerSkip.cs
//
// WHICH LAYERS AN EASY WORD CAN SKIP (the indexed mind, "Index every part of the model, not one part": "Layers. Index
// which layers an easy word can skip."). A layer that barely moves the thought - its feed-forward and mixing add almost
// nothing - can be stepped over, and the thought passes through unchanged. This watches how far each layer moves the
// thought over a run of words and marks the ones that move it less than a threshold; the model then skips them
// (LanguageModel.Skip). Skipping is lossless only where the contribution is near zero, which is what the threshold holds to.

using CircleAI.Inference.Managed.Model;

namespace CircleAI.Inference.Managed.Store;

/// <summary>The layers a word can skip, built from how little each one moves the thought.</summary>
public static class LayerSkip
{
    /// <summary>
    /// A flag per layer, true where the layer's output barely differs from the layer's below it over <paramref name="words"/>
    /// - it adds almost nothing, so an easy word can skip it. Layer 0 is never marked (it has no layer below to compare).
    /// The run leaves <paramref name="model"/>'s callbacks and state as it found them.
    /// </summary>
    public static bool[] Build(LanguageModel model, IReadOnlyList<int> words, float threshold = 1e-4f)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(words);
        var layers = model.Config.Layers;
        var embedding = model.Config.Embedding;
        var moved = new double[layers];
        var previous = new float[embedding];
        var saved = model.LayerOutput;
        model.LayerOutput = (il, x) =>
        {
            if (il > 0)
            {
                double sum = 0;
                for (var e = 0; e < embedding; e++)
                {
                    double d = x[e] - previous[e];
                    sum += d * d;
                }
                moved[il] = Math.Max(moved[il], Math.Sqrt(sum));
            }
            x.AsSpan(0, embedding).CopyTo(previous);
        };
        try
        {
            var state = model.NewState(Math.Max(1, words.Count));
            foreach (var token in words) model.Forward(token, state);
        }
        finally
        {
            model.LayerOutput = saved;
        }
        var skip = new bool[layers];
        for (var il = 1; il < layers; il++) skip[il] = moved[il] < threshold;
        return skip;
    }
}
