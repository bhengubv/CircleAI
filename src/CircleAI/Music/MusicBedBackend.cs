namespace CircleAI.Music;

/// <summary>
/// Which engine actually produced a <see cref="MusicBed"/>.
/// </summary>
/// <remarks>
/// This mirrors the intent of <c>CircleAI.Inference.SelectionQuality</c>:
/// <list type="bullet">
///   <item>
///     <see cref="Procedural"/> is the on-device heuristic fallback — the
///     equivalent of <c>SelectionQuality.HeuristicFallback</c>: no model, no
///     download, no RAM, and it always works.
///   </item>
///   <item>
///     <see cref="Neural"/> is a catalogued, downloaded music model — the
///     equivalent of <c>SelectionQuality.Good</c> / <c>BelowFloor</c>. It is
///     absent until a model bundle is present on the device.
///   </item>
/// </list>
/// Callers can record this on a produced clip so telemetry and UI can honestly
/// distinguish "real model" output from the procedural safety net.
/// </remarks>
public enum MusicBedBackend
{
    /// <summary>Pure-managed procedural synthesiser. Always available, zero deps.</summary>
    Procedural = 0,

    /// <summary>A downloaded neural music model selected from the catalogue.</summary>
    Neural,
}
