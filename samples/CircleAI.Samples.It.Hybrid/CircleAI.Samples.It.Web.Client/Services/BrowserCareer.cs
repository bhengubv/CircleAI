// BrowserCareer.cs
//
// The CV lives on the device with its SQLite store.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
/// <remarks>
/// A CV IS THE MOST PERSONAL THING THIS APP HOLDS - a name, a phone number, an
/// employment history. It stays in the on-device store; a browser build keeps no
/// copy and offers no interview, rather than collecting those answers somewhere
/// the promise does not cover.
/// </remarks>
public sealed class BrowserCareer : ICareerInterview
{
    private const string OnPhone =
        "Your CV is built on the phone, where it stays. Install the app to start.";

    /// <inheritdoc />
    public Task<CareerStep> StepAsync(CancellationToken ct = default)
        => Task.FromResult(new CareerStep("Your CV is built on the phone", OnPhone,
            Verify: false, Done: true));

    /// <inheritdoc />
    public Task AnswerAsync(string text, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<CvLine>> PreviewAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CvLine>>([]);

    /// <inheritdoc />
    public Task<string> ProgressAsync(CancellationToken ct = default)
        => Task.FromResult(OnPhone);

    /// <inheritdoc />
    public Task<string> SaveAsync(CancellationToken ct = default) => Task.FromResult(OnPhone);
}

/// <inheritdoc />
public sealed class BrowserTailor : IJobSpecTailor
{
    /// <inheritdoc />
    public Task<TailorResult> TailorAsync(
        string advert, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new TailorResult(false,
            "Aiming your CV at a job needs the model on the phone."));
}
