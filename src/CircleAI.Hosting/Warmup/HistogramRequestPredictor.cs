// HistogramRequestPredictor.cs
//
// (RT-07) Histogram-based request predictor. Maintains a 24-hour-of-day
// arrival rate estimate over a rolling N-day window (default 7 days).
// Forecast = expected arrivals during the window, derived from the
// per-minute rate at the corresponding clock time on past days.
//
// All counting is in-process; no telemetry, no upload. Thread-safe.

using System;
using System.Threading;

namespace CircleAI.Hosting.Warmup;

/// <summary>
/// (RT-07) Default <see cref="IRequestPredictor"/> — keeps a histogram of
/// per-minute arrival rates over a rolling window of recent days, then
/// forecasts the next-window rate from that histogram.
/// </summary>
public sealed class HistogramRequestPredictor : IRequestPredictor
{
    private const int    MinutesPerDay     = 24 * 60;
    private const double WarmConfidence    = 1.0;
    private const int    MinSamplesForFullConfidence = 25;

    private readonly int     _historyDays;
    private readonly double[] _perMinuteRate; // index = minute-of-day; value = avg arrivals/minute observed
    private readonly int[]    _perMinuteCount;
    private readonly object  _gate = new();
    private long _observed;

    /// <summary>
    /// Construct a histogram predictor with a rolling history of
    /// <paramref name="historyDays"/> days. Default 7 — one calendar week.
    /// </summary>
    public HistogramRequestPredictor(int historyDays = 7)
    {
        if (historyDays <= 0) throw new ArgumentOutOfRangeException(nameof(historyDays));
        _historyDays   = historyDays;
        _perMinuteRate  = new double[MinutesPerDay];
        _perMinuteCount = new int[MinutesPerDay];
    }

    /// <inheritdoc/>
    public long ObservedArrivals => Interlocked.Read(ref _observed);

    /// <inheritdoc/>
    public void RecordArrival(DateTimeOffset utc)
    {
        var minute = (utc.UtcDateTime.Hour * 60) + utc.UtcDateTime.Minute;
        lock (_gate)
        {
            var cnt = ++_perMinuteCount[minute];
            // EWMA over the last `historyDays` of observations at this slot.
            // alpha shrinks as cnt grows, so early samples dominate less.
            var alpha = 2.0 / (Math.Min(cnt, _historyDays) + 1);
            _perMinuteRate[minute] = (alpha * 1.0) + ((1 - alpha) * _perMinuteRate[minute]);
        }
        Interlocked.Increment(ref _observed);
    }

    /// <inheritdoc/>
    public ArrivalForecast Predict(DateTimeOffset utcNow, TimeSpan forecastWindow)
    {
        if (forecastWindow <= TimeSpan.Zero)
            return new ArrivalForecast(0, 0, 0);
        var observed = ObservedArrivals;
        if (observed == 0)
            return new ArrivalForecast(0, 0, 0);

        var minute = (utcNow.UtcDateTime.Hour * 60) + utcNow.UtcDateTime.Minute;
        var minutes = Math.Max(1, (int)Math.Ceiling(forecastWindow.TotalMinutes));
        double expected = 0;
        int    coveredSamples = 0;
        lock (_gate)
        {
            for (var i = 0; i < minutes; i++)
            {
                var idx = (minute + i) % MinutesPerDay;
                expected       += _perMinuteRate[idx];
                coveredSamples += _perMinuteCount[idx];
            }
        }

        // Poisson tail: P(>=1 arrival) = 1 - exp(-lambda).
        var probability = 1.0 - Math.Exp(-expected);
        // Confidence rises as the per-minute slots accumulate samples.
        var confidence = Math.Min(WarmConfidence,
            (double)coveredSamples / (MinSamplesForFullConfidence * minutes));
        return new ArrivalForecast(probability, expected, confidence);
    }

    /// <summary>Test-only — wipe state.</summary>
    internal void ResetForTests()
    {
        lock (_gate)
        {
            Array.Clear(_perMinuteRate);
            Array.Clear(_perMinuteCount);
        }
        Interlocked.Exchange(ref _observed, 0);
    }
}
