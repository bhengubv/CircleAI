// CareerInterviewHost.cs
//
// The CV interview on this device, over the same CircleAI.Career engine the
// native screen uses.
//
// THE SCRIPT AND THE STORE ARE NOT REIMPLEMENTED. CareerInterview decides what to
// ask next and SqliteCareerStore keeps the answers; this class only maps an answer
// onto the right field and turns the rendered document into lines a page can lay
// out. Rewriting the question order here would give two apps two interviews.

using CircleAI.Career;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class CareerInterviewHost : ICareerInterview
{
    private static string StorePath
        => Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "career.db");

    private SqliteCareerStore Store()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        return new SqliteCareerStore(StorePath);
    }

    /// <inheritdoc />
    public Task<CareerStep> StepAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var store = Store();
            var q = CareerInterview.Next(store.Load());

            // OUT OF QUESTIONS IS NOT THE END OF THE SCREEN. The document is the
            // point, so the closing lines keep the person on it.
            return q is null
                ? new CareerStep(
                    "That is everything I need",
                    "Your CV is below. You can change anything, or aim it at a job.",
                    Verify: false, Done: true)
                : new CareerStep(q.Ask, q.Why, q.Verify, Done: false);
        }, ct);

    /// <inheritdoc />
    public Task AnswerAsync(string text, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var store = Store();
            var profile = store.Load();
            var q = CareerInterview.Next(profile);
            if (q is null) return;

            // A DECLINE STILL ADVANCES. Somebody who will not answer must not be
            // asked the same thing forever.
            if (CareerInterview.IsDecline(text)) return;

            var id = profile.Identity;
            switch (q.Field)
            {
                case ProfileField.FullName:
                    store.SaveIdentity(id with { FullName = text }); break;
                case ProfileField.Phone:
                    store.SaveIdentity(id with { Phone = text }); break;
                case ProfileField.Headline:
                    store.SaveIdentity(id with { Headline = text }); break;
                case ProfileField.Location:
                    store.SaveIdentity(id with { Location = text }); break;
                case ProfileField.Summary:
                    store.SaveIdentity(id with { Summary = text }); break;

                case ProfileField.WorkRole:
                    store.AddHistory(new ProfileHistory(text)); break;
                case ProfileField.WorkOrganisation:
                    ReplaceLatest(store, h => h with { Organisation = text, Formal = true }); break;
                case ProfileField.WorkWhen:
                    // Kept as they said it. "About two years, until last winter" is
                    // more honest on a CV than a date this app invented.
                    ReplaceLatest(store, h => h with { Start = text }); break;
                case ProfileField.WorkDid:
                    ReplaceLatest(store, h => h with { Achievements = SplitList(text) }); break;

                case ProfileField.Skills:
                    foreach (var s in SplitList(text)) store.AddSkill(new ProfileSkill(s));
                    break;
                case ProfileField.Certification:
                    foreach (var c in SplitList(text)) store.AddCertification(new ProfileCertification(c));
                    break;
                case ProfileField.Education:
                    store.AddEducation(new ProfileEducation(text)); break;
                case ProfileField.Languages:
                    foreach (var l in SplitList(text)) store.AddLanguage(new ProfileLanguage(l));
                    break;
            }
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<CvLine>> PreviewAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<CvLine>>(() =>
        {
            using var store = Store();
            var cv = ProfileToCv.Render(store.Load());
            var lines = new List<CvLine>();

            void Add(string? text, CvLineKind kind)
            {
                if (!string.IsNullOrWhiteSpace(text)) lines.Add(new CvLine(text!, kind));
            }
            void Gap() => lines.Add(new CvLine("", CvLineKind.Gap));

            Add(cv.FullName, CvLineKind.Name);
            Add(cv.Headline, CvLineKind.Headline);

            var contact = new[] { cv.Contact.Phone, cv.Contact.Email, cv.Contact.Location }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (contact.Count > 0) Add(string.Join("  ·  ", contact), CvLineKind.Small);

            if (!string.IsNullOrWhiteSpace(cv.Summary)) { Gap(); Add(cv.Summary, CvLineKind.Body); }

            if (cv.Experience.Count > 0)
            {
                Gap(); Add("WORK", CvLineKind.Section);
                foreach (var e in cv.Experience)
                {
                    var where = string.IsNullOrWhiteSpace(e.Organisation) ? "" : $" — {e.Organisation}";
                    var when = string.IsNullOrWhiteSpace(e.StartDate)
                        ? ""
                        : $"  ({e.StartDate}{(e.EndDate is null ? " – present" : " – " + e.EndDate)})";
                    Add(e.Title + where + when, CvLineKind.Entry);
                    foreach (var h in e.Highlights) Add("•  " + h, CvLineKind.Small);
                }
            }

            if (cv.Skills.Count > 0)
            {
                Gap(); Add("SKILLS", CvLineKind.Section);
                Add(string.Join("  ·  ", cv.Skills), CvLineKind.Small);
            }

            if (cv.Certifications is { Count: > 0 })
            {
                Gap(); Add("CERTIFICATES", CvLineKind.Section);
                foreach (var c in cv.Certifications) Add("•  " + c.Name, CvLineKind.Small);
            }

            if (cv.Education.Count > 0)
            {
                Gap(); Add("EDUCATION", CvLineKind.Section);
                foreach (var e in cv.Education)
                    Add($"{e.Qualification}"
                      + $"{(string.IsNullOrWhiteSpace(e.Institution) ? "" : " — " + e.Institution)}",
                        CvLineKind.Small);
            }

            return lines;
        }, ct);

    /// <inheritdoc />
    public Task<string> ProgressAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var store = Store();
            return $"Your CV is {store.Load().Completeness() * 100:0}% there";
        }, ct);

    /// <inheritdoc />
    public Task<string> SaveAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var store = Store();
            var cv = ProfileToCv.Render(store.Load());
            var name = string.IsNullOrWhiteSpace(cv.FullName) ? "cv" : cv.FullName.Replace(' ', '-');

            // Plain text, into the app's own documents folder. A CV somebody
            // cannot open is not a CV; text opens everywhere, including on a
            // phone with no office app installed.
            var path = Path.Combine(FileSystem.AppDataDirectory, $"{name}.txt");
            var body = string.Join(Environment.NewLine,
                PreviewAsync(ct).GetAwaiter().GetResult()
                    .Select(l => l.Kind == CvLineKind.Gap ? "" : l.Text));
            File.WriteAllText(path, body);
            return $"Saved to {path}";
        }, ct);

    /// <summary>
    /// Rewrites the newest job.
    /// </summary>
    /// <remarks>
    /// The work questions build ONE row across four answers, so each has to update
    /// rather than insert - otherwise a single job becomes four half-empty ones.
    /// </remarks>
    private static void ReplaceLatest(SqliteCareerStore store, Func<ProfileHistory, ProfileHistory> edit)
    {
        var latest = store.Load().History.FirstOrDefault();
        if (latest is null) { store.AddHistory(edit(new ProfileHistory("Work"))); return; }

        store.Remove("history", latest.Id);
        store.AddHistory(edit(latest));
    }

    /// <summary>Splits "a, b and c" the way somebody actually speaks a list.</summary>
    private static IReadOnlyList<string> SplitList(string text)
        => text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)
               .SelectMany(p => p.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
               .Select(p => p.Trim())
               .Where(p => p.Length > 1)
               .ToList();
}
