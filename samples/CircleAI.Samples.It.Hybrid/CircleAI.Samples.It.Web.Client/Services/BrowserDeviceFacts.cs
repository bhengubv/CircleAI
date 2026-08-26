// BrowserDeviceFacts.cs
//
// A browser is not a device, and says so.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <summary>
/// The abilities screen in a browser: the list, with no state and no numbers.
/// </summary>
/// <remarks>
/// EVERY VALUE ON THIS SCREEN IS A MEASUREMENT OF A PHONE - free space, memory,
/// which models are on disk, what fits. A browser can answer none of them, and
/// inventing plausible figures would put numbers on screen that nothing checked.
/// So the abilities are listed, because the product does have them, and their
/// state reads NotCatalogued here rather than pretending to a tick.
/// </remarks>
public sealed class BrowserDeviceFacts : IDeviceFacts
{
    private static readonly (string Title, string Blurb)[] Catalogue =
    [
        // Filled in at read time, not in this initialiser - see DeviceFacts.
        ("Talking",   "Reads things out loud, in {n} languages"),
        ("Listening", "Understands you when you speak"),
        ("Answering", "Answers questions and helps you write"),
        ("Seeing",    "Looks at a photo and tells you what is in it"),
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AbilityRow>>(
            Catalogue.Select(a => new AbilityRow(
                a.Title,
                a.Blurb.Replace("{n}", SampleLanguages.All.Count.ToString()),
                AbilityState.NotCatalogued))
                     .ToList());

    /// <inheritdoc />
    public Task<PhoneFacts> PhoneAsync(CancellationToken ct = default)
        => Task.FromResult(new PhoneFacts(
            [new PhoneFact("Where it runs",
                "On the phone. This page is only showing you what it does.")],
            []));

    /// <inheritdoc />
    public Task<string> TurnOnAsync(
        string title, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult("Install the app to turn this on.");
}
