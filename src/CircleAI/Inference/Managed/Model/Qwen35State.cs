// Qwen35State.cs
//
// What one conversation has built up in a qwen35 model: the full-attention layers' keys and
// values (fp16, as the fork's default cache holds them), and each DeltaNet layer's last
// conv-kernel-minus-one inputs and its fp32 state (one 128x128 matrix per value head, stored
// transposed as the fork stores it: row j of a head is column j of its state).

namespace CircleAI.Inference.Managed.Model;

/// <summary>A conversation's state in a <see cref="Qwen35Model"/>.</summary>
public sealed class Qwen35State
{
    internal Qwen35State(Qwen35Config config, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        var kvWidth = config.KvHeads * config.HeadDim;
        var convWidth = (config.ConvKernel - 1) * config.ConvChannels;
        var ssmWidth = config.SsmHeads * config.SsmState * config.SsmState;

        Keys = new Half[config.Layers][];
        Values = new Half[config.Layers][];
        Conv = new float[config.Layers][];
        Ssm = new float[config.Layers][];
        for (var layer = 0; layer < config.Layers; layer++)
        {
            if (config.IsRecurrent(layer))
            {
                Conv[layer] = new float[convWidth];
                Ssm[layer] = new float[ssmWidth];
            }
            else
            {
                Keys[layer] = new Half[capacity * kvWidth];
                Values[layer] = new Half[capacity * kvWidth];
            }
        }
    }

    /// <summary>Tokens this state can hold.</summary>
    public int Capacity { get; }

    /// <summary>Tokens consumed so far: the position the next token takes.</summary>
    public int Position { get; internal set; }

    internal Half[][] Keys { get; }

    internal Half[][] Values { get; }

    internal float[][] Conv { get; }

    internal float[][] Ssm { get; }
}
