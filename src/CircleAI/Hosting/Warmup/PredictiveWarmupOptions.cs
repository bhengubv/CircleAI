// PredictiveWarmupOptions.cs
//
// (RT-07) Knobs for PredictiveWarmupController.

using System;

namespace CircleAI.Hosting.Warmup;

/// <summary>
/// (RT-07) Configuration for <see cref="PredictiveWarmupController"/>.
/// </summary>
public sealed class PredictiveWarmupOptions
{
    /// <summary>
    /// When <c>false</c> (default), the controller does not pre-warm —
    /// opt-in to avoid surprising callers with extra inference at idle.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// How often the controller asks the predictor about the upcoming
    /// window. Default 30 s — keeps polling cost negligible.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far ahead to forecast. Default 60 s — about the slack a
    /// cheap-phone model load takes before first-token.
    /// </summary>
    public TimeSpan ForecastWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Pre-warm when forecast <c>ProbabilityOfArrival × Confidence</c>
    /// is at or above this threshold. Default 0.5.
    /// </summary>
    public double WarmupThreshold { get; set; } = 0.5;

    /// <summary>
    /// Minimum delay between consecutive pre-warm calls. Keeps the
    /// controller from churning when a spike persists. Default 5 minutes.
    /// </summary>
    public TimeSpan MinTimeBetweenWarmups { get; set; } = TimeSpan.FromMinutes(5);
}
