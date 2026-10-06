// BiosignalKind.cs
//
// Canonical taxonomy of biosignals ingested by Circle AI's wearable layer.
// Integer values are stable across language ports — do not renumber.

namespace CircleAI.Wearable.Biosignals;

/// <summary>
/// Canonical kinds of biosignal samples Circle AI consumes from wearables.
/// </summary>
public enum BiosignalKind
{
    /// <summary>Heart rate, beats per minute.</summary>
    HeartRate = 0,

    /// <summary>Heart rate variability, RMSSD in milliseconds.</summary>
    HeartRateVariability = 1,

    /// <summary>Peripheral oxygen saturation, percent (0-100).</summary>
    OxygenSaturation = 2,

    /// <summary>Accelerometer magnitude, m/s^2.</summary>
    Accelerometer = 3,

    /// <summary>Body temperature, degrees Celsius.</summary>
    BodyTemperature = 4,

    /// <summary>Sleep stage encoded as a float: 0=awake, 1=light, 2=deep, 3=REM.</summary>
    SleepStage = 5,

    /// <summary>Step count (cumulative or delta — see <see cref="BiosignalSample.IsCumulative"/>).</summary>
    Steps = 6,

    /// <summary>Galvanic skin response, microsiemens.</summary>
    GalvanicSkinResponse = 7,

    /// <summary>Catch-all for vendor-specific or future signals.</summary>
    Unknown = 8,
}
