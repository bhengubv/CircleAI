namespace CircleAI.Core;

/// <summary>
/// How a brain's working memory - its attention keys and values - is held. The integers are the
/// MNN bridge's C ABI encoding, so the managed and native layers agree without a table; the C#
/// engine holds the same modes itself (TurboQuant over its full-attention layers).
/// </summary>
/// <remarks>
/// IN CORE, NOT IN THE MNN INTEROP WHERE IT WAS BORN: two engines read it now, and the device's
/// plan picks it for whichever runs.
/// </remarks>
public enum KvCompressionMode
{
    /// <summary>Full FP16 KV cache — default behaviour, always supported.</summary>
    Off = 0,

    /// <summary>TurboQuant at 4 bits per channel — ~4× shrink, &lt; 1% accuracy loss expected.</summary>
    TurboQuant4Bit = 1,

    /// <summary>TurboQuant at 3 bits per channel — ~5× shrink, marginal accuracy loss expected.</summary>
    TurboQuant3Bit = 2,

    /// <summary>TurboQuant at 2 bits per channel — ~8× shrink, noticeable accuracy loss expected.</summary>
    TurboQuant2Bit = 3,
}
