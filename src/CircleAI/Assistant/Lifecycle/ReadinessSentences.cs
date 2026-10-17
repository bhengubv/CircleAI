// ReadinessSentences.cs
//
// WHAT THE SERVICE SAYS ABOUT ITSELF, IN WORDS A PERSON READS ON A NOTIFICATION OR A
// SETUP SCREEN. One owner, so the shade, the app and the status verb never disagree.

namespace CircleAI.Assistant.Lifecycle;

/// <summary>Plain sentences for component states.</summary>
public static class ReadinessSentences
{
    /// <summary>Ready to answer.</summary>
    public const string Ready = "Ready to answer.";

    /// <summary>Loading.</summary>
    public const string Loading = "Getting its brain ready.";

    /// <summary>A model is on the phone and not loaded.</summary>
    public const string Installed = "Ready - it loads its brain when you ask.";

    /// <summary>No model yet.</summary>
    public const string NoModel = "No brain on this phone yet - finish setting it up.";

    /// <summary>The sentence for a component's state.</summary>
    public static string For(ComponentState state) => state.Phase switch
    {
        BrainPhase.Ready     => Ready,
        BrainPhase.Loading   => Loading,
        BrainPhase.Installed => Installed,
        BrainPhase.NoModel   => NoModel,
        BrainPhase.Failed or BrainPhase.Paused => state.Kind is { } k ? For(k) : "It stopped, and will try again.",
        _ => "Starting.",
    };

    /// <summary>The sentence for why something stopped.</summary>
    public static string For(FailureKind kind) => kind switch
    {
        FailureKind.OutOfMemory         => "Waiting for the phone to free some memory.",
        FailureKind.AndroidStopped      => "The phone paused it; it will carry on by itself.",
        FailureKind.AndroidTimeout      => "The phone's time limit stopped it; it will carry on by itself.",
        FailureKind.NativeWorkerCrashed => "Its brain crashed; it is starting again.",
        FailureKind.ModelFilesDamaged   => "Part of its brain is damaged and is being fetched again.",
        FailureKind.PermissionMissing   => "It needs a permission first.",
        FailureKind.NotInstalled        => NoModel,
        FailureKind.TimedOut            => "Its brain stopped responding; it is starting again.",
        _                               => "It stopped, and will try again.",
    };
}
