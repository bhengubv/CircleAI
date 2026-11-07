// KernelChoice.cs
//
// SCALAR KERNELS, AFTER AN ILLEGAL INSTRUCTION (S6).
//
// A runtime can report a vector unit the chip does not really have, or compile one instruction
// past what this core decodes, and the answer is a SIGILL: the inference process dies with no
// exception and nothing in it can catch the fault. Wolverine reads that death in the main
// process and turns the vector kernels off (BrainTuning's "kernels" knob); every kernel this
// engine picks by "fastest available" asks here first.
//
// WHAT IT GOVERNS TODAY, SAID PLAINLY. The model's products run on the lookup-table kernel
// (Ptq1Lut), which is plain pointer code with no vector instructions at all, so a scalar-only
// device loses nothing on that path. What it stops is every path chosen by Ptq1.Best - the
// AdvSimd, dot-product, AVX-VNNI, AVX-512, AVX2 and Vector128 kernels - and any kernel added later must take its
// "fastest" through Allows, or this switch cannot protect it.

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Which kernels this process may use: everything the CPU offers, or no vector instructions.</summary>
public static class KernelChoice
{
    private static volatile bool _scalarOnly;

    /// <summary>
    /// True after an illegal instruction: no kernel that uses vector instructions is chosen.
    /// Set where the brain is built, from BrainTuning.
    /// </summary>
    public static bool ScalarOnly
    {
        get => _scalarOnly;
        set => _scalarOnly = value;
    }

    /// <summary>Whether <paramref name="path"/> runs vector instructions.</summary>
    public static bool IsVector(KernelPath path) =>
        path is KernelPath.Vector128 or KernelPath.AdvSimd or KernelPath.Dp or KernelPath.Avx2 or KernelPath.Avx512 or KernelPath.AvxVnni;

    /// <summary>Whether this process may run <paramref name="path"/> now.</summary>
    public static bool Allows(KernelPath path) => !(ScalarOnly && IsVector(path));

    /// <summary>
    /// The path to run when <paramref name="fastest"/> is what the CPU offers: itself, or the
    /// scalar reference when vector instructions are switched off.
    /// </summary>
    public static KernelPath Pick(KernelPath fastest) => Allows(fastest) ? fastest : KernelPath.Scalar;
}
