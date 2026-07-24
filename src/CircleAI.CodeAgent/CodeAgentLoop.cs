// CodeAgentLoop.cs
//
// THE agent loop seam: read files -> propose an edit -> run a command ->
// observe -> iterate, driven by the existing CircleAI.Hosting IAIService brain.
// No new model runtime; no cloud. The loop's control flow, tool dispatch,
// workspace sandbox, and tier gate are real. The DECISIONS (which file, what
// edit) are delegated to the brain — which only exists when a real coding model
// is installed, and the gate says so up front on every device that lacks one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.CodeUnderstanding; // ICodeSearch, CodeMatch
using CircleAI.Core;              // DeviceProbe, DeviceTier, DeviceTierDefaults
using CircleAI.DevTools;          // ICodeEditor, FileEdit
using CircleAI.Hosting;           // IAIService
using CircleAI.Inference;         // ChatMessage, GenerationOptions, SelectionQuality

namespace CircleAI.CodeAgent;

/// <summary>Tunables for a <see cref="CodeAgentLoop"/> run.</summary>
public sealed class CodeAgentOptions
{
    /// <summary>
    /// Hard cap on loop iterations. <c>null</c> derives it from the device tier
    /// via <see cref="DeviceTierDefaults.AgenticMaxIterations"/>.
    /// </summary>
    public int? MaxIterations { get; init; }

    /// <summary>
    /// Whether the loop may issue <c>run_command</c> actions. Default <c>false</c>
    /// — even with a real <see cref="ICommandRunner"/> wired, command execution
    /// stays off until the host turns it on for a run.
    /// </summary>
    public bool AllowCommands { get; init; }

    /// <summary>The coding floor the loop gates against. Default: <see cref="CodingModelRequirements.Default"/>.</summary>
    public CodingModelRequirements Requirements { get; init; } = CodingModelRequirements.Default;

    /// <summary>Cap on how many characters of any single observation are fed back to the brain.</summary>
    public int MaxObservationChars { get; init; } = 8 * 1024;

    /// <summary>Generation knobs for each brain call. <c>null</c> uses the brain's defaults.</summary>
    public GenerationOptions? Generation { get; init; }
}

/// <summary>One executed step of an agent run: what was tried and what came back.</summary>
public sealed record CodeAgentStep(int Index, AgentActionKind Action, string Detail, string Observation);

/// <summary>The outcome of a <see cref="ICodeAgent.RunAsync"/> call.</summary>
/// <param name="Available"><c>false</c> when the tier gate declined the device (see <paramref name="Reason"/>).</param>
/// <param name="Quality">The gate's <see cref="SelectionQuality"/> verdict.</param>
/// <param name="Reason">Human-readable gate justification, safe to show a user.</param>
/// <param name="Steps">Every step taken, in order.</param>
/// <param name="AppliedEdits">Edits actually written to disk across the run.</param>
/// <param name="FinalSummary">The model's closing summary, or why the run stopped.</param>
public sealed record CodeAgentRunResult(
    bool                       Available,
    SelectionQuality           Quality,
    string                     Reason,
    IReadOnlyList<CodeAgentStep> Steps,
    IReadOnlyList<FileEdit>    AppliedEdits,
    string                     FinalSummary);

/// <summary>The on-device coding agent entry point.</summary>
public interface ICodeAgent
{
    /// <summary>
    /// Attempt <paramref name="task"/> against the workspace rooted at
    /// <paramref name="workspaceRoot"/>. Gates on the device first — a run on a
    /// weak phone (or with no installed coding model) returns immediately with
    /// <see cref="CodeAgentRunResult.Available"/> = <c>false</c>.
    /// </summary>
    ValueTask<CodeAgentRunResult> RunAsync(
        string task,
        string workspaceRoot,
        DeviceProbe? probe = null,
        CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ICodeAgent"/>. Composes the tier gate, the brain, the
/// file editor, the (optional) code search, and the command runner into a
/// bounded read/edit/run/observe loop.
/// </summary>
public sealed class CodeAgentLoop : ICodeAgent
{
    private readonly IAIService _brain;
    private readonly ICodeEditor _editor;
    private readonly ICommandRunner _runner;
    private readonly ICodingCapabilityPlanner _planner;
    private readonly ICodeSearch? _search;
    private readonly CodeAgentOptions _options;

    /// <summary>
    /// Wire the loop. <paramref name="search"/> is optional — without it the
    /// <c>search_code</c> action reports "no search backend" rather than failing.
    /// </summary>
    public CodeAgentLoop(
        IAIService brain,
        ICodeEditor editor,
        ICommandRunner runner,
        ICodingCapabilityPlanner planner,
        ICodeSearch? search = null,
        CodeAgentOptions? options = null)
    {
        _brain   = brain   ?? throw new ArgumentNullException(nameof(brain));
        _editor  = editor  ?? throw new ArgumentNullException(nameof(editor));
        _runner  = runner  ?? throw new ArgumentNullException(nameof(runner));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _search  = search;
        _options = options ?? new CodeAgentOptions();
    }

    /// <inheritdoc/>
    public async ValueTask<CodeAgentRunResult> RunAsync(
        string task,
        string workspaceRoot,
        DeviceProbe? probe = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("task required", nameof(task));
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("workspaceRoot required", nameof(workspaceRoot));

        probe ??= DeviceProbe.Snapshot();

        // 1. TIER GATE. This is the whole "honest on a P30 Lite" promise: a weak
        //    phone (or a build with no installed coding model) never enters the
        //    loop.
        var plan = _planner.PlanForCoding(probe);
        if (!plan.IsAvailable)
            return Declined(plan.Quality, plan.Reason);

        // 2. Bring the brain up. The gate cleared the device + catalogue, but the
        //    concrete IAIService still has to load a model; surface a load failure
        //    honestly rather than looping against a dead brain.
        try
        {
            await _brain.StartAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Declined(SelectionQuality.Unavailable,
                $"coding gate passed but the brain failed to load: {ex.Message}");
        }

        if (!_brain.IsReady)
            return Declined(SelectionQuality.Unavailable,
                "the brain did not become ready; no on-device model is loaded.");

        var tier    = probe.Classify();
        var maxIter = _options.MaxIterations ?? DeviceTierDefaults.AgenticMaxIterations(tier);
        if (maxIter < 1) maxIter = 1;

        var steps      = new List<CodeAgentStep>();
        var applied    = new List<FileEdit>();
        var transcript = new List<ChatMessage>
        {
            new("system", BuildSystemPrompt(task, workspaceRoot, _options.AllowCommands, _search is not null)),
            new("user", task),
        };

        var finalSummary = string.Empty;
        for (var i = 0; i < maxIter; i++)
        {
            ct.ThrowIfCancellationRequested();

            string reply;
            try
            {
                reply = await _brain.ChatAsync(transcript, _options.Generation, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                steps.Add(new CodeAgentStep(i, AgentActionKind.Unknown, "chat", $"brain error: {ex.Message}"));
                finalSummary = "run stopped: the brain raised an error.";
                break;
            }

            transcript.Add(new ChatMessage("assistant", reply));
            var action = AgentActionParser.Parse(reply);

            if (action.Kind == AgentActionKind.Finish)
            {
                finalSummary = action.Summary ?? string.Empty;
                steps.Add(new CodeAgentStep(i, action.Kind, "finish", finalSummary));
                break;
            }

            var (detail, observation, editsApplied) =
                await ExecuteAsync(action, workspaceRoot, ct).ConfigureAwait(false);

            applied.AddRange(editsApplied);
            steps.Add(new CodeAgentStep(i, action.Kind, detail, observation));

            // Feed the observation back as a tool turn so the next decision sees it.
            transcript.Add(new ChatMessage("tool", Truncate(observation, _options.MaxObservationChars)));
        }

        if (string.IsNullOrEmpty(finalSummary))
            finalSummary = steps.Count == 0
                ? "no steps were taken."
                : "reached the iteration budget without an explicit finish.";

        return new CodeAgentRunResult(
            Available:    true,
            Quality:      plan.Quality,
            Reason:       plan.Reason,
            Steps:        steps,
            AppliedEdits: applied,
            FinalSummary: finalSummary);
    }

    private static CodeAgentRunResult Declined(SelectionQuality quality, string reason) =>
        new(Available: false, Quality: quality, Reason: reason,
            Steps: Array.Empty<CodeAgentStep>(), AppliedEdits: Array.Empty<FileEdit>(), FinalSummary: string.Empty);

    // Dispatch one parsed action to the appropriate real seam and return
    // (short detail, observation fed back to the brain, edits written).
    private async ValueTask<(string Detail, string Observation, IReadOnlyList<FileEdit> Edits)> ExecuteAsync(
        AgentAction action, string workspaceRoot, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case AgentActionKind.ReadFile:
            {
                var path = ResolvePath(workspaceRoot, action.Path);
                if (path is null)
                    return ("read", "error: missing path, or path escapes the workspace", Array.Empty<FileEdit>());
                try
                {
                    var text = await _editor.ReadAsync(path, ct).ConfigureAwait(false);
                    return ($"read {action.Path}", Truncate(text, _options.MaxObservationChars), Array.Empty<FileEdit>());
                }
                catch (Exception ex)
                {
                    return ($"read {action.Path}", $"error: {ex.Message}", Array.Empty<FileEdit>());
                }
            }

            case AgentActionKind.EditFile:
            {
                var path = ResolvePath(workspaceRoot, action.Path);
                if (path is null)
                    return ("edit", "error: missing path, or path escapes the workspace", Array.Empty<FileEdit>());
                var edit = new FileEdit(path, action.RangeStart, action.RangeEnd, action.Replacement ?? "");
                try
                {
                    await _editor.ApplyAsync(new[] { edit }, ct).ConfigureAwait(false);
                    await _editor.SaveAsync(path, ct).ConfigureAwait(false);
                    return ($"edit {action.Path} [{action.RangeStart}..{action.RangeEnd}]",
                            "ok: edit applied", new[] { edit });
                }
                catch (Exception ex)
                {
                    return ($"edit {action.Path}", $"error: {ex.Message}", Array.Empty<FileEdit>());
                }
            }

            case AgentActionKind.RunCommand:
            {
                if (!_options.AllowCommands)
                    return ("run", "error: command execution is off (CodeAgentOptions.AllowCommands=false)",
                            Array.Empty<FileEdit>());
                if (string.IsNullOrWhiteSpace(action.Executable))
                    return ("run", "error: no executable", Array.Empty<FileEdit>());

                var cwd = ResolvePath(workspaceRoot, action.Path) ?? workspaceRoot;
                var request = new CommandRequest(action.Executable!, action.Args ?? Array.Empty<string>(), cwd);
                var res = await _runner.RunAsync(request, ct).ConfigureAwait(false);
                var obs = res.Executed
                    ? $"exit={res.ExitCode}{(res.TimedOut ? " (timed out)" : "")}\nstdout:\n{res.Stdout}\nstderr:\n{res.Stderr}"
                    : $"not run: {res.Denied}";
                return ($"run {action.Executable}", Truncate(obs, _options.MaxObservationChars), Array.Empty<FileEdit>());
            }

            case AgentActionKind.SearchCode:
            {
                if (_search is null)
                    return ("search", "error: no code-search backend is wired", Array.Empty<FileEdit>());
                if (string.IsNullOrWhiteSpace(action.Query))
                    return ("search", "error: no query", Array.Empty<FileEdit>());
                try
                {
                    var matches = await _search.SearchAsync(action.Query!, action.TopK, ct).ConfigureAwait(false);
                    var sb = new StringBuilder();
                    foreach (var m in matches)
                        sb.Append(m.Path).Append(':').Append(m.Line).Append("  ").AppendLine(m.Snippet);
                    var body = sb.Length == 0 ? "(no matches)" : sb.ToString();
                    return ($"search '{action.Query}'", Truncate(body, _options.MaxObservationChars), Array.Empty<FileEdit>());
                }
                catch (Exception ex)
                {
                    return ("search", $"error: {ex.Message}", Array.Empty<FileEdit>());
                }
            }

            default:
                return ("unknown",
                    "error: could not parse a known action from the reply. Reply with a single JSON action object. " +
                    $"Raw: {Truncate(action.Raw ?? "", 512)}",
                    Array.Empty<FileEdit>());
        }
    }

    // Resolve a model-supplied path against the workspace and refuse anything
    // that escapes it. Path traversal out of the workspace is the obvious way an
    // on-device agent goes from "edit my repo" to "edit /etc" — closed here.
    private static string? ResolvePath(string workspaceRoot, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var full = Path.GetFullPath(
                Path.IsPathRooted(candidate) ? candidate : Path.Combine(root, candidate));

            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return full;
            return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSystemPrompt(string task, string workspaceRoot, bool allowCommands, bool hasSearch)
    {
        var lines = new List<string>
        {
            $"You are an on-device coding agent working inside the workspace: {workspaceRoot}.",
            "Work ONE step at a time. Reply with a SINGLE JSON object and nothing else.",
            "Supported actions:",
            "  {\"action\":\"read_file\",\"path\":\"relative/path\"}",
        };
        if (hasSearch)
            lines.Add("  {\"action\":\"search_code\",\"query\":\"text\",\"top_k\":10}");
        lines.Add("  {\"action\":\"edit_file\",\"path\":\"relative/path\",\"range_start\":0,\"range_end\":0,\"replacement\":\"text\"}");
        if (allowCommands)
            lines.Add("  {\"action\":\"run_command\",\"executable\":\"dotnet\",\"args\":[\"build\"],\"cwd\":\".\"}");
        lines.Add("  {\"action\":\"finish\",\"summary\":\"what you did\"}");
        lines.Add("range_start/range_end are absolute character offsets into the file's CURRENT text; read before you edit.");
        lines.Add("Paths must stay inside the workspace. After each action you receive an observation. Finish when done.");
        lines.Add($"Task: {task}");
        return string.Join("\n", lines);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (max < 1) max = 1;
        return s.Length <= max ? s : s[..max] + $"\n...[truncated {s.Length - max} chars]";
    }
}
