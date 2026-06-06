// BetaLloydMaxCodebook.cs
//
// Lloyd-Max scalar quantizer optimised for the Beta((d-1)/2, (d-1)/2)
// distribution on [-1, 1] — the per-coordinate distribution of a unit vector
// on the (d-1)-sphere after orthogonal rotation.
//
// The codebook depends only on (bits, dim) so we compute once per pair and
// cache. For typical embeddings (dim ≥ 64) the Beta is sharply concentrated
// near zero and the codebook converges in ~50–200 Lloyd-Max iterations.
//
// Port of the Rust reference implementation in turbovec/src/codebook.rs
// (RyanCodrai/turbovec, MIT).

using System;
using System.Collections.Concurrent;

namespace CircleAI.Core.Compression;

/// <summary>
/// Optimal scalar quantizer (Lloyd-Max) for the Beta((d-1)/2, (d-1)/2)
/// distribution on [-1, 1].
/// </summary>
/// <param name="Boundaries">
/// Length 2^bits - 1. boundaries[i] separates centroid i from centroid i+1.
/// </param>
/// <param name="Centroids">
/// Length 2^bits. centroids[i] is the reconstruction value for bin i.
/// </param>
public sealed record BetaCodebook(float[] Boundaries, float[] Centroids);

/// <summary>
/// Computes Lloyd-Max codebooks for Beta((d-1)/2, (d-1)/2). Cached by
/// (bits, dim).
/// </summary>
public static class BetaLloydMaxCodebook
{
    private static readonly ConcurrentDictionary<(int bits, int dim), BetaCodebook> _cache = new();

    /// <summary>
    /// Returns the codebook for the given bit width and dimension, computing
    /// it on first request.
    /// </summary>
    public static BetaCodebook Get(int bits, int dim)
    {
        if (bits is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bits), "bits must be in 1..8.");
        if (dim <= 1)
            throw new ArgumentOutOfRangeException(nameof(dim), "dim must be > 1.");
        return _cache.GetOrAdd((bits, dim), key => Compute(key.bits, key.dim));
    }

    /// <summary>
    /// Returns the bin index for <paramref name="value"/> against
    /// <paramref name="boundaries"/>. Linear scan; for small codebooks (≤ 256
    /// entries) this is faster than a branch-heavy binary search.
    /// </summary>
    public static ushort BinFor(float value, ReadOnlySpan<float> boundaries)
    {
        for (int i = 0; i < boundaries.Length; i++)
        {
            if (value < boundaries[i]) return (ushort)i;
        }
        return (ushort)boundaries.Length;
    }

    // ── Lloyd-Max iteration ──────────────────────────────────────────────

    private static BetaCodebook Compute(int bits, int dim, int maxIter = 200, double tol = 1e-12)
    {
        double a = (dim - 1.0) / 2.0;
        int nLevels = 1 << bits;

        // Initial centroids: evenly spaced across ±3σ of the Beta-on-[-1,1].
        double std = Math.Sqrt(2.0 * a / ((2.0 * a + 1.0) * 4.0 * a));
        double spread = 3.0 * std;
        var centroids = new double[nLevels];
        for (int i = 0; i < nLevels; i++)
            centroids[i] = -spread + 2.0 * spread * i / (nLevels - 1);

        for (int iter = 0; iter < maxIter; iter++)
        {
            // Boundaries = midpoints between adjacent centroids.
            var boundaries = new double[nLevels - 1];
            for (int i = 0; i < nLevels - 1; i++)
                boundaries[i] = (centroids[i] + centroids[i + 1]) / 2.0;

            var edges = new double[nLevels + 1];
            edges[0] = -1.0;
            for (int i = 0; i < boundaries.Length; i++) edges[i + 1] = boundaries[i];
            edges[nLevels] = 1.0;

            var newCentroids = new double[nLevels];
            for (int i = 0; i < nLevels; i++)
            {
                double lo = edges[i];
                double hi = edges[i + 1];
                double cdfLo = BetaCdfSymmetric(a, (lo + 1.0) / 2.0);
                double cdfHi = BetaCdfSymmetric(a, (hi + 1.0) / 2.0);
                double prob = cdfHi - cdfLo;

                if (prob < 1e-15)
                {
                    newCentroids[i] = centroids[i];
                }
                else
                {
                    double mean = AdaptiveSimpson(
                        x => x * BetaPdfSymmetric(a, (x + 1.0) / 2.0) / 2.0,
                        lo, hi, 1e-14, 50);
                    newCentroids[i] = mean / prob;
                }
            }

            double maxChange = 0.0;
            for (int i = 0; i < nLevels; i++)
                maxChange = Math.Max(maxChange, Math.Abs(centroids[i] - newCentroids[i]));
            centroids = newCentroids;

            if (maxChange < tol) break;
        }

        var finalBoundaries = new float[nLevels - 1];
        for (int i = 0; i < nLevels - 1; i++)
            finalBoundaries[i] = (float)((centroids[i] + centroids[i + 1]) / 2.0);
        var finalCentroids = new float[nLevels];
        for (int i = 0; i < nLevels; i++) finalCentroids[i] = (float)centroids[i];
        return new BetaCodebook(finalBoundaries, finalCentroids);
    }

    // ── Beta(a, a) PDF / CDF on [0, 1] ───────────────────────────────────
    // The "Symmetric" suffix is a reminder that we always use shape Beta(a, a).

    private static double BetaPdfSymmetric(double a, double x)
    {
        if (x <= 0.0 || x >= 1.0) return 0.0;
        // f(x) = x^(a-1) * (1-x)^(a-1) / B(a, a)
        // Use log-space for numerical stability at large a.
        double logPdf = (a - 1.0) * Math.Log(x) + (a - 1.0) * Math.Log(1.0 - x)
                       - LogBeta(a, a);
        return Math.Exp(logPdf);
    }

    private static double BetaCdfSymmetric(double a, double x)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;
        return RegularizedIncompleteBeta(a, a, x);
    }

    // log Beta(a, b) via log Gamma — uses Lanczos approximation in Math.
    private static double LogBeta(double a, double b) =>
        LogGamma(a) + LogGamma(b) - LogGamma(a + b);

    /// <summary>
    /// log Γ(x) for x > 0 via the Lanczos approximation (g = 7, n = 9).
    /// Standard recipe; accurate to ~1e-13 across the range we need.
    /// </summary>
    private static double LogGamma(double x)
    {
        // Coefficients for g = 7.
        double[] c = {
            0.99999999999980993, 676.5203681218851, -1259.1392167224028,
            771.32342877765313, -176.61502916214059, 12.507343278686905,
            -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7
        };
        if (x < 0.5)
        {
            // Reflection: Γ(x)Γ(1-x) = π/sin(πx)
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);
        }
        x -= 1.0;
        double t = x + 7.5;
        double sum = c[0];
        for (int i = 1; i < c.Length; i++) sum += c[i] / (x + i);
        return 0.5 * Math.Log(2.0 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(sum);
    }

    /// <summary>
    /// Regularised incomplete beta function I_x(a, b).
    /// Continued-fraction expansion (Numerical Recipes 6.4).
    /// </summary>
    private static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(x), "x must be in [0, 1].");
        if (x == 0.0 || x == 1.0) return x;

        double bt = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b)
                            + a * Math.Log(x) + b * Math.Log(1.0 - x));
        if (x < (a + 1.0) / (a + b + 2.0))
            return bt * BetaContinuedFraction(a, b, x) / a;
        return 1.0 - bt * BetaContinuedFraction(b, a, 1.0 - x) / b;
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int maxIter = 200;
        const double eps = 3e-15;
        const double fpmin = 1e-300;

        double qab = a + b;
        double qap = a + 1.0;
        double qam = a - 1.0;
        double c = 1.0;
        double d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < fpmin) d = fpmin;
        d = 1.0 / d;
        double h = d;

        for (int m = 1; m <= maxIter; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1.0 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1.0) < eps) return h;
        }
        return h; // best effort if no convergence
    }

    // ── Adaptive Simpson integration ─────────────────────────────────────

    private static double AdaptiveSimpson(Func<double, double> f,
                                          double a, double b, double tol, int maxDepth)
    {
        double mid = (a + b) / 2.0;
        double fa = f(a), fb = f(b), fm = f(mid);
        double whole = (b - a) / 6.0 * (fa + 4.0 * fm + fb);
        return AdaptiveSimpsonRec(f, a, b, fa, fb, fm, whole, tol, maxDepth);
    }

    private static double AdaptiveSimpsonRec(Func<double, double> f,
                                             double a, double b,
                                             double fa, double fb, double fm,
                                             double whole, double tol, int depth)
    {
        double mid = (a + b) / 2.0;
        double m1 = (a + mid) / 2.0;
        double m2 = (mid + b) / 2.0;
        double fm1 = f(m1), fm2 = f(m2);
        double left = (mid - a) / 6.0 * (fa + 4.0 * fm1 + fm);
        double right = (b - mid) / 6.0 * (fm + 4.0 * fm2 + fb);
        double refined = left + right;

        if (depth == 0 || Math.Abs(refined - whole) < 15.0 * tol)
            return refined + (refined - whole) / 15.0;
        return AdaptiveSimpsonRec(f, a, mid, fa, fm, fm1, left, tol / 2.0, depth - 1)
             + AdaptiveSimpsonRec(f, mid, b, fm, fb, fm2, right, tol / 2.0, depth - 1);
    }
}
