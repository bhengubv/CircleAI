// DeviceProfile.cs
//
// The profile, over the career store that already holds it.
//
// NOT A SECOND COPY. SqliteCareerStore already keeps the name, phone, location,
// headline and summary the CV interview gathers; a separate profile table would
// be two places for one truth, and the one somebody edits would not be the one
// the CV renders from.

using CircleAI.Career;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceProfile : IProfile
{
    private static string StorePath
        => Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "career.db");

    private static SqliteCareerStore Store()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        return new SqliteCareerStore(StorePath);
    }

    /// <inheritdoc />
    public Task<Profile> LoadAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var store = Store();
            var p = store.Load();
            var id = p.Identity;

            var facts = new List<ProfileFact>
            {
                // SHORT ENOUGH TO FIT THE BOX. These read as full sentences -
                // "So it knows what to call you", "A sentence or two in your own
                // words" - and on a 344px screen, beside their label, they were
                // clipped mid-word. A hint cut off halfway looks like a rendering
                // fault, and a person cannot act on the half they can see.
                new("name", "Your name", id.FullName ?? "", "What to call you"),
                new("headline", "What you do", id.Headline ?? "", "Driver, teacher, looking"),
                new("location", "Where you live", id.Location ?? "", "Your town"),
                new("phone", "Phone number", id.Phone ?? "", "For documents only"),
                new("summary", "About you", id.Summary ?? "",
                    "In your own words", Multiline: true),
            };

            return new Profile(facts, Sections(p), p.Completeness(), Missing(p));
        }, ct);

    /// <summary>
    /// The lists the store holds, as the screen shows them.
    /// </summary>
    /// <remarks>
    /// ALL FIVE, because the completeness figure counts all five. Showing the
    /// identity fields alone put a bar on screen that could not pass 50% however
    /// carefully somebody filled the form in - work history alone is worth 4 of
    /// the 18 points, more than the name.
    /// <para>
    /// The empty lines say what would go here and why, not "nothing yet". A
    /// person who does not know that a driver's licence code decides applications
    /// outright has no reason to go and add one.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ProfileSection> Sections(CareerProfile p) =>
    [
        new("history", "Work", p.History
            .Select(h => new ProfileEntry(h.Id, h.Role, Where(h)))
            .ToList(),
            "Where you have worked - counts for more than anything else here, and "
            + "piece work and informal work count."),

        new("skill", "Skills", p.Skills
            .Select(s => new ProfileEntry(s.Id, s.Name,
                s.Years is { } y ? $"{y:0.#} years" : ""))
            .ToList(),
            "What you can actually do. Tied to the job you did it in, it can be "
            + "defended in an interview."),

        new("education", "Education", p.Education
            .Select(e => new ProfileEntry(e.Id, e.Qualification,
                Join(e.Institution, e.Year, e.Completed ? null : "not finished")))
            .ToList(),
            "School, college or a course - finished or not."),

        new("certification", "Certificates", p.Certifications
            .Select(c => new ProfileEntry(c.Id, c.Name,
                Join(c.Issuer, c.Year, c.Expires is { } x ? $"expires {x}" : null)))
            .ToList(),
            "A licence code, a PSIRA grade, a first-aid ticket. These decide "
            + "applications outright."),

        new("language", "Languages", p.Languages
            .Select(l => new ProfileEntry(l.Id, l.Name, l.Level ?? ""))
            .ToList(),
            "Serving customers in isiZulu or Sesotho is a qualification, not a "
            + "footnote."),
    ];

    /// <summary>The absent thing worth most, in plain words.</summary>
    /// <remarks>
    /// IN THE SAME ORDER THE SCORE WEIGHS THEM, so the sentence and the bar
    /// beside it cannot disagree. A bar at 40% with no hint of what would move it
    /// is a nag; naming the one thing worth most makes it an instruction.
    /// </remarks>
    private static string Missing(CareerProfile p)
    {
        var id = p.Identity;
        if (string.IsNullOrWhiteSpace(id.FullName)) return "Start with your name.";
        if (string.IsNullOrWhiteSpace(id.Phone)) return "A phone number matters most next — nothing can reach you without it.";
        if (p.History.Count == 0) return "Where you have worked counts for more than anything else here.";
        if (string.IsNullOrWhiteSpace(id.Headline)) return "Say what you do, in a few words.";
        if (p.Skills.Count == 0) return "Add what you can do.";
        if (string.IsNullOrWhiteSpace(id.Location)) return "Where you live — the town is enough.";
        if (p.Education.Count == 0) return "Add school or a course.";
        if (p.Certifications.Count == 0) return "A licence or a certificate, if you have one.";
        if (p.Languages.Count == 0) return "Add the languages you speak.";
        return "";
    }

    /// <summary>Where and when a job was, on one line.</summary>
    private static string Where(ProfileHistory h)
    {
        var when = h.Start is null && h.End is null
            ? null
            : $"{h.Start}{(h.End is null ? " – now" : $" – {h.End}")}".Trim();

        // "Informal" is recorded because it changes how a CV should be written,
        // not because it counts for less - so it is stated plainly rather than
        // hidden or apologised for.
        return Join(h.Organisation, when, h.Formal ? null : "informal");
    }

    /// <summary>The parts that exist, separated by a dot.</summary>
    private static string Join(params string?[] parts)
        => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <inheritdoc />
    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var store = Store();
            var id = store.Load().Identity;

            store.SaveIdentity(key switch
            {
                "name" => id with { FullName = value },
                "headline" => id with { Headline = value },
                "location" => id with { Location = value },
                "phone" => id with { Phone = value },
                "summary" => id with { Summary = value },
                _ => id,
            });
        }, ct);

    /// <inheritdoc />
    public Task RemoveAsync(string section, long id, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var store = Store();

            // The section key IS the table name, which is why ProfileSection
            // carries it - the alternative is a mapping in the UI that has to be
            // kept in step with a schema it cannot see. The store refuses any
            // table that is not one of the five removable ones.
            store.Remove(section, id);
        }, ct);

    /// <inheritdoc />
    public Task ForgetAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            // The whole database, not the identity row. Work history, skills and
            // education are as personal as a name, and a "forget me" that leaves
            // somebody's employment record behind is not one.
            try { if (File.Exists(StorePath)) File.Delete(StorePath); }
            catch { /* a file that will not delete is not worth a crash */ }
        }, ct);
}
