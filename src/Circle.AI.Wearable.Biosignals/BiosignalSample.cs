// BiosignalSample.cs
//
// A single measurement from a wearable sensor.

namespace Circle.AI.Wearable.Biosignals;

/// <summary>
/// A single biosignal measurement.
/// </summary>
/// <param name="Id">Stable identifier for this sample.</param>
/// <param name="Kind">The kind of signal — see <see cref="BiosignalKind"/>.</param>
/// <param name="Value">Numeric value in the canonical unit for the kind.</param>
/// <param name="Unit">Canonical unit string ("bpm", "ms", "%", "m/s^2", "celsius", "stage", "count", "uS").</param>
/// <param name="Confidence">Sensor-reported confidence in [0, 1]. Samples below 0.5 are typically ignored by the mapper.</param>
/// <param name="IsCumulative">True when <see cref="Kind"/> is <see cref="BiosignalKind.Steps"/> and the value is total-since-epoch rather than a delta.</param>
/// <param name="MeasuredAt">UTC time the sample was captured.</param>
public sealed record BiosignalSample(
    Guid Id,
    BiosignalKind Kind,
    float Value,
    string Unit,
    float Confidence,
    bool IsCumulative,
    DateTimeOffset MeasuredAt
)
{
    /// <summary>
    /// Creates a fresh sample with a new <see cref="Guid"/> id, current UTC timestamp,
    /// and confidence clamped to [0, 1].
    /// </summary>
    /// <param name="kind">Signal kind.</param>
    /// <param name="value">Measured value in canonical units.</param>
    /// <param name="unit">Canonical unit string.</param>
    /// <param name="confidence">Sensor confidence; clamped to [0, 1].</param>
    /// <param name="isCumulative">True if value is cumulative (e.g. step count since midnight).</param>
    /// <returns>A new <see cref="BiosignalSample"/>.</returns>
    public static BiosignalSample Create(
        BiosignalKind kind,
        float value,
        string unit,
        float confidence = 1.0f,
        bool isCumulative = false) =>
        new(
            Guid.NewGuid(),
            kind,
            value,
            unit,
            Math.Clamp(confidence, 0f, 1f),
            isCumulative,
            DateTimeOffset.UtcNow);
}
