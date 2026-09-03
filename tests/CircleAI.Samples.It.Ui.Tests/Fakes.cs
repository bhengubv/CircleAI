// Fakes.cs
//
// Heads that do nothing, so a component can be asked what it SAYS.
//
// DELIBERATELY NOT MOCKS WITH EXPECTATIONS. Every test here is about what the
// UI claims given what a head reports, so the fakes are plain records of
// answers: set the answer, render, read the markup. A mock framework would put
// a second language between the question and the assertion.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

/// <summary>A voice host that offers exactly the catalogue it was handed.</summary>
internal sealed class FakeVoiceHost : IVoiceHost
{
    public IReadOnlyList<VoiceRow> Catalogue { get; init; } = [];
    public VoiceAvailability Availability { get; init; } = VoiceAvailability.OnDevice;
    public string Provenance => "fake";

    public Task<IReadOnlyList<VoiceRow>> CatalogueAsync(CancellationToken ct = default)
        => Task.FromResult(Catalogue);

    public Task<SpeakOutcome> SpeakAsync(
        string language, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new SpeakOutcome(false, "fake host does not speak"));

    public Task<SpeakOutcome> SayAsync(
        string text, string language, IProgress<string>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(new SpeakOutcome(false, "fake host does not speak"));
}

/// <summary>Setup that is already finished, unless a test says otherwise.</summary>
internal sealed class FakeSetup : ISetup
{
    public Readiness Readiness { get; init; } =
        new(ReadyStage.Ready, "Tap and talk", "", true);

    public bool IsRunning => false;

    public Task<Readiness> ReadinessAsync(CancellationToken ct = default)
        => Task.FromResult(Readiness);

    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SetupItem>>([]);

    /// <summary>A census where everything asked for is already on the device.</summary>
    public Census Census { get; init; } =
        new([new CensusRow("the voice", true, 1024, "here")], 1, 1, "1 of 1 on this phone");

    public Task<Census> CensusAsync(CancellationToken ct = default)
        => Task.FromResult(Census);

    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TourStep>>([]);

    public Task<bool> AllowMicrophoneAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>Settings held in memory, so a test can put the app in Translator mode.</summary>
internal sealed class FakeSettings : ISettings
{
    public AppSettings Settings { get; set; } = new();

    public Task<AppSettings> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredDocument>> DocumentsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoredDocument>>([]);

    public Task DeleteDocumentAsync(string path, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string?> ServiceSettingAsync(
        string service, string key, string? fallback = null, CancellationToken ct = default)
        => Task.FromResult(fallback);

    public Task SetServiceSettingAsync(
        string service, string key, string? value = null, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>A conversation that never starts, so a test drives the mark itself.</summary>
/// <remarks>
/// The tests here are about what the marks DRAW for a given state, not about
/// running a turn - so this reports nothing and the test sets VoiceMark directly.
/// That keeps the assertion on the thing that broke.
/// </remarks>
internal sealed class FakeConversation : IConversation
{
    /// <summary>Whether a turn can run at all - Home greets instead when it cannot.</summary>
    public bool Ready { get; init; }

    public Task<BrainState> StateAsync(CancellationToken ct = default)
        => Task.FromResult(new BrainState(Ready, Ready ? "" : "no brain in a test"));

    /// <summary>What the transcriber will claim to have heard, if anything.</summary>
    public string? Heard { get; init; }

    /// <summary>True once a turn was cancelled - the routed case.</summary>
    public bool WasCancelled { get; private set; }

    public async Task TurnAsync(IProgress<TurnState> updates, CancellationToken ct = default)
    {
        updates.Report(new TurnState(TurnPhase.Listening));

        if (Heard is { Length: > 0 })
            updates.Report(new TurnState(TurnPhase.Thinking, Heard: Heard));

        // The real turn goes on to answer here. A routed turn is cancelled during
        // the report above, so this is where that shows up.
        try { await Task.Delay(20, ct); }
        catch (OperationCanceledException) { WasCancelled = true; throw; }

        updates.Report(new TurnState(TurnPhase.Idle));
    }

    public Task<string?> DictateAsync(
        IProgress<TurnState> updates, CancellationToken ct = default, string? language = null)
        => Task.FromResult<string?>(null);

    public Task SayAsync(string text, string? language = null, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>How many times the app asked for a warm-up.</summary>
    public int Prepared { get; private set; }

    public Task<string> PrepareAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Prepared++;
        progress?.Report("Opening the ears");
        return Task.FromResult("ears: open; voice: ready");
    }

    public Task HeardAsync(string heard, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string?> SeeAsync(
        string prompt, byte[] image, Action<string>? onToken = null, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

/// <summary>Nothing was shared into the app.</summary>
internal sealed class FakeShareTarget : IShareTarget
{
    public bool HasPending => false;
    public string? Take() => null;
}

/// <summary>A resident assistant that is switched off and stays off.</summary>
internal sealed class FakeResidentAssistant : IResidentAssistant
{
    public bool IsListening => false;

    public event EventHandler<string>? Woke;

    /// <summary>Lets a test raise a wake the way the service would.</summary>
    public void RaiseWoke(string phrase) => Woke?.Invoke(this, phrase);

    private static ResidentStatus Off =>
        new(ResidentState.Off, "Not listening", "");

    public Task<ResidentStatus> StartAsync(CancellationToken ct = default) => Task.FromResult(Off);
    public Task<ResidentStatus> StopAsync(CancellationToken ct = default) => Task.FromResult(Off);
    public Task<ResidentStatus> ResumeAsync(CancellationToken ct = default) => Task.FromResult(Off);
}

/// <summary>A phone that says where it is, so a pair can be tested.</summary>
internal sealed class FakeWhereAmI : IWhereAmI
{
    public Whereabouts Whereabouts { get; init; } =
        new("ZA", CountrySource.Locale, "ZA");

    public Whereabouts Locate() => Whereabouts;
}

/// <summary>A spoken language that answers whatever the test set.</summary>
internal sealed class FakeSpokenLanguage : ISpokenLanguage
{
    public string Current { get; set; } = "en";
    public IReadOnlyList<string> Suggested => [Current];
    public string? Chosen { get; private set; }
    public void Choose(string tag) { Chosen = tag; Current = tag; }
    public void ClearChoice() => Chosen = null;
}

/// <summary>A brain that answers with whatever the test decided.</summary>
/// <remarks>
/// The interpreter asks it to translate, so the "translation" a test asserts on
/// is simply what this returns. That keeps the assertions about the SCREEN -
/// which side a line lands on, what happens when it fails - rather than about a
/// model nobody can pin.
/// </remarks>
internal sealed class FakeBrain : IBrain
{
    public string Answer { get; init; } = "translated";
    public bool Ready { get; init; } = true;
    public Exception? Throws { get; init; }

    public Task<BrainState> StateAsync(CancellationToken ct = default)
        => Task.FromResult(new BrainState(Ready, Ready ? "" : "no brain in a test"));

    public Task<string> AskAsync(
        string prompt, Action<string>? token = null, CancellationToken ct = default)
        => Throws is not null ? Task.FromException<string>(Throws) : Task.FromResult(Answer);

    public Task<string> SeeAsync(
        string question, byte[] image,
        Action<string>? token = null, CancellationToken ct = default)
        => Task.FromResult(Answer);
}

/// <summary>A phone, as far as any screen can tell.</summary>
internal sealed class FakeFormFactor : IFormFactor
{
    public string GetFormFactor() => "Phone";
    public string GetPlatform() => "Test";
    public bool IsOnDevice { get; init; } = true;
}
