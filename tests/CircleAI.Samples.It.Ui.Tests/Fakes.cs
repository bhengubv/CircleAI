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

    /// <summary>How many times the exemption screen was asked for.</summary>
    public int BackgroundAsks { get; private set; }

    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default)
    {
        BackgroundAsks++;

        // Granting is what the real one CANNOT do - it opens a system screen and
        // the person decides. The fake models that: asking makes it allowed, so
        // a test can check the prompt disappears once the phone says yes.
        BackgroundAllowed = true;
        return Task.FromResult(true);
    }

    /// <summary>Whether this phone will let the assistant keep running.</summary>
    /// <remarks>
    /// TRUE BY DEFAULT, so the exemption warning is absent from every test that
    /// is not about it. A prompt that appeared everywhere would be asserted
    /// around rather than asserted on.
    /// </remarks>
    public bool BackgroundAllowed { get; set; } = true;

    public Task<bool> BackgroundAllowedAsync(CancellationToken ct = default)
        => Task.FromResult(BackgroundAllowed);
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

    /// <summary>How many turns have been taken.</summary>
    /// <remarks>
    /// Counted because a wake now opens a CONVERSATION rather than buying one
    /// turn, and "did it carry on listening" is a question about how many times
    /// this was called.
    /// </remarks>
    public int Turns { get; private set; }

    /// <summary>Turns after which the fake stops hearing anything.</summary>
    /// <remarks>
    /// A conversation ends when somebody stops talking, so a fake that always
    /// hears something can only ever test the ceiling. This is how a test says
    /// "they said two things and then stopped".
    /// </remarks>
    public int HearsForTurns { get; init; } = int.MaxValue;

    public async Task TurnAsync(IProgress<TurnState> updates, CancellationToken ct = default)
    {
        var hearsThisTurn = Turns < HearsForTurns;
        Turns++;

        updates.Report(new TurnState(TurnPhase.Listening));

        if (!hearsThisTurn)
        {
            updates.Report(new TurnState(TurnPhase.Idle, Detail: "I did not catch that."));
            return;
        }


        if (Heard is { Length: > 0 })
            updates.Report(new TurnState(TurnPhase.Thinking, Heard: Heard));

        // The real turn goes on to answer here. A routed turn is cancelled during
        // the report above, so this is where that shows up.
        //
        // LONG ENOUGH FOR THE ROUTER TO GET A WORD IN, and that is not a fudge.
        // Progress<T>.Report MARSHALS to the renderer's dispatcher - it does not
        // run the handler inline - so the Observe call that cancels this turn
        // happens on a later tick. At 20ms the delay could finish first, the
        // turn completed normally, and WasCancelled stayed false FOREVER: a race
        // no timeout can rescue, which is why raising the wait from 1s to 5s to
        // 15s kept failing at whatever number it was given.
        //
        // A real turn spends seconds in the model after reporting what it heard,
        // so a fake that reports and finishes in the same breath is not a faster
        // version of the product - it is a different one. Only the routed case
        // needs the room; a silent turn stays quick so the rest of the suite
        // does not pay for this.
        // 250ms, not 1000. A second was long enough to close the race and long
        // enough to open another one: the tests that wait for a turn to FINISH
        // do so inside bunit's one-second default, so a turn that took exactly
        // that long started failing them instead. Far more than a dispatcher
        // tick, comfortably less than the window anything waits in.
        var thinking = Heard is { Length: > 0 } ? 250 : 20;

        try { await Task.Delay(thinking, ct); }
        catch (OperationCanceledException) { WasCancelled = true; throw; }

        updates.Report(new TurnState(TurnPhase.Idle));
    }

    public Task<string?> DictateAsync(
        IProgress<TurnState> updates, CancellationToken ct = default, string? language = null)
        => Task.FromResult<string?>(null);

    /// <summary>What a session should hand back, and what it was asked for.</summary>
    /// <remarks>
    /// The silence window is recorded because it is the one setting the screen
    /// has to choose deliberately - a meeting and a question want different
    /// numbers, and a screen that passed the default for both would be wrong for
    /// one of them.
    /// </remarks>
    public string SessionText { get; init; } = "";
    public double? SessionSilenceMs { get; private set; }
    public int Sessions { get; private set; }

    public async Task<string> SessionAsync(
        IProgress<TurnState> updates, CancellationToken ct = default,
        string? language = null, double silenceMs = 5000)
    {
        Sessions++;
        SessionSilenceMs = silenceMs;

        updates.Report(new TurnState(TurnPhase.Listening, Language: language));

        // Runs until stopped, the way a real one does.
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { WasCancelled = true; }

        updates.Report(new TurnState(TurnPhase.Idle, Heard: SessionText, Language: language));
        return SessionText;
    }

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
    /// <summary>Whether it is holding the microphone, and whether Start works.</summary>
    public bool IsListening { get; private set; }
    public bool StartSucceeds { get; init; } = true;
    public int Starts { get; private set; }

    public event EventHandler<string>? Woke;

    /// <summary>Lets a test raise a wake the way the service would.</summary>
    public void RaiseWoke(string phrase) => Woke?.Invoke(this, phrase);

    private static ResidentStatus Off =>
        new(ResidentState.Off, "Not listening", "");

    public Task<ResidentStatus> StartAsync(CancellationToken ct = default)
    {
        Starts++;
        IsListening = StartSucceeds;
        return Task.FromResult(StartSucceeds
            ? new ResidentStatus(ResidentState.Listening, "Listening", "")
            : new ResidentStatus(ResidentState.Failed, "Not listening",
                "This phone stops it in the background. Allow it there."));
    }
    /// <summary>Stops, and actually stops.</summary>
    /// <remarks>
    /// IT RETURNED THE OFF STATUS AND LEFT IsListening TRUE. Start set the flag
    /// and Stop did not clear it, so this fake could only ever go one way -
    /// and any test that switched the listener off was quietly asserting
    /// against a listener that was still on. It made a real staleness bug in
    /// Settings look like a broken test.
    /// </remarks>
    public Task<ResidentStatus> StopAsync(CancellationToken ct = default)
    {
        IsListening = false;
        return Task.FromResult(Off);
    }

    public Task<ResidentStatus> ResumeAsync(CancellationToken ct = default) => Task.FromResult(Off);

    /// <summary>How many times the listener was asked to catch up.</summary>
    /// <remarks>
    /// COUNTED RATHER THAN IGNORED, because the bug this exists for is a call
    /// that never happened: choosing a phrase wrote the settings table and the
    /// keywords file and told the listener nothing, so the microphone went on
    /// hearing the old name. A fake that silently returned would let that come
    /// back.
    /// </remarks>
    public int Refreshes { get; private set; }

    public Task<ResidentStatus> RefreshAsync(CancellationToken ct = default)
    {
        Refreshes++;
        return Task.FromResult(IsListening
            ? new ResidentStatus(ResidentState.Listening, "Listening", "")
            : Off);
    }
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
