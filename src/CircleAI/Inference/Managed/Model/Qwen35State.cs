// Qwen35State.cs
//
// What one conversation has built up in a qwen35 model: the full-attention layers' keys and
// values - fp16, as the fork's default cache holds them, or TurboQuant codes (TurboKvCache) in a
// compressed mode - and each DeltaNet layer's last conv-kernel-minus-one inputs and its fp32 state
// (one 128x128 matrix per value head, stored transposed as the fork stores it: row j of a head is
// column j of its state).

using CircleAI.Core;
using CircleAI.Core.Compression;

namespace CircleAI.Inference.Managed.Model;

/// <summary>A conversation's state in a <see cref="Qwen35Model"/>.</summary>
public sealed class Qwen35State
{
    internal Qwen35State(Qwen35Config config, int capacity,
        KvCompressionMode kvMode = KvCompressionMode.Off, TurboQuantForm keyForm = TurboQuantForm.Mse)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        KvMode = kvMode;
        KeyForm = keyForm;
        var kvWidth = config.KvHeads * config.HeadDim;
        var convWidth = (config.ConvKernel - 1) * config.ConvChannels;
        var ssmWidth = config.SsmHeads * config.SsmState * config.SsmState;

        Keys = new Half[config.Layers][];
        Values = new Half[config.Layers][];
        Turbo = new TurboKvCache?[config.Layers];
        Conv = new float[config.Layers][];
        Ssm = new float[config.Layers][];
        for (var layer = 0; layer < config.Layers; layer++)
        {
            if (config.IsRecurrent(layer))
            {
                Conv[layer] = new float[convWidth];
                Ssm[layer] = new float[ssmWidth];
            }
            else if (kvMode == KvCompressionMode.Off)
            {
                Keys[layer] = new Half[capacity * kvWidth];
                Values[layer] = new Half[capacity * kvWidth];
            }
            else
            {
                Turbo[layer] = new TurboKvCache(capacity, config.KvHeads, config.HeadDim, kvMode, keyForm);
            }
        }
    }

    /// <summary>Tokens this state can hold.</summary>
    public int Capacity { get; }

    /// <summary>Tokens consumed so far: the position the next token takes.</summary>
    public int Position { get; internal set; }

    /// <summary>How the attention layers hold their keys and values.</summary>
    public KvCompressionMode KvMode { get; }

    /// <summary>The keys' TurboQuant form in a compressed mode.</summary>
    public TurboQuantForm KeyForm { get; }

    internal Half[][] Keys { get; }

    internal Half[][] Values { get; }

    // An attention layer's TurboQuant cache in a compressed mode; null in Off and for DeltaNet layers.
    internal TurboKvCache?[] Turbo { get; }

    internal float[][] Conv { get; }

    internal float[][] Ssm { get; }

    // The one point this state can go back to: its position then, and a copy of the recurrent
    // layers, which a recurrent model cannot step back through. The attention layers need no
    // copy - what they hold before that position is not written again until a reset, which
    // forgets the mark.
    private int _markedPosition = -1;
    private float[][]? _markedConv;
    private float[][]? _markedSsm;

    // And a second point: where the system turn ends - the primed start every conversation with
    // the same system turn can begin from (S3).
    private int _primedPosition = -1;
    private float[][]? _primedConv;
    private float[][]? _primedSsm;

    /// <summary>The position <see cref="Rewind"/> returns to; -1 when nothing is marked.</summary>
    internal int MarkedPosition => _markedPosition;

    /// <summary>The position <see cref="RewindToPrimed"/> returns to; -1 when nothing is primed.</summary>
    internal int PrimedPosition => _primedPosition;

    /// <summary>Forgets everything: position 0, the recurrent layers cleared, nothing marked or primed.</summary>
    internal void Reset()
    {
        Position = 0;
        _markedPosition = -1;
        _primedPosition = -1;
        foreach (var conv in Conv) Array.Clear(conv ?? []);
        foreach (var ssm in Ssm) Array.Clear(ssm ?? []);
    }

    /// <summary>Remembers this point as the primed start, so <see cref="RewindToPrimed"/> can come back to it.</summary>
    internal void MarkPrimed()
    {
        _primedConv ??= Allocate(Conv);
        _primedSsm ??= Allocate(Ssm);
        CopyInto(Conv, _primedConv);
        CopyInto(Ssm, _primedSsm);
        _primedPosition = Position;
    }

    /// <summary>
    /// Goes back to the primed start. A turn mark beyond it is forgotten: the tokens after the
    /// primed start are about to be written again, and the mark would point at what they replace.
    /// </summary>
    internal void RewindToPrimed()
    {
        if (_primedPosition < 0) throw new InvalidOperationException("Nothing is primed.");
        CopyInto(_primedConv!, Conv);
        CopyInto(_primedSsm!, Ssm);
        Position = _primedPosition;
        if (_markedPosition > _primedPosition) _markedPosition = -1;
    }

    /// <summary>Remembers this point, so <see cref="Rewind"/> can come back to it.</summary>
    internal void Mark()
    {
        _markedConv ??= Allocate(Conv);
        _markedSsm ??= Allocate(Ssm);
        CopyInto(Conv, _markedConv);
        CopyInto(Ssm, _markedSsm);
        _markedPosition = Position;
    }

    /// <summary>Goes back to the point <see cref="Mark"/> remembered.</summary>
    internal void Rewind()
    {
        if (_markedPosition < 0) throw new InvalidOperationException("Nothing is marked.");
        CopyInto(_markedConv!, Conv);
        CopyInto(_markedSsm!, Ssm);
        Position = _markedPosition;
        if (_primedPosition > _markedPosition) _primedPosition = -1;
    }

    private static float[][] Allocate(float[][] like)
    {
        var copy = new float[like.Length][];
        for (var i = 0; i < like.Length; i++)
        {
            if (like[i] is { } layer) copy[i] = new float[layer.Length];
        }
        return copy;
    }

    private static void CopyInto(float[][] from, float[][] to)
    {
        for (var i = 0; i < from.Length; i++) from[i].AsSpan().CopyTo(to[i]);
    }

    private const ulong Magic = 0x3154_5335_4E45_5751;      // "QWEN5ST1", little-endian: fp16 keys and values
    private const ulong MagicModes = 0x3254_5335_4E45_5751; // "QWEN5ST2": the working memory's mode after the shape

    /// <summary>
    /// Writes this state: its shape (so it is never read into another model), how it holds its
    /// keys and values, its position, and every layer's cache or recurrent state - the keys and
    /// values only as far as they are filled. With <paramref name="atMark"/>, the state as it
    /// stood at the mark instead.
    /// </summary>
    internal void Write(BinaryWriter writer, Qwen35Config config, bool atMark = false)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Sessions are written little-endian.");
        if (atMark && _markedPosition < 0) throw new InvalidOperationException("Nothing is marked.");
        var position = atMark ? _markedPosition : Position;
        var conv = atMark ? _markedConv! : Conv;
        var ssm = atMark ? _markedSsm! : Ssm;

        writer.Write(MagicModes);
        foreach (var n in Shape(config, Capacity)) writer.Write(n);
        writer.Write((int)KvMode);
        writer.Write((int)KeyForm);
        writer.Write(position);
        var kvWidth = config.KvHeads * config.HeadDim;
        for (var layer = 0; layer < config.Layers; layer++)
        {
            if (config.IsRecurrent(layer))
            {
                writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(conv[layer].AsSpan()));
                writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ssm[layer].AsSpan()));
            }
            else if (Turbo[layer] is { } turbo)
            {
                turbo.WriteTo(writer, position);
            }
            else
            {
                writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Keys[layer].AsSpan(0, position * kvWidth)));
                writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Values[layer].AsSpan(0, position * kvWidth)));
            }
        }
    }

    /// <summary>
    /// A state written by <see cref="Write"/> for a model of this shape, in the mode it was written
    /// in (a session from before the modes holds fp16); anything else is refused.
    /// </summary>
    internal static Qwen35State Read(BinaryReader reader, Qwen35Config config)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Sessions are read little-endian.");
        var magic = reader.ReadUInt64();
        if (magic != Magic && magic != MagicModes) throw new InvalidDataException("This is not a qwen35 session.");
        var shape = Shape(config, 0);
        var stored = new int[shape.Length];
        for (var i = 0; i < stored.Length; i++) stored[i] = reader.ReadInt32();
        if (!stored.AsSpan(0, shape.Length - 1).SequenceEqual(shape.AsSpan(0, shape.Length - 1)))
            throw new InvalidDataException("The session was written by a model of another shape.");
        var capacity = stored[^1];
        var mode = KvCompressionMode.Off;
        var form = TurboQuantForm.Mse;
        if (magic == MagicModes)
        {
            mode = (KvCompressionMode)reader.ReadInt32();
            form = (TurboQuantForm)reader.ReadInt32();
            if (!Enum.IsDefined(mode) || !Enum.IsDefined(form)) throw new InvalidDataException("The session names a working-memory mode this engine does not know.");
        }
        var state = new Qwen35State(config, capacity, mode, form);
        var position = reader.ReadInt32();
        if (position < 0 || position > capacity) throw new InvalidDataException($"The session's position {position} is outside its {capacity} tokens.");
        state.Position = position;

        var kvWidth = config.KvHeads * config.HeadDim;
        for (var layer = 0; layer < config.Layers; layer++)
        {
            if (config.IsRecurrent(layer))
            {
                Fill(reader, System.Runtime.InteropServices.MemoryMarshal.AsBytes(state.Conv[layer].AsSpan()));
                Fill(reader, System.Runtime.InteropServices.MemoryMarshal.AsBytes(state.Ssm[layer].AsSpan()));
            }
            else if (state.Turbo[layer] is { } turbo)
            {
                turbo.ReadFrom(reader, position);
            }
            else
            {
                Fill(reader, System.Runtime.InteropServices.MemoryMarshal.AsBytes(state.Keys[layer].AsSpan(0, position * kvWidth)));
                Fill(reader, System.Runtime.InteropServices.MemoryMarshal.AsBytes(state.Values[layer].AsSpan(0, position * kvWidth)));
            }
        }
        return state;
    }

    // What a session must agree on with the model reading it; the last entry is the capacity.
    private static int[] Shape(Qwen35Config c, int capacity) =>
        [c.Layers, c.Embedding, c.KvHeads, c.HeadDim, c.ConvKernel, c.ConvChannels, c.SsmHeads, c.SsmState, c.FullAttentionInterval, capacity];

    private static void Fill(BinaryReader reader, Span<byte> destination)
    {
        while (destination.Length > 0)
        {
            var n = reader.Read(destination);
            if (n == 0) throw new InvalidDataException("The session ends early.");
            destination = destination[n..];
        }
    }
}
