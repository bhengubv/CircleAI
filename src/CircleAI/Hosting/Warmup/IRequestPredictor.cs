// IRequestPredictor.cs
//
// (RT-07) Local-only request-timeline learner. The predictor records
// arrival times and answers "what's the chance of a request in the next
// N seconds?" — used by PredictiveWarmupController to pre-warm the
// generator before a predicted spike.
//
// All implementations must be local-only — no telemetry, no upload.

using System;

namespace CircleAI.Hosting.Warmup;

/// <summary>
/// (RT-07) Forecast of inbound requests over a window. Higher
/// <see cref="ProbabilityOfArrival"/> means the predictor expects at
/// least one request inside the window; <see cref="Confidence"/> is
/// how trustworthy that estimate is given the sample size so far.
/// </summary>
/// <param name="ProbabilityOfArrival">0.0 .. 1.0.</param>
/// <param name="ExpectedCount">Best estimate of how many arrivals to expect.</param>
/// <param name="Confidence">0.0 .. 1.0. Cold-start histograms return ~0.</param>
public readonly record struct ArrivalForecast(
    double ProbabilityOfArrival,
    double ExpectedCount,
    double Confidence);

/// <summary>
/// (RT-07) Local-only predictor that learns request arrival timing and
/// forecasts whether a spike is coming. Plug into
/// <see cref="PredictiveWarmupController"/> to drive automatic
/// pre-warming.
/// </summary>
public interface IRequestPredictor
{
    /// <summary>Record one arrival at <paramref name="utc"/>.</summary>
    void RecordArrival(DateTimeOffset utc);

    /// <summary>
    /// Forecast arrivals in <paramref name="forecastWindow"/> starting at
    /// <paramref name="utcNow"/>. Returns
    /// <see cref="ArrivalForecast"/> with
    /// <see cref="ArrivalForecast.Confidence"/> = 0 when the learner has
    /// no signal yet.
    /// </summary>
    ArrivalForecast Predict(DateTimeOffset utcNow, TimeSpan forecastWindow);

    /// <summary>Total arrivals observed since construction.</summary>
    long ObservedArrivals { get; }
}
