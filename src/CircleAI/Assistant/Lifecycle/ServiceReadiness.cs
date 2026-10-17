// ServiceReadiness.cs
//
// "CONNECTED", "MODEL INSTALLED", "LOADING" AND "READY TO ANSWER" ARE FOUR STATES.
//
// The service used to have one static - Idle, Loading, Ready, Failed - and it lied in
// both directions. A second OnStartCommand published Ready while the first was still
// inside the load; a managed load failure left the node set, so the next start
// published Ready over a brain that would never answer; Idle meant stopped, no brain
// asked for, no model, and every model refused, all at once. And it never crossed the
// link: SetupReadiness read files on disk and said "Tap and talk" while the brain was
// loading, had failed, or refused everything it held.
//
// THIS IS THE SHAPE THE SERVICE REPORTS, to every app, in one verb. Connected is not
// here because it is the client's to know: an app that can read this is connected.

using System;
using System.Collections.Generic;

namespace CircleAI.Assistant.Lifecycle;

/// <summary>Where a component has got to. Appended, never renumbered.</summary>
public enum BrainPhase
{
    /// <summary>The service could not say (an older service, or nothing reported yet).</summary>
    Unknown = 0,

    /// <summary>No usable model is on this device.</summary>
    NoModel = 1,

    /// <summary>A usable model is on the device and not loaded.</summary>
    Installed = 2,

    /// <summary>Being loaded; <see cref="ComponentState.Step"/> says which part.</summary>
    Loading = 3,

    /// <summary>Loaded, warmed, and answering.</summary>
    Ready = 4,

    /// <summary>It tried and could not; <see cref="ComponentState.Kind"/> says why.</summary>
    Failed = 5,

    /// <summary>Android stopped the work from continuing. The work is kept and resumes.</summary>
    Paused = 6,
}

/// <summary>Which part of a load is running.</summary>
public enum LoadStep
{
    /// <summary>Not loading.</summary>
    None = 0,

    /// <summary>Waiting for something before it can start (the catalogue, memory, the worker).</summary>
    Waiting = 1,

    /// <summary>Checking the files.</summary>
    Verifying = 2,

    /// <summary>Reading the weights.</summary>
    LoadingWeights = 3,

    /// <summary>The first prefill.</summary>
    Warming = 4,
}

/// <summary>Why work is paused rather than failed.</summary>
public enum PauseReason
{
    /// <summary>Not paused.</summary>
    None = 0,

    /// <summary>A platform time budget ran out; it resumes on the next allowed start.</summary>
    AndroidTimeLimit = 1,

    /// <summary>Android would not let the service start in the foreground just now.</summary>
    StartNotAllowed = 2,

    /// <summary>Not enough free memory right now.</summary>
    WaitingForMemory = 3,

    /// <summary>A damaged file is being fetched again.</summary>
    WaitingForRepair = 4,

    /// <summary>A permission or grant is needed first.</summary>
    WaitingForPermission = 5,

    /// <summary>The network it needs is not there.</summary>
    WaitingForNetwork = 6,

    /// <summary>Waiting for the phone to be on charge (upgrades only).</summary>
    WaitingForCharge = 7,
}

/// <summary>One component's state.</summary>
/// <param name="Component">Which: <see cref="ReadinessComponents"/>.</param>
/// <param name="Phase">Where it has got to.</param>
/// <param name="Step">Which part of a load, while <see cref="BrainPhase.Loading"/>.</param>
/// <param name="Kind">Why, while <see cref="BrainPhase.Failed"/> or <see cref="BrainPhase.Paused"/>.</param>
/// <param name="Pause">Why it is paused.</param>
/// <param name="Detail">One plain sentence a person could read.</param>
/// <param name="ModelId">The model involved, when one is.</param>
/// <param name="Since">When it entered this phase.</param>
/// <param name="Progress">0..1 while something measurable is under way.</param>
/// <param name="RetryAfter">When a failed or paused component will try again by itself; null means it will not.</param>
public sealed record ComponentState(
    string Component,
    BrainPhase Phase,
    LoadStep Step,
    FailureKind? Kind,
    PauseReason Pause,
    string Detail,
    string? ModelId,
    DateTimeOffset Since,
    double? Progress = null,
    TimeSpan? RetryAfter = null)
{
    /// <summary>A component in a phase, with nothing else to say.</summary>
    public static ComponentState Of(string component, BrainPhase phase, string detail, DateTimeOffset since, string? modelId = null)
        => new(component, phase, LoadStep.None, null, PauseReason.None, detail, modelId, since);
}

/// <summary>The names of the components the service reports.</summary>
public static class ReadinessComponents
{
    /// <summary>The language model that answers.</summary>
    public const string Brain = "brain";

    /// <summary>Speech recognition.</summary>
    public const string Ears = "ears";

    /// <summary>The voice.</summary>
    public const string Voice = "voice";

    /// <summary>The wake word listener.</summary>
    public const string Wake = "wake";

    /// <summary>Model downloads.</summary>
    public const string Fetch = "fetch";

    /// <summary>The assistant role (default digital assistant).</summary>
    public const string Assistant = "assistant";
}

/// <summary>Everything the service says about itself, at one sequence number.</summary>
/// <param name="Schema">The row schema version; a client reads what it knows.</param>
/// <param name="Seq">Increases only when something changed; a client polls with the last one it saw.</param>
/// <param name="BootId">A new value for every process start, so a client can tell a restart happened.</param>
/// <param name="Brain">The brain, which is what "can it answer" means.</param>
/// <param name="Components">Every other component.</param>
/// <param name="RecentFailures">The last few classified failures, newest first.</param>
/// <param name="Settings">What this service supports, as key/value pairs a client reads instead of hard-coding.</param>
public sealed record ReadinessSnapshot(
    int Schema,
    long Seq,
    string BootId,
    ComponentState Brain,
    IReadOnlyList<ComponentState> Components,
    IReadOnlyList<Failure> RecentFailures,
    IReadOnlyList<KeyValuePair<string, string>> Settings)
{
    /// <summary>The schema this build writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Whether a question would be answered now, without a load.</summary>
    public bool CanAnswerNow => Brain.Phase == BrainPhase.Ready;

    /// <summary>Whether a question would be answered after a load the service will do by itself.</summary>
    public bool CanAnswerSoon => Brain.Phase is BrainPhase.Ready or BrainPhase.Installed or BrainPhase.Loading
        || (Brain.Phase is BrainPhase.Failed or BrainPhase.Paused && Brain.RetryAfter is not null);
}

/// <summary>The keys of <see cref="ReadinessSnapshot.Settings"/>. A client ignores keys it does not know.</summary>
public static class ReadinessSettings
{
    /// <summary>The link's interface token.</summary>
    public const string LinkDescriptor = "link.descriptor";
    /// <summary>The link protocol revision (an integer; higher has more verbs).</summary>
    public const string LinkProtocol = "link.protocol";
    /// <summary>Whether answers stream as they are written ("1").</summary>
    public const string TurnStreaming = "turn.streaming";
    /// <summary>Whether a turn may carry an image ("1").</summary>
    public const string TurnImages = "turn.images";
    /// <summary>Whether agentic turns run tools in the service ("1").</summary>
    public const string TurnAgentic = "turn.agentic";
    /// <summary>The largest audio clip, in bytes.</summary>
    public const string AudioMaxBytes = "audio.maxBytes";
    /// <summary>The audio format.</summary>
    public const string AudioFormat = "audio.format";
    /// <summary>The model that answers.</summary>
    public const string ModelId = "model.id";
    /// <summary>The longest answer to a spoken question, in tokens.</summary>
    public const string MaxTokensSpoken = "gen.maxTokensSpoken";
    /// <summary>The longest answer to a typed question, in tokens.</summary>
    public const string MaxTokensTyped = "gen.maxTokensTyped";
    /// <summary>The longest question accepted, in characters.</summary>
    public const string MaxPromptChars = "ask.maxChars";
    /// <summary>How many questions one app may have waiting.</summary>
    public const string PerCallerQueued = "ask.perCaller";
    /// <summary>How long a finished question is kept, in hours.</summary>
    public const string RetentionHours = "ask.retentionHours";
    /// <summary>The languages the voice speaks (comma separated).</summary>
    public const string TtsLanguages = "tts.languages";
    /// <summary>The languages the ears hear (comma separated).</summary>
    public const string AsrLanguages = "asr.languages";
    /// <summary>The wake phrase.</summary>
    public const string WakePhrase = "wake.phrase";
    /// <summary>Whether the wake word is listening ("1").</summary>
    public const string Listening = "listening";
    /// <summary>This caller's grants (the LinkScope names, comma separated).</summary>
    public const string CallerScopes = "caller.scopes";
}
