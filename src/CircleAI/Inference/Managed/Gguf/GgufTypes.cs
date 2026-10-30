// GgufTypes.cs
//
// The two numberings a GGUF file uses: metadata value types (gguf.h, gguf_type) and tensor
// element types (ggml.h, ggml_type). Only the tensor types the Bonsai 2 fleet ships are
// sized here - F32, F16, BF16 and Q8_0 from ggml, PQ2_0 and PTQ1_0 from the pinned fork.
// A tensor of any other type is still listed; reading its bytes says which type it is.

namespace CircleAI.Inference.Managed.Gguf;

/// <summary>A metadata value's type, numbered as the file numbers it.</summary>
public enum GgufType : uint
{
    /// <summary>Unsigned 8-bit integer.</summary>
    UInt8 = 0,

    /// <summary>Signed 8-bit integer.</summary>
    Int8 = 1,

    /// <summary>Unsigned 16-bit integer.</summary>
    UInt16 = 2,

    /// <summary>Signed 16-bit integer.</summary>
    Int16 = 3,

    /// <summary>Unsigned 32-bit integer.</summary>
    UInt32 = 4,

    /// <summary>Signed 32-bit integer.</summary>
    Int32 = 5,

    /// <summary>32-bit float.</summary>
    Float32 = 6,

    /// <summary>One byte, zero for false.</summary>
    Bool = 7,

    /// <summary>UTF-8 text with a 64-bit length.</summary>
    String = 8,

    /// <summary>An element type, a 64-bit count and the elements; never an array of arrays.</summary>
    Array = 9,

    /// <summary>Unsigned 64-bit integer.</summary>
    UInt64 = 10,

    /// <summary>Signed 64-bit integer.</summary>
    Int64 = 11,

    /// <summary>64-bit float.</summary>
    Float64 = 12,
}

/// <summary>A tensor's element type, numbered as ggml numbers it: the types the Bonsai 2 fleet ships.</summary>
public enum GgmlType : uint
{
    /// <summary>32-bit float.</summary>
    F32 = 0,

    /// <summary>16-bit IEEE float.</summary>
    F16 = 1,

    /// <summary>Blocks of 32: an fp16 scale and 32 int8 values (34 bytes).</summary>
    Q8_0 = 8,

    /// <summary>16-bit brain float.</summary>
    BF16 = 30,

    /// <summary>The fork's 2-bit slots: blocks of 128 in 34 bytes (fp16 scale, 32 bytes of codes).</summary>
    PQ2_0 = 142,

    /// <summary>The fork's dense ternary: blocks of 128 in 28 bytes (see Kernels/Ptq1.cs).</summary>
    PTQ1_0 = 143,
}

/// <summary>Sizes of the tensor types this engine reads.</summary>
public static class GgmlTypes
{
    /// <summary>Whether <paramref name="type"/> is one this engine can size and read.</summary>
    public static bool IsKnown(GgmlType type) => type is GgmlType.F32 or GgmlType.F16 or GgmlType.Q8_0
        or GgmlType.BF16 or GgmlType.PQ2_0 or GgmlType.PTQ1_0;

    /// <summary>Elements per block, and the bytes one block takes.</summary>
    public static (int BlockElements, int BlockBytes) Layout(GgmlType type) => type switch
    {
        GgmlType.F32 => (1, 4),
        GgmlType.F16 => (1, 2),
        GgmlType.BF16 => (1, 2),
        GgmlType.Q8_0 => (32, 34),
        GgmlType.PQ2_0 => (128, 34),
        GgmlType.PTQ1_0 => (128, 28),
        _ => throw new NotSupportedException($"ggml type {(uint)type} is not one this engine reads."),
    };
}
