// ModelState.cs
//
// What one conversation has built up in a qwen35 model: the full-attention layers' keys and
// values - fp16, as the fork's default cache holds them, or TurboQuant codes (TurboKvCache) in a
// compressed mode - and each DeltaNet layer's last conv-kernel-minus-one inputs and its fp32 state
// (one 128x128 matrix per value head, stored transposed as the fork stores it: row j of a head is
// column j of its state).
//
// TWO POSITIONS. Position counts what the state holds - one cell per token, an image's tokens
// included - and is where the next token's keys and values go. RopePosition is where the next
// text token sits in the rotary embedding: the same as Position for text, but an image of rows x
// columns tokens fills that many cells and moves it by only max(rows, columns), as the fork places
// images (mtmd_image_tokens_get_n_pos). Both are kept, marked, primed and written together.

using System.Runtime.InteropServices;
using CircleAI.Core;
using CircleAI.Core.Compression;

namespace CircleAI.Inference.Managed.Model;

/// <summary>
/// The MTP head's key/value cache: one full-attention layer's keys and values, fp16 as the fork's
/// default cache holds them, one slot per position of the conversation.
/// </summary>
public sealed class MtpState
{
    internal MtpState(ModelConfig config, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        var width = config.KvHeads * config.HeadDim;
        Keys = new Half[capacity * width];
        Values = new Half[capacity * width];
    }

    /// <summary>Positions this cache can hold.</summary>
    public int Capacity { get; }

    internal Half[] Keys { get; }

    internal Half[] Values { get; }
}

/// <summary>A conversation's state in a <see cref="LanguageModel"/>.</summary>
public sealed class ModelState : IDisposable
{
    internal ModelState(ModelConfig config, int capacity,
        KvCompressionMode kvMode = KvCompressionMode.Off, TurboQuantForm keyForm = TurboQuantForm.Mse, string? spillDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        KvMode = kvMode;
        KeyForm = keyForm;
        if (spillDirectory is not null)
        {
            if (kvMode != KvCompressionMode.Off) throw new NotSupportedException("The attention spill holds fp16 keys and values; it is not built for a compressed cache yet.");
            Spill = new KvSpill(config, spillDirectory);
        }
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

    /// <summary>Tokens consumed so far, an image's included: the cell the next token takes.</summary>
    public int Position { get; internal set; }

    /// <summary>
    /// Where the next text token sits in the rotary embedding: <see cref="Position"/> until an image
    /// is read, which moves it by the larger side of its token grid rather than by its token count.
    /// </summary>
    public int RopePosition { get; internal set; }

    /// <summary>How the attention layers hold their keys and values.</summary>
    public KvCompressionMode KvMode { get; }

    /// <summary>The keys' TurboQuant form in a compressed mode.</summary>
    public TurboQuantForm KeyForm { get; }

    internal Half[][] Keys { get; }

    internal Half[][] Values { get; }

    // An attention layer's TurboQuant cache in a compressed mode; null in Off and for DeltaNet layers.
    internal TurboKvCache?[] Turbo { get; }

    // THE ATTENTION SPILL (the indexed mind, "Attention ... a conversation's memory can sit on the SSD and grow far past
    // RAM"): when set, a full-attention layer's keys and values go to flash, not the capped RAM arrays, so the conversation
    // grows past Capacity. Null keeps everything in RAM, the default.
    internal KvSpill? Spill { get; }

    internal float[][] Conv { get; }

    internal float[][] Ssm { get; }

    // The one point this state can go back to: its position then, and a copy of the recurrent
    // layers, which a recurrent model cannot step back through. The attention layers need no
    // copy - what they hold before that position is not written again until a reset, which
    // forgets the mark.
    private int _markedPosition = -1;
    private int _markedRope;
    private float[][]? _markedConv;
    private float[][]? _markedSsm;

    // And a second point: where the system turn ends - the primed start every conversation with
    // the same system turn can begin from (S3).
    private int _primedPosition = -1;
    private int _primedRope;
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
        RopePosition = 0;
        _markedPosition = -1;
        _primedPosition = -1;
        _talkPosition = -1;
        _checkpointPosition = -1;
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
        _primedRope = RopePosition;
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
        RopePosition = _primedRope;
        if (_markedPosition > _primedPosition) _markedPosition = -1;
        if (_talkPosition > _primedPosition) _talkPosition = -1;
    }

    /// <summary>Remembers this point, so <see cref="Rewind"/> can come back to it.</summary>
    internal void Mark()
    {
        _markedConv ??= Allocate(Conv);
        _markedSsm ??= Allocate(Ssm);
        CopyInto(Conv, _markedConv);
        CopyInto(Ssm, _markedSsm);
        _markedPosition = Position;
        _markedRope = RopePosition;
    }

    /// <summary>Goes back to the point <see cref="Mark"/> remembered.</summary>
    internal void Rewind()
    {
        if (_markedPosition < 0) throw new InvalidOperationException("Nothing is marked.");
        CopyInto(_markedConv!, Conv);
        CopyInto(_markedSsm!, Ssm);
        Position = _markedPosition;
        RopePosition = _markedRope;
        if (_primedPosition > _markedPosition) _primedPosition = -1;
        if (_talkPosition > _markedPosition) _talkPosition = -1;
    }

    // And a point of the conversation's own: where its last finished turn ended. What comes after it is
    // a turn still being said or answered, and can be thrown away; what comes before it is what was said.
    private int _talkPosition = -1;
    private int _talkRope;
    private float[][]? _talkConv;
    private float[][]? _talkSsm;

    /// <summary>The position <see cref="RewindToTalk"/> returns to; -1 when no conversation is kept.</summary>
    internal int TalkPosition => _talkPosition;

    /// <summary>Remembers this point as the end of the conversation's last finished turn.</summary>
    internal void MarkTalk()
    {
        _talkConv ??= Allocate(Conv);
        _talkSsm ??= Allocate(Ssm);
        CopyInto(Conv, _talkConv);
        CopyInto(Ssm, _talkSsm);
        _talkPosition = Position;
        _talkRope = RopePosition;
    }

    /// <summary>Goes back to where the conversation's last finished turn ended.</summary>
    internal void RewindToTalk()
    {
        if (_talkPosition < 0) throw new InvalidOperationException("No conversation is kept.");
        CopyInto(_talkConv!, Conv);
        CopyInto(_talkSsm!, Ssm);
        Position = _talkPosition;
        RopePosition = _talkRope;
        if (_markedPosition > _talkPosition) _markedPosition = -1;
    }

    // And a third: where a speculative verification began, so drafts the trunk rejects can be taken
    // back out of the recurrent layers (S5). Its own copy, apart from the turn's mark and the primed
    // start, which a conversation is still using while it speculates.
    private int _checkpointPosition = -1;
    private int _checkpointRope;
    private float[][]? _checkpointConv;
    private float[][]? _checkpointSsm;

    /// <summary>Remembers this point for <see cref="RestoreCheckpoint"/>: the recurrent layers and both positions.</summary>
    internal void Checkpoint()
    {
        _checkpointConv ??= Allocate(Conv);
        _checkpointSsm ??= Allocate(Ssm);
        CopyInto(Conv, _checkpointConv);
        CopyInto(Ssm, _checkpointSsm);
        _checkpointPosition = Position;
        _checkpointRope = RopePosition;
    }

    /// <summary>
    /// Goes back to the checkpoint. The attention layers need no copy: what they hold past it is
    /// written again before it is read, by the tokens that replace the rejected ones.
    /// </summary>
    internal void RestoreCheckpoint()
    {
        if (_checkpointPosition < 0) throw new InvalidOperationException("No checkpoint is held.");
        CopyInto(_checkpointConv!, Conv);
        CopyInto(_checkpointSsm!, Ssm);
        Position = _checkpointPosition;
        RopePosition = _checkpointRope;
        if (_markedPosition > Position) _markedPosition = -1;
        if (_primedPosition > Position) _primedPosition = -1;
    }

    /// <summary>
    /// Lets every copy of the recurrent layers go - the mark's, the primed start's, the conversation's, the checkpoint's -
    /// and forgets the points they kept. A trunk that streams its layers keeps none: each is 151 MB for the 27B.
    /// </summary>
    internal void ForgetCopies()
    {
        ForgetPoints();
        _markedConv = _markedSsm = null;
        _primedConv = _primedSsm = null;
        _talkConv = _talkSsm = null;
        _checkpointConv = _checkpointSsm = null;
    }

    // The points this state can go back to no longer describe it.
    private void ForgetPoints()
    {
        _markedPosition = -1;
        _primedPosition = -1;
        _talkPosition = -1;
        _checkpointPosition = -1;
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
    private const ulong MagicRope = 0x3354_5335_4E45_5751;  // "QWEN5ST3": the rotary position after the position

    /// <summary>
    /// Writes this state: its shape (so it is never read into another model), how it holds its
    /// keys and values, its position, and every layer's cache or recurrent state - the keys and
    /// values only as far as they are filled. With <paramref name="atMark"/>, the state as it
    /// stood at the mark instead.
    /// </summary>
    internal void Write(BinaryWriter writer, ModelConfig config, bool atMark = false)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Sessions are written little-endian.");
        if (atMark && _markedPosition < 0) throw new InvalidOperationException("Nothing is marked.");
        var position = atMark ? _markedPosition : Position;
        var rope = atMark ? _markedRope : RopePosition;
        var conv = atMark ? _markedConv! : Conv;
        var ssm = atMark ? _markedSsm! : Ssm;

        writer.Write(MagicRope);
        foreach (var n in Shape(config, Capacity)) writer.Write(n);
        writer.Write((int)KvMode);
        writer.Write((int)KeyForm);
        writer.Write(position);
        writer.Write(rope);
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
    /// in (a session from before the modes holds fp16; one from before images has its rotary
    /// position at its position); anything else is refused. Read into <paramref name="into"/> when
    /// it holds the same capacity and working memory - no second state in RAM, its marks forgotten;
    /// a read that fails part way leaves it to be reset - else into a new one.
    /// </summary>
    internal static ModelState Read(BinaryReader reader, ModelConfig config, ModelState? into = null)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Sessions are read little-endian.");
        var magic = reader.ReadUInt64();
        if (magic != Magic && magic != MagicModes && magic != MagicRope) throw new InvalidDataException("This is not a qwen35 session.");
        var shape = Shape(config, 0);
        var stored = new int[shape.Length];
        for (var i = 0; i < stored.Length; i++) stored[i] = reader.ReadInt32();
        if (!stored.AsSpan(0, shape.Length - 1).SequenceEqual(shape.AsSpan(0, shape.Length - 1)))
            throw new InvalidDataException("The session was written by a model of another shape.");
        var capacity = stored[^1];
        var mode = KvCompressionMode.Off;
        var form = TurboQuantForm.Mse;
        if (magic != Magic)
        {
            mode = (KvCompressionMode)reader.ReadInt32();
            form = (TurboQuantForm)reader.ReadInt32();
            if (!Enum.IsDefined(mode) || !Enum.IsDefined(form)) throw new InvalidDataException("The session names a working-memory mode this engine does not know.");
        }
        ModelState state;
        if (into is not null && into.Capacity == capacity && into.KvMode == mode && into.KeyForm == form)
        {
            state = into;
            state.ForgetPoints();
        }
        else
        {
            state = new ModelState(config, capacity, mode, form);
        }
        var position = reader.ReadInt32();
        if (position < 0 || position > capacity) throw new InvalidDataException($"The session's position {position} is outside its {capacity} tokens.");
        var rope = magic == MagicRope ? reader.ReadInt32() : position;
        if (rope < 0 || rope > position) throw new InvalidDataException($"The session's rotary position {rope} is outside its {position} tokens.");
        state.Position = position;
        state.RopePosition = rope;

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
    private static int[] Shape(ModelConfig c, int capacity) =>
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

    /// <summary>Lets the attention spill's flash files go; a state with no spill holds nothing to let go.</summary>
    public void Dispose() => Spill?.Dispose();
}

/// <summary>
/// A full-attention layer's keys and values on flash, so a conversation's memory grows past what RAM holds (the indexed
/// mind). Each position's keys then values are one row in the layer's file; a word writes its row and reads the rows up to
/// it back into shared buffers to attend over. Fp16, Off mode only. One file a full-attention layer, in a directory of the
/// state's own, deleted when the state is let go.
/// </summary>
internal sealed class KvSpill : IDisposable
{
    private readonly string _directory;
    private readonly FileStream?[] _files; // one a full-attention layer; null for a recurrent layer
    private readonly int _kvWidth;
    private readonly long _rowBytes;        // keys then values, one position: 2 * kvWidth fp16
    private byte[] _io = [];
    private Half[] _keys = [], _values = [];

    public KvSpill(ModelConfig config, string directory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
        _kvWidth = config.KvHeads * config.HeadDim;
        _rowBytes = 2L * _kvWidth * sizeof(ushort);
        _files = new FileStream?[config.Layers];
        for (var il = 0; il < config.Layers; il++)
            if (!config.IsRecurrent(il))
                _files[il] = new FileStream(Path.Combine(directory, $"kv-{il}.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
    }

    /// <summary>Writes layer <paramref name="layer"/>'s keys and values for <paramref name="position"/> to flash.</summary>
    public void Write(int layer, int position, ReadOnlySpan<float> keys, ReadOnlySpan<float> values)
    {
        var file = _files[layer] ?? throw new InvalidOperationException($"Layer {layer} holds no keys and values to spill.");
        if (_io.Length < _rowBytes) _io = new byte[_rowBytes];
        var half = MemoryMarshal.Cast<byte, Half>(_io.AsSpan(0, (int)_rowBytes));
        for (var i = 0; i < _kvWidth; i++)
        {
            half[i] = (Half)keys[i];
            half[_kvWidth + i] = (Half)values[i];
        }
        file.Seek((long)position * _rowBytes, SeekOrigin.Begin);
        file.Write(_io, 0, (int)_rowBytes);
        file.Flush();
    }

    /// <summary>
    /// Layer <paramref name="layer"/>'s keys and values for positions 0..<paramref name="count"/>-1, read from flash into
    /// shared buffers: keys[t*kvWidth..] and values[t*kvWidth..] for position t, the shape <c>AttendFp16</c> reads. The
    /// buffers are reused, so they are the attention's until the next call.
    /// </summary>
    public (Half[] Keys, Half[] Values) Read(int layer, int count)
    {
        var file = _files[layer] ?? throw new InvalidOperationException($"Layer {layer} holds no keys and values to spill.");
        var need = checked((int)((long)count * _rowBytes));
        if (_io.Length < need) _io = new byte[need];
        file.Seek(0, SeekOrigin.Begin);
        var got = 0;
        while (got < need)
        {
            var n = file.Read(_io, got, need - got);
            if (n == 0) throw new InvalidDataException($"The spill of layer {layer} ends inside position {got / _rowBytes}.");
            got += n;
        }
        if (_keys.Length < count * _kvWidth)
        {
            _keys = new Half[count * _kvWidth];
            _values = new Half[count * _kvWidth];
        }
        var all = MemoryMarshal.Cast<byte, Half>(_io.AsSpan(0, need));
        for (var t = 0; t < count; t++)
        {
            all.Slice(t * 2 * _kvWidth, _kvWidth).CopyTo(_keys.AsSpan(t * _kvWidth, _kvWidth));
            all.Slice(t * 2 * _kvWidth + _kvWidth, _kvWidth).CopyTo(_values.AsSpan(t * _kvWidth, _kvWidth));
        }
        return (_keys, _values);
    }

    public void Dispose()
    {
        foreach (var file in _files) file?.Dispose();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The files are closed; a directory left behind is swept with the rest.
        }
    }
}
