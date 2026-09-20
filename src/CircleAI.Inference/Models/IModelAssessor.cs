// IModelAssessor.cs
//
// Fills the catalogue's two computed columns. Given a device snapshot, it walks
// every catalogue row and writes `compatible` (can THIS device run it now) and
// `rank` (quality/fit score) back through IModelCatalog.SetAssessment. This is
// what makes selection build-independent: the feed adds rows, the assessor
// re-scores them against the live device, the selector reads the result.
//
// Runs on: catalogue update (a feed just upserted rows), device change (RAM /
// storage moved), and first launch (after the embedded seed).

using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>The result of a catalogue re-assessment.</summary>
/// <param name="Assessed">How many rows were scored.</param>
/// <param name="Compatible">How many of those can run on this device now.</param>
public readonly record struct AssessmentResult(int Assessed, int Compatible);

/// <summary>
/// Re-scores the model catalogue against a device. Writes <c>compatible</c> /
/// <c>rank</c> for every row.
/// </summary>
public interface IModelAssessor
{
    /// <summary>
    /// Assess every row in the catalogue against <paramref name="probe"/>,
    /// writing the <c>compatible</c> bit and the <c>rank</c>. Returns how many
    /// rows were scored and how many are compatible.
    /// </summary>
    AssessmentResult Assess(DeviceProbe probe);
}
