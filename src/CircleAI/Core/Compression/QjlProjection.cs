// QjlProjection.cs
//
// TurboQuant's second stage (arxiv:2504.19874, after QJL, arxiv:2406.03482): a fixed Gaussian
// matrix S (d x d, entries N(0, 1), from a seed) of which only the SIGNS of S r are kept - one bit
// a coordinate - for whatever the first stage left over, r. With r's norm beside them,
//
//     <q, r>  ~  sqrt(pi/2) / d * |r| * <S q, sign(S r)>
//
// and over S that estimate is unbiased, where the first stage alone - the MSE quantiser - pulls
// every inner product toward zero by about its own distortion. The price is variance: at equal
// bits the second stage trades a bias for noise, which is why both forms are kept and measured.

using System.Collections.Concurrent;

namespace CircleAI.Core.Compression;

/// <summary>The one-bit Johnson-Lindenstrauss projection TurboQuant's second stage keeps the signs of.</summary>
public sealed class QjlProjection
{
    /// <summary>The seed every CircleAI process shares.</summary>
    public const ulong Seed = 0x51A1_7B17_C1C1_EA10UL;

    private static readonly ConcurrentDictionary<int, QjlProjection> Cache = new();

    private readonly float[] _matrix; // row-major, Dim x Dim

    private QjlProjection(int dim)
    {
        Dim = dim;
        _matrix = new float[dim * dim];
        var gaussian = new SeededGaussian(Seed ^ ((ulong)dim * 0x9E3779B97F4A7C15UL));
        for (var i = 0; i < _matrix.Length; i++) _matrix[i] = (float)gaussian.Sample();
        Scale = MathF.Sqrt(MathF.PI / 2f) / dim;
    }

    /// <summary>The projection for vectors of <paramref name="dim"/> values (made once, then shared).</summary>
    public static QjlProjection For(int dim)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dim, 2);
        return Cache.GetOrAdd(dim, d => new QjlProjection(d));
    }

    /// <summary>The width it projects.</summary>
    public int Dim { get; }

    /// <summary>sqrt(pi/2) / d: what turns a sum of signed projections back into an inner product.</summary>
    public float Scale { get; }

    /// <summary>Bytes the signs of one vector take.</summary>
    public int SignBytes => (Dim + 7) / 8;

    /// <summary><paramref name="y"/> = S <paramref name="x"/>.</summary>
    public void Project(ReadOnlySpan<float> x, Span<float> y)
    {
        Check(x.Length);
        Check(y.Length);
        for (var i = 0; i < Dim; i++)
        {
            var row = _matrix.AsSpan(i * Dim, Dim);
            var sum = 0f;
            for (var j = 0; j < Dim; j++) sum += row[j] * x[j];
            y[i] = sum;
        }
    }

    /// <summary>The signs of S <paramref name="residual"/>, one bit each (set = not negative), into <paramref name="signs"/>.</summary>
    public void Signs(ReadOnlySpan<float> residual, Span<byte> signs)
    {
        Check(residual.Length);
        if (signs.Length < SignBytes) throw new ArgumentException($"The signs take {SignBytes} bytes.", nameof(signs));
        signs[..SignBytes].Clear();
        for (var i = 0; i < Dim; i++)
        {
            var row = _matrix.AsSpan(i * Dim, Dim);
            var sum = 0f;
            for (var j = 0; j < Dim; j++) sum += row[j] * residual[j];
            if (sum >= 0f) signs[i >> 3] |= (byte)(1 << (i & 7));
        }
    }

    /// <summary>
    /// The estimate of &lt;q, r&gt; from a query already projected (<see cref="Project"/>), the signs
    /// of r and r's norm.
    /// </summary>
    public float Estimate(ReadOnlySpan<float> projectedQuery, ReadOnlySpan<byte> signs, float residualNorm)
    {
        Check(projectedQuery.Length);
        var sum = 0f;
        for (var i = 0; i < Dim; i++) sum += (signs[i >> 3] & (1 << (i & 7))) != 0 ? projectedQuery[i] : -projectedQuery[i];
        return Scale * residualNorm * sum;
    }

    /// <summary>
    /// The vector whose inner product with any q is <see cref="Estimate"/>'s: sqrt(pi/2) / d * |r| * Sᵀ z,
    /// added into <paramref name="into"/>.
    /// </summary>
    public void AddDequantized(ReadOnlySpan<byte> signs, float residualNorm, Span<float> into)
    {
        Check(into.Length);
        var scale = Scale * residualNorm;
        for (var i = 0; i < Dim; i++)
        {
            var z = (signs[i >> 3] & (1 << (i & 7))) != 0 ? scale : -scale;
            var row = _matrix.AsSpan(i * Dim, Dim);
            for (var j = 0; j < Dim; j++) into[j] += z * row[j];
        }
    }

    private void Check(int length)
    {
        if (length != Dim) throw new ArgumentException($"This projection takes {Dim} values, not {length}.");
    }
}
