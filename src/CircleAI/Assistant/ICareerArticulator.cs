// ICareerArticulator.cs
//
// Offering a CV-ready wording of an answer, for the person to confirm.

namespace CircleAI.Assistant;

/// <summary>
/// Suggests a clearer wording for the answer to the CURRENT interview question.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE INTERVIEW ON PURPOSE, exactly as <see cref="IJobSpecTailor"/>
/// is. Running the script and keeping the answers needs no model; proposing better
/// words for an answer does, and the two have different failure modes - a phone
/// with no model still runs the whole interview, and simply offers no wording help.
/// </remarks>
public interface ICareerArticulator
{
    /// <summary>
    /// A clearer, CV-ready wording of <paramref name="rawAnswer"/> for the question
    /// being asked now, or null to keep the person's own words.
    /// </summary>
    /// <remarks>
    /// Null is the honest default: the question is not one where wording is the work,
    /// the model is busy or absent, or it returned nothing better. A suggestion is
    /// only ever a PROPOSAL - the person confirms it before it is stored.
    /// </remarks>
    Task<string?> SuggestAsync(string rawAnswer, CancellationToken ct = default);
}
