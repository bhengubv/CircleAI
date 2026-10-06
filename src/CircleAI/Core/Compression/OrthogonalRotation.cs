// OrthogonalRotation.cs
//
// Deterministic random orthogonal rotation matrix used by TurboQuant.
// Constructed via QR decomposition of a seeded Gaussian matrix. After this
// rotation is applied to a unit vector on the (d-1)-sphere, each coordinate
// follows Beta((d-1)/2, (d-1)/2) on [-1, 1] — which is the distribution the
// Lloyd-Max codebook is optimised for.
//
// The rotation matrix only depends on the dimension; we cache one per dim
// because constructing it is O(d^3).

using System;
using System.Collections.Concurrent;

namespace CircleAI.Core.Compression;

/// <summary>
/// Provides a deterministic random orthogonal rotation matrix for a given
/// dimension. Caches one matrix per dimension.
/// </summary>
public static class OrthogonalRotation
{
    /// <summary>
    /// Fixed seed shared across every CircleAI process so the rotation is
    /// portable: compress on device A, decode on device B works identically.
    /// </summary>
    public const ulong RotationSeed = 0xC1C1EA10C1C1EA10UL;

    private static readonly ConcurrentDictionary<int, float[]> _cache = new();

    /// <summary>
    /// Returns the dim×dim orthogonal matrix in row-major layout. Length is
    /// dim*dim. Cached after the first call for a given dimension.
    /// </summary>
    public static float[] GetMatrix(int dim)
    {
        if (dim <= 0) throw new ArgumentOutOfRangeException(nameof(dim));
        return _cache.GetOrAdd(dim, BuildMatrix);
    }

    /// <summary>
    /// Multiplies the rotation matrix by <paramref name="vector"/> in place,
    /// writing into <paramref name="output"/>. output[i] = Σ R[i,j] * vector[j].
    /// </summary>
    public static void Rotate(int dim, ReadOnlySpan<float> vector, Span<float> output)
    {
        if (vector.Length != dim) throw new ArgumentException("vector length must equal dim.");
        if (output.Length != dim) throw new ArgumentException("output length must equal dim.");
        var matrix = GetMatrix(dim);
        for (int i = 0; i < dim; i++)
        {
            float sum = 0f;
            int rowStart = i * dim;
            for (int j = 0; j < dim; j++)
            {
                sum += matrix[rowStart + j] * vector[j];
            }
            output[i] = sum;
        }
    }

    /// <summary>
    /// Inverse rotation — multiplies the TRANSPOSE of the rotation matrix
    /// by <paramref name="vector"/>. The transpose of an orthogonal matrix is
    /// also its inverse.
    /// </summary>
    public static void Unrotate(int dim, ReadOnlySpan<float> vector, Span<float> output)
    {
        if (vector.Length != dim) throw new ArgumentException("vector length must equal dim.");
        if (output.Length != dim) throw new ArgumentException("output length must equal dim.");
        var matrix = GetMatrix(dim);
        for (int i = 0; i < dim; i++)
        {
            float sum = 0f;
            for (int j = 0; j < dim; j++)
            {
                // Transpose: matrix[j, i] instead of matrix[i, j]
                sum += matrix[j * dim + i] * vector[j];
            }
            output[i] = sum;
        }
    }

    // ── Construction ──────────────────────────────────────────────────────

    private static float[] BuildMatrix(int dim)
    {
        // 1. Generate a seeded Gaussian matrix G (dim × dim).
        var gauss = new double[dim * dim];
        var rng = new SeededGaussian(RotationSeed);
        for (int i = 0; i < gauss.Length; i++) gauss[i] = rng.Sample();

        // 2. QR decomposition via modified Gram-Schmidt — numerically stable
        //    for the dimensions we encounter (≤ 4096).
        var q = ModifiedGramSchmidt(gauss, dim);

        // 3. Sign-correct columns so Q is a deterministic orthogonal matrix
        //    (matches the Rust reference implementation's behaviour).
        SignCorrectColumns(q, dim);

        // 4. Convert to row-major float32.
        var result = new float[dim * dim];
        for (int i = 0; i < result.Length; i++) result[i] = (float)q[i];
        return result;
    }

    /// <summary>
    /// Modified Gram-Schmidt QR. Returns Q (orthonormal columns) in row-major
    /// flat layout. The input <paramref name="g"/> is destroyed.
    /// </summary>
    private static double[] ModifiedGramSchmidt(double[] g, int dim)
    {
        var q = new double[dim * dim];

        for (int j = 0; j < dim; j++)
        {
            // Copy column j of g into a working vector.
            for (int i = 0; i < dim; i++) q[i * dim + j] = g[i * dim + j];

            // Subtract projections onto already-processed columns.
            for (int k = 0; k < j; k++)
            {
                double dot = 0.0;
                for (int i = 0; i < dim; i++) dot += q[i * dim + j] * q[i * dim + k];
                for (int i = 0; i < dim; i++) q[i * dim + j] -= dot * q[i * dim + k];
            }

            // Normalise column j.
            double norm = 0.0;
            for (int i = 0; i < dim; i++) norm += q[i * dim + j] * q[i * dim + j];
            norm = Math.Sqrt(norm);
            if (norm < 1e-15)
                throw new InvalidOperationException(
                    $"Gram-Schmidt produced a near-zero column at j={j} (dim={dim}). " +
                    "This is statistically impossible for a Gaussian matrix; check the RNG seed.");
            double inv = 1.0 / norm;
            for (int i = 0; i < dim; i++) q[i * dim + j] *= inv;
        }
        return q;
    }

    private static void SignCorrectColumns(double[] q, int dim)
    {
        for (int j = 0; j < dim; j++)
        {
            // Diagonal-based sign convention: ensure q[j,j] >= 0.
            double diag = q[j * dim + j];
            if (diag < 0.0)
            {
                for (int i = 0; i < dim; i++) q[i * dim + j] = -q[i * dim + j];
            }
        }
    }
}

/// <summary>
/// Deterministic Gaussian sampler — Box-Muller over a seeded SplitMix64 PRNG.
/// We hand-roll this rather than use Random so the output is reproducible
/// across .NET versions and platforms.
/// </summary>
internal sealed class SeededGaussian
{
    private ulong _state;
    private bool _hasSpare;
    private double _spare;

    public SeededGaussian(ulong seed) => _state = seed == 0 ? 0xDEADBEEFCAFEBABEUL : seed;

    public double Sample()
    {
        if (_hasSpare) { _hasSpare = false; return _spare; }

        // Two uniforms in (0, 1].
        double u, v;
        do { u = NextUniform(); } while (u <= 1e-300);
        v = NextUniform();
        double magnitude = Math.Sqrt(-2.0 * Math.Log(u));
        double angle = 2.0 * Math.PI * v;
        _spare = magnitude * Math.Sin(angle);
        _hasSpare = true;
        return magnitude * Math.Cos(angle);
    }

    private double NextUniform()
    {
        // SplitMix64 step.
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z = z ^ (z >> 31);
        // Convert top 53 bits to a double in [0, 1).
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}
