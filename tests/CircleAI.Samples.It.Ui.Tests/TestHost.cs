// TestHost.cs
//
// Every service every screen injects, in one place.
//
// SO THAT A SCREEN CANNOT BE ADDED WITHOUT SOMEBODY NOTICING WHAT IT NEEDS.
// Settings.razor injected IResidentAssistant and the web head never registered
// it, so that page threw rather than rendered on one of the three heads - found
// by hand, months later, by grepping. A container that has to hold everything is
// where that becomes a compile-or-fail-fast problem instead.

using Bunit;
using CircleAI.Samples.It;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

internal sealed class FakeCareer : ICareerInterview
{
    public Task<CareerStep> StepAsync(CancellationToken ct = default)
        => Task.FromResult(new CareerStep("What do you do?", "so it can help", false, false));
    public Task AnswerAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<CvLine>> PreviewAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CvLine>>([]);
    public Task<string> ProgressAsync(CancellationToken ct = default) => Task.FromResult("");
    public Task<string> SaveAsync(CancellationToken ct = default) => Task.FromResult("");
}

internal sealed class FakeTailor : IJobSpecTailor
{
    public Task<TailorResult> TailorAsync(
        string advert, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new TailorResult(false, "no model in a test"));
}

internal sealed class FakeProfile : IProfile
{
    public Task<Profile> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(new Profile([], [], 0, "nothing yet"));
    public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(string section, long id, CancellationToken ct = default) => Task.CompletedTask;
    public Task ForgetAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeDeviceFacts : IDeviceFacts
{
    public Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AbilityRow>>([]);
    public Task<PhoneFacts> PhoneAsync(CancellationToken ct = default)
        => Task.FromResult(new PhoneFacts([], []));
    public Task<string> TurnOnAsync(
        string title, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult("nothing to turn on in a test");
}

internal sealed class FakeWakePhrases : IWakePhrases
{
    public Task<IReadOnlyList<WakePhraseOption>> ForAsync(string language, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WakePhraseOption>>([]);
    public Task<WakePhraseResult> CheckAsync(string language, string phrase, CancellationToken ct = default)
        => Task.FromResult(new WakePhraseResult(false, WakePhraseQuality.Unusable, "test"));
    public Task<WakePhraseResult> AddAsync(string language, string phrase, CancellationToken ct = default)
        => Task.FromResult(new WakePhraseResult(false, WakePhraseQuality.Unusable, "test"));
    public Task ChooseAsync(string language, string phrase, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(string language, string phrase, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeWakeWord : IWakeWord
{
    public Task ListenAsync(IProgress<WakeStatus> updates, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> RequestMicrophoneAsync() => Task.FromResult(true);
}

/// <summary>A container holding every service the shared UI can ask for.</summary>
internal static class TestHost
{
    /// <summary>
    /// Register everything, so a page can be rendered without knowing what it needs.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY EVERYTHING, not the minimum each test needs. A page that
    /// starts injecting something new should fail here loudly and once, rather
    /// than in whichever unrelated test happened to render it.
    /// </remarks>
    public static void WireEverything(this TestContext ctx)
    {
        var s = ctx.Services;
        s.AddSingleton(new VoiceMark());
        s.AddSingleton<IConversation>(new FakeConversation());
        s.AddSingleton<IShareTarget>(new FakeShareTarget());
        s.AddSingleton<IResidentAssistant>(new FakeResidentAssistant());
        s.AddSingleton<IVoiceHost>(new FakeVoiceHost
        {
            Catalogue = [new VoiceRow("en", 1), new VoiceRow("ja", 1), new VoiceRow("zu", 1)],
        });
        s.AddSingleton<ISetup>(new FakeSetup());
        s.AddSingleton<ISettings>(new FakeSettings());
        s.AddSingleton<ISpokenLanguage>(new FakeSpokenLanguage());
        s.AddSingleton<IWhereAmI>(new FakeWhereAmI());
        s.AddSingleton<IWiringProbe>(new BrowserWiringProbeStub());
        s.AddSingleton<IBrain>(new FakeBrain());
        s.AddSingleton<IFormFactor>(new FakeFormFactor());
        s.AddSingleton<ICareerInterview>(new FakeCareer());
        s.AddSingleton<IJobSpecTailor>(new FakeTailor());
        s.AddSingleton<IProfile>(new FakeProfile());
        s.AddSingleton<IDeviceFacts>(new FakeDeviceFacts());
        s.AddSingleton<IWakePhrases>(new FakeWakePhrases());
        s.AddSingleton<IWakeWord>(new FakeWakeWord());
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }
}
