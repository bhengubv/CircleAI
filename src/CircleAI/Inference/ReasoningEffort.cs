// ReasoningEffort.cs
//
// HOW HARD A BRAIN THINKS BEFORE IT ANSWERS, AND WHICH GENERATORS TAKE TOOLS IN THEIR OWN WORDS.
//
// Two small contracts beside IChatGenerator. The effort is chosen per question by EffortRouter
// and carried on GenerationOptions; the tool-taking marker tells a host to hand a generator the
// tools themselves (GenerationOptions.Tools) instead of describing them in the prompt.

namespace CircleAI.Inference;

/// <summary>How much a model thinks before it answers.</summary>
/// <remarks>
/// THE NAMES ARE THE ONES CHAT TEMPLATES USE. A template that takes <c>reasoning_effort</c> is
/// given the level's name in lower case ("low", "medium", "high", "xhigh"); one that does not -
/// Qwen3.5's - takes thinking on or off, and the thinking budget sets how long. Appended, never
/// renumbered: the level travels between processes by name.
/// </remarks>
public enum ReasoningEffort
{
    /// <summary>No thinking: the answer starts at once.</summary>
    Off = 0,

    /// <summary>A short thought.</summary>
    Low = 1,

    /// <summary>A thought of moderate length: a phone's choice for a hard question.</summary>
    Medium = 2,

    /// <summary>A long thought.</summary>
    High = 3,

    /// <summary>The longest thought a device will wait for: a computer's choice for a hard question.</summary>
    XHigh = 4,
}

/// <summary>Helpers over <see cref="ReasoningEffort"/>.</summary>
public static class ReasoningEfforts
{
    /// <summary>The level as a chat template's <c>reasoning_effort</c> spells it: "off", "low", "medium", "high", "xhigh".</summary>
    public static string TemplateName(this ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Low    => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High   => "high",
        ReasoningEffort.XHigh  => "xhigh",
        _                      => "off",
    };

    /// <summary>The level a name spells, ignoring case; null for a name that is not one.</summary>
    public static ReasoningEffort? Parse(string? name) =>
        Enum.TryParse<ReasoningEffort>(name, ignoreCase: true, out var effort) && Enum.IsDefined(effort) ? effort : null;
}

/// <summary>
/// A generator whose model is handed <see cref="GenerationOptions.Tools"/> through its own chat
/// template, and whose sampler holds every call the model writes to that template's form.
/// </summary>
/// <remarks>
/// A HOST GIVES SUCH A GENERATOR THE TOOLS THEMSELVES, not a description of them in the prompt:
/// the template's own tools block is the form the model was trained on, and the call that comes
/// back can only be well formed, because the logits are the generator's to mask.
/// </remarks>
public interface IToolCallingGenerator : IChatGenerator
{
}
