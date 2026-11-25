// CareerArticulator.cs
//
// The wording suggestions on this device, over the same CircleAI.Career script and
// store the interview uses, and the app's one shared brain.
//
// THE SCRIPT AND THE STORE ARE NOT REIMPLEMENTED, for the same reason
// CareerInterviewHost does not reimplement them: the field an answer is about to be
// stored in is read from the store the same way, so a suggestion is always shaped
// for the question actually on screen.

using CircleAI.Career;

namespace CircleAI.Assistant.Device;

/// <inheritdoc />
public sealed class CareerArticulator : ICareerArticulator
{
    private readonly IBrain _brain;

    /// <summary>Takes the app's one brain, the way JobSpecTailor does.</summary>
    /// <remarks>
    /// Not a session of its own: a wording suggestion is a one-shot ask on the same
    /// loaded model the rest of the app shares, not a reason to load and unload one
    /// on the screen where somebody is waiting.
    /// </remarks>
    public CareerArticulator(IBrain brain) => _brain = brain;

    private static string StorePath
        => Path.Combine(AppPaths.Data, "CircleAI", "career.db");

    /// <inheritdoc />
    public async Task<string?> SuggestAsync(string rawAnswer, CancellationToken ct = default)
    {
        var raw = (rawAnswer ?? "").Trim();
        if (raw.Length == 0) return null;

        // WHICH QUESTION ARE WE ON. The current one is the first unanswered, read
        // the same way AnswerAsync reads it - so the suggestion is shaped for the
        // field the answer is about to land in. Off the UI thread: it opens the store.
        var question = await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            using var store = new SqliteCareerStore(StorePath);
            return CareerInterview.Next(store.Load());
        }, ct).ConfigureAwait(false);

        if (question is null || !SkillArticulation.Applies(question.Field)) return null;

        // "Skip" is an answer the interview understands; it is not something to reword.
        if (CareerInterview.IsDecline(raw)) return null;

        try
        {
            var prompt = SkillArticulation.BuildPrompt(question, raw);
            var reply = await _brain.AskAsync(prompt, ct: ct).ConfigureAwait(false);
            return SkillArticulation.Parse(reply, raw);
        }
        catch
        {
            // A BUSY OR MISSING MODEL IS NOT A FAILURE HERE. The person keeps their
            // own words and the interview carries on - the suggestion was only ever
            // an offer.
            return null;
        }
    }
}
