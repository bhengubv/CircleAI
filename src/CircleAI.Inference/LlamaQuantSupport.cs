// LlamaQuantSupport.cs
//
// WHICH GGUF PACKS THE LOADED BACKEND CAN ACTUALLY READ.
//
// The engine gate answers "do we ship llama.cpp"; it does not answer "can THIS
// llama.cpp read THIS pack", and those are different questions. Stock llama.cpp
// refuses Ternary-Bonsai-2-27B outright -- prism's model card says so in as many
// words: it "rejects PQ2_0 and PTQ1_0 as unknown types", and the ternary kernels
// live only in the PrismML-Eng fork. A catalogue that knows the engine but not the
// quant would offer Bonsai on a stock build and fail at load.
//
// SO THE SEAM IS THE QUANT, NOT THE FORK. Modelling it as "stock vs prism" ages
// badly: the next exotic pack comes from someone else's fork and the enum has to
// grow again. A backend declares the quant TYPE NAMES it reads; a pack declares
// the one it is in; a pack is runnable when the loaded backend claims its type.
// New quant, new row in a table -- no enum churn, no assessor change.
//
// The backend identifies itself at RUNTIME through LlamaGenerator.NativeVersion,
// which is the bridge + llama.cpp build string, so nothing here is decided at
// compile time. A build that is not present claims nothing, which is why a device
// with no native library offers no GGUF at all rather than offering all of them.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Inference;

/// <summary>A llama.cpp build and the GGUF quantisation types it can read.</summary>
public sealed record LlamaBackend(string Id, IReadOnlySet<string> Quants)
{
    /// <summary>Whether this build reads packs of the given quantisation.</summary>
    public bool Reads(string? quant) =>
        !string.IsNullOrWhiteSpace(quant) && Quants.Contains(quant.Trim());
}

/// <summary>
/// Maps the loaded native bridge to the packs it can read.
/// </summary>
public static class LlamaQuantSupport
{
    private static HashSet<string> Set(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Quantisations any upstream llama.cpp reads.
    /// </summary>
    /// <remarks>
    /// TQ1_0 and TQ2_0 are here and are NOT the Bonsai types. Upstream's ternary
    /// formats are a 256-element superblock (54 bytes, 1.6875 bpw); Bonsai's PTQ1_0
    /// is a 128-element block of 28 bytes with its own fp16 scale, plus a Hadamard
    /// rotation folded into the weights that upstream knows nothing about. Names
    /// that look alike are the trap this table exists to avoid.
    /// </remarks>
    public static readonly LlamaBackend Stock = new("llama.cpp", Set(
        "F32", "F16", "BF16",
        "Q4_0", "Q4_1", "Q5_0", "Q5_1", "Q8_0", "Q8_1",
        "Q2_K", "Q3_K_S", "Q3_K_M", "Q3_K_L", "Q3_K_XL",
        "Q4_K_S", "Q4_K_M", "Q5_K_S", "Q5_K_M", "Q6_K", "Q6_K_XL",
        "IQ1_S", "IQ1_M", "IQ2_XXS", "IQ2_XS", "IQ2_S", "IQ2_M",
        "IQ3_XXS", "IQ3_S", "IQ3_M", "IQ4_XS", "IQ4_NL",
        "TQ1_0", "TQ2_0"));

    /// <summary>
    /// PrismML-Eng/llama.cpp: everything upstream reads, plus the Bonsai ternary packs.
    /// </summary>
    /// <remarks>
    /// A SUPERSET, which is why shipping it costs nothing in coverage. PTQ1_0 packs
    /// trits densely at 1.75 bpw (5.95 GB for Bonsai 2 27B); PQ2_0 gives each trit a
    /// 2-bit slot at 2.13 bpw (7.21 GB) and decodes cheaper. Q2_0 and PQ1_0 appear
    /// on the fork's own branches beside them.
    /// </remarks>
    public static readonly LlamaBackend Prism = new("prism-llama.cpp",
        Set(Stock.Quants.Concat(new[] { "PTQ1_0", "PQ2_0", "PQ1_0", "Q2_0" }).ToArray()));

    /// <summary>Every backend this code knows how to recognise.</summary>
    public static IReadOnlyList<LlamaBackend> Known => new[] { Prism, Stock };

    /// <summary>
    /// The backend actually loaded, or <c>null</c> when there is no native bridge.
    /// </summary>
    /// <remarks>
    /// Read from the runtime version string rather than a build flag, so one APK can
    /// carry either library and still tell the truth about what it can open. The fork
    /// is matched first and by name: its build string carries "prism", and a stock
    /// string must never be read as the fork -- that direction of error offers Bonsai
    /// on a build that cannot open it, which is the failure this whole file prevents.
    /// An unrecognised non-empty version is treated as STOCK, the conservative
    /// reading: it under-claims rather than over-claims.
    /// </remarks>
    public static LlamaBackend? Loaded
    {
        get
        {
            var v = LlamaGenerator.NativeVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            return v.Contains("prism", StringComparison.OrdinalIgnoreCase) ? Prism : Stock;
        }
    }

    /// <summary>
    /// Whether a pack of this quantisation can be opened on this device right now.
    /// </summary>
    /// <remarks>
    /// An unknown or absent quantisation string answers <c>true</c> when a backend is
    /// loaded. The catalogue's older MNN rows carry values like "MNN-Q4" that say
    /// nothing about GGUF, and this predicate must not be what removes them — the
    /// ENGINE gate is what keeps a non-GGUF row away from llama.cpp. This one only
    /// ever refuses a quantisation it positively knows the loaded build cannot read.
    /// </remarks>
    public static bool CanRead(string? quantization)
    {
        var backend = Loaded;
        if (backend is null) return false;                       // no bridge: nothing opens
        if (string.IsNullOrWhiteSpace(quantization)) return true; // unstated: engine gate decides
        if (backend.Reads(quantization)) return true;

        // Refuse ONLY when some other known backend claims it — that is the case where
        // the pack is real and this build is the wrong one (Bonsai on stock). A name
        // nobody claims is left to the engine gate rather than guessed at.
        return !Known.Any(b => b.Reads(quantization));
    }
}
