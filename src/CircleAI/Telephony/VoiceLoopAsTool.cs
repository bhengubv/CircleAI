// VoiceLoopAsTool.cs
//
// (3.3.0) Expose the CircleAI voice loop as a tool an external agent
// framework (LangGraph, OpenAI Agents, CrewAI) can call. The framework
// hands us a number to call + a script, we drive the call to
// completion, return a structured result.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Request to make one outbound voice call as a tool invocation.</summary>
/// <param name="ToNumber">E.164 destination number.</param>
/// <param name="Goal">Plain-English goal ("Book a haircut for Sipho on Saturday").</param>
/// <param name="ContextJson">Extra structured context the agent needs.</param>
/// <param name="SystemPrompt">Persona / script for the voice agent.</param>
/// <param name="MaxDuration">Hard ceiling on call length.</param>
public sealed record VoiceLoopToolRequest(
    string    ToNumber,
    string    Goal,
    string?   ContextJson  = null,
    string?   SystemPrompt = null,
    TimeSpan? MaxDuration  = null);

/// <summary>(3.3.0) Result of the call returned to the calling agent.</summary>
/// <param name="GoalAchieved">True if the AI reports it completed the goal.</param>
/// <param name="Summary">Natural-language summary the AI wrote.</param>
/// <param name="CallId">Carrier call id.</param>
/// <param name="Duration">Actual call duration.</param>
/// <param name="Transcript">Full conversation transcript.</param>
/// <param name="StructuredOutputJson">Optional JSON the AI extracted (e.g. appointment time).</param>
public sealed record VoiceLoopToolResult(
    bool      GoalAchieved,
    string    Summary,
    string    CallId,
    TimeSpan  Duration,
    string    Transcript,
    string?   StructuredOutputJson);

/// <summary>(3.3.0) Voice-loop-as-a-tool surface.</summary>
public interface IVoiceLoopTool
{
    /// <summary>(3.3.0) Make the call and report back.</summary>
    Task<VoiceLoopToolResult> InvokeAsync(VoiceLoopToolRequest request, CancellationToken ct = default);
}

/// <summary>(3.3.0) Driver that delegates the actual call to a host-supplied runner.</summary>
public sealed class VoiceLoopAsTool : IVoiceLoopTool
{
    private readonly Func<VoiceLoopToolRequest, CancellationToken, Task<VoiceLoopToolResult>> _runner;
    private readonly TimeSpan _defaultMaxDuration;

    public VoiceLoopAsTool(
        Func<VoiceLoopToolRequest, CancellationToken, Task<VoiceLoopToolResult>> runner,
        TimeSpan? defaultMaxDuration = null)
    {
        _runner             = runner ?? throw new ArgumentNullException(nameof(runner));
        _defaultMaxDuration = defaultMaxDuration ?? TimeSpan.FromMinutes(5);
    }

    public async Task<VoiceLoopToolResult> InvokeAsync(VoiceLoopToolRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ToNumber))
        {
            throw new ArgumentException("ToNumber is required.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Goal))
        {
            throw new ArgumentException("Goal is required.", nameof(request));
        }

        var maxDuration = request.MaxDuration ?? _defaultMaxDuration;
        using var timeoutCts = new CancellationTokenSource(maxDuration);
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await _runner(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new VoiceLoopToolResult(
                GoalAchieved: false,
                Summary:      $"Call timed out after {maxDuration.TotalMinutes:F1} minutes.",
                CallId:       "",
                Duration:     maxDuration,
                Transcript:   "",
                StructuredOutputJson: null);
        }
    }

    /// <summary>(3.3.0) Tool descriptor for use with <see cref="IToolCallRegistry"/>.</summary>
    public static ToolDefinition Descriptor { get; } = new(
        Name: "make_voice_call",
        Description: "Place an outbound phone call and follow the supplied goal/script. Returns whether the goal was achieved.",
        ArgumentsJsonSchema: """
        {
          "type": "object",
          "properties": {
            "to_number":     { "type": "string", "description": "E.164 destination." },
            "goal":          { "type": "string" },
            "context_json":  { "type": "string", "nullable": true },
            "system_prompt": { "type": "string", "nullable": true },
            "max_duration_seconds": { "type": "integer", "nullable": true }
          },
          "required": ["to_number", "goal"]
        }
        """);
}
