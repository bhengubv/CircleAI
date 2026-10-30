// Sensitive.cs
//
// WHAT A PERSON SAID, OR WAS TOLD, IS NOT LOG MATERIAL. Wrap it and it renders as its
// length - "[42 chars]" - unless the host has deliberately asked for content, the same
// marker-file act that already turns VoiceTrace.KeepsSpeech on. Log files and the
// WolverineAPI outbox outlive the moment and can leave the process; a question somebody
// asked their phone must not travel with them.

namespace CircleAI.Core.Logging;

/// <summary>Keeps a person's words out of logs unless content was explicitly asked for.</summary>
public static class Sensitive
{
    /// <summary>True only when a developer has deliberately asked for content in the logs.</summary>
    public static bool ShowContent { get; set; }

    /// <summary>The text itself when content is asked for; otherwise only its length.</summary>
    public static string Text(string? value) =>
        ShowContent ? value ?? string.Empty : $"[{value?.Length ?? 0} chars]";
}
