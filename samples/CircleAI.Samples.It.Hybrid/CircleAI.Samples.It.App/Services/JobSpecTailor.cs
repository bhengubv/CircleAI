// JobSpecTailor.cs
//
// Aims the stored CV at a pasted advert, using the shared brain.

using CircleAI.Career;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class JobSpecTailor : IJobSpecTailor
{
    private readonly IBrain _brain;

    /// <summary>Takes the app's one brain rather than loading its own.</summary>
    /// <remarks>
    /// The native screen used to build a session per advert and dispose it
    /// afterwards - a full model load and unload on the screen where somebody is
    /// waiting to see their CV rearranged.
    /// </remarks>
    public JobSpecTailor(IBrain brain) => _brain = brain;

    private static string StorePath
        => Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "career.db");

    /// <inheritdoc />
    public async Task<TailorResult> TailorAsync(
        string advert, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var text = advert.Trim();
        if (text.Length < 20)
            return new TailorResult(false, "Paste a bit more of the advert.");

        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        using var store = new SqliteCareerStore(StorePath);
        var profile = store.Load();

        if (profile.History.Count == 0)
        {
            return new TailorResult(false,
                "Tell me about your work first — then I can aim it at a job.");
        }

        try
        {
            progress?.Report("Reading the advert…");

            // The advert's first line is the title unless it says otherwise -
            // enough to tell two applications apart in the list afterwards.
            var title = text.Split('\n')[0].Trim();
            if (title.Length > 60) title = title[..60];

            var spec = new JobSpec(title, null, text, Source: "typed");
            var specId = store.AddSpec(spec);

            var prompt = ProfileTailoring.BuildPrompt(profile, spec with { Id = specId });
            var answer = await _brain.AskAsync(prompt, ct: ct).ConfigureAwait(false);

            var choice = ProfileTailoring.Parse(answer, profile);
            var cv = ProfileToCv.Render(profile,
                ProfileTailoring.SelectedFacts(choice).ToHashSet());

            // WHAT CHANGED AND WHY, in the person's own interest. They are about to
            // put their name on it, so the reasoning is shown to them rather than
            // logged for us.
            var lines = new List<string>
            {
                choice.Reasoning,
                "",
                $"Leading with: {string.Join(", ", cv.Experience.Select(e => e.Title))}",
                $"Skills shown: {string.Join(", ", cv.Skills)}",
                "",
                "Nothing here was invented — every line came from what you told me.",
            };
            return new TailorResult(true, string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            return new TailorResult(false, $"Could not read that advert. ({ex.GetType().Name})");
        }
    }
}
