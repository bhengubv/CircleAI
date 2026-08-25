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
                new("name", "Your name", id.FullName ?? "", "So it knows what to call you"),
                new("headline", "What you do", id.Headline ?? "", "Driver, teacher, still looking"),
                new("location", "Where you live", id.Location ?? "", "The town is enough"),
                new("phone", "Phone number", id.Phone ?? "", "Only used on documents you make"),
                new("summary", "About you", id.Summary ?? "",
                    "A sentence or two in your own words", Multiline: true),
            };

            return new Profile(facts, p.Completeness());
        }, ct);

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
