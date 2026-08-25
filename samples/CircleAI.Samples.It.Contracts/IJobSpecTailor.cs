// IJobSpecTailor.cs
//
// Rearranging a CV to face a particular advert.

namespace CircleAI.Samples.It;

/// <summary>What the tailoring produced, for the screen to show.</summary>
/// <param name="Ok">Whether it ran.</param>
/// <param name="Text">
/// The reasoning and the result, in the person's own interest. THEY ARE ABOUT TO
/// PUT THEIR NAME ON IT, so what changed and why is shown to them rather than
/// logged for us.
/// </param>
public sealed record TailorResult(bool Ok, string Text);

/// <summary>Aims an existing CV at a job advert.</summary>
/// <remarks>
/// NOTHING IS INVENTED. The model is asked which of the facts already in the
/// profile to lead with - it chooses ids, not words - so the output can only ever
/// be a reordering of what somebody actually said about themselves. A CV that
/// improves itself is a CV that lies in an interview.
/// </remarks>
public interface IJobSpecTailor
{
    /// <summary>Read the advert and rearrange the CV behind it.</summary>
    /// <param name="advert">The pasted or shared job advert.</param>
    /// <param name="progress">Called with what it is doing, for the button.</param>
    Task<TailorResult> TailorAsync(
        string advert, IProgress<string>? progress = null, CancellationToken ct = default);
}
