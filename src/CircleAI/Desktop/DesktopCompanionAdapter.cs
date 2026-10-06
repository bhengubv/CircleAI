using CircleAI.Companion;
using System.Runtime.CompilerServices;
using System.Text;

namespace CircleAI.Desktop;

/// <summary>
/// Wraps <see cref="ICompanionSession"/> with desktop-surface context:
/// operating system, active application, and optional clipboard content.
/// Enables file-system-aware, clipboard-aware Companion responses.
/// </summary>
public sealed class DesktopCompanionAdapter : ICompanionSession
{
    private readonly ICompanionSession _inner;

    /// <summary>Name of the currently active application, if known.</summary>
    public string? ActiveApplication { get; set; }

    /// <summary>
    /// Clipboard text to inject as context (only when the user has
    /// explicitly granted access).
    /// </summary>
    public string? ClipboardContent { get; set; }

    public DesktopCompanionAdapter(ICompanionSession inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string SessionId => _inner.SessionId;
    public string IdentityId => _inner.IdentityId;
    public InterfaceKind Interface => InterfaceKind.Desktop;
    public IReadOnlyList<CompanionTurn> History => _inner.History;
    public event EventHandler<CompanionProactiveEvent>? ProactiveMessageReady
    {
        add    => _inner.ProactiveMessageReady += value;
        remove => _inner.ProactiveMessageReady -= value;
    }

    public CompanionContext GetContext() => _inner.GetContext();
    public Task RefreshContextAsync(CancellationToken ct = default) => _inner.RefreshContextAsync(ct);
    public Task SignalFeedbackAsync(bool positive, string? note = null, CancellationToken ct = default)
        => _inner.SignalFeedbackAsync(positive, note, ct);

    public Task<string> SendAsync(string message, CancellationToken ct = default)
        => _inner.SendAsync(EnrichMessage(message), ct);
    public IAsyncEnumerable<string> StreamAsync(
        string message, CancellationToken ct = default)
        => _inner.StreamAsync(EnrichMessage(message), ct);
    public Task<string> AgentAsync(string instruction, CancellationToken ct = default)
        => _inner.AgentAsync(EnrichMessage(instruction), ct);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private string EnrichMessage(string message)
    {
        var sb = new StringBuilder(message);
        if (!string.IsNullOrWhiteSpace(ActiveApplication))
        {
            sb.AppendLine();
            sb.Append($"[Desktop context] Active app: {ActiveApplication}");
        }
        if (!string.IsNullOrWhiteSpace(ClipboardContent))
        {
            sb.AppendLine();
            sb.Append($"[Clipboard] {ClipboardContent.AsSpan(0, Math.Min(200, ClipboardContent.Length))}");
        }
        return sb.ToString().TrimEnd();
    }

    public Task<string> DiagnoseSlowdownAsync(string symptoms, string systemSpecs, CancellationToken ct=default)
        => _inner.AgentAsync($"Diagnose desktop slowdown: {symptoms} on {systemSpecs}. Top 5 suspect causes + how to verify each in 60 seconds.", ct);

    public Task<string> WriteShortcutCheatsheetAsync(string appName, string proficiencyLevel, CancellationToken ct=default)
        => _inner.AgentAsync($"Write a one-page keyboard shortcut cheatsheet for {appName}, {proficiencyLevel} user. Group by action category.", ct);

    public Task<string> AutomateRepetitiveTaskAsync(string taskDescription, string preferredTool, CancellationToken ct=default)
        => _inner.AgentAsync($"Suggest automation for: {taskDescription} using {preferredTool}. Step-by-step + edge cases.", ct);

    public Task<string> DesignWorkspaceLayoutAsync(string monitorCount, string primaryWorkflow, CancellationToken ct=default)
        => _inner.AgentAsync($"Design a {monitorCount}-monitor workspace layout for: {primaryWorkflow}. Apps per screen, hotkey conventions, eye-line ergonomics.", ct);

}
