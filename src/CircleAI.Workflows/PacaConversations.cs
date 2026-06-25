// PacaConversations.cs
//
// (3.3.0) Conversation state machine + OpenHands runtime contract +
// repo / PR helpers. The actual Docker isolation + OpenHands SDK
// integration is host-supplied via IConversationExecutor; this package
// owns the state machine, history, and lifecycle events.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) Conversation state.</summary>
public enum ConversationState { Queued, Running, Finished, Failed, Stopped }

/// <summary>(3.3.0) One conversation between a human + an agent (or multiple agents).</summary>
public sealed record AgentConversation(
    string             Id,
    string             ProjectId,
    string             AgentMemberId,
    string?            HumanMemberId,
    string             OpeningPrompt,
    ConversationState  State,
    DateTimeOffset     QueuedAtUtc,
    DateTimeOffset?    StartedAtUtc,
    DateTimeOffset?    FinishedAtUtc,
    string?            ResultJson,
    string?            FailureReason);

/// <summary>(3.3.0) One executed step in a conversation.</summary>
public sealed record ConversationStep(
    string             ConversationId,
    int                Order,
    string             Speaker,            // "user" / "agent" / "tool"
    string             ContentJson,
    DateTimeOffset     At);

/// <summary>(3.3.0) Permission flag set required to run risky actions.</summary>
public sealed record ConversationPermissions(
    bool               AllowCloneRepos,
    bool               AllowCreatePr);

/// <summary>(3.3.0) Host-supplied executor — invokes OpenHands SDK / Docker container per conversation.</summary>
public interface IConversationExecutor
{
    /// <summary>Start a conversation; emit ConversationStep events into the registry as work progresses.</summary>
    Task RunAsync(
        AgentConversation           conversation,
        ConversationPermissions     permissions,
        Action<ConversationStep>    onStep,
        CancellationToken           ct = default);
}

/// <summary>(3.3.0) Conversation registry + state machine.</summary>
public sealed class PacaConversationRuntime
{
    private readonly ConcurrentDictionary<string, AgentConversation> _conversations = new();
    private readonly ConcurrentDictionary<string, List<ConversationStep>> _steps    = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly IConversationExecutor _executor;
    private readonly Func<DateTimeOffset> _clock;

    public PacaConversationRuntime(IConversationExecutor executor, Func<DateTimeOffset>? clock = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _clock    = clock    ?? (() => DateTimeOffset.UtcNow);
    }

    public AgentConversation Queue(string id, string projectId, string agentMemberId, string openingPrompt, string? humanMemberId = null)
    {
        var c = new AgentConversation(
            Id:             id,
            ProjectId:      projectId,
            AgentMemberId:  agentMemberId,
            HumanMemberId:  humanMemberId,
            OpeningPrompt:  openingPrompt ?? "",
            State:          ConversationState.Queued,
            QueuedAtUtc:    _clock(),
            StartedAtUtc:   null,
            FinishedAtUtc:  null,
            ResultJson:     null,
            FailureReason:  null);
        if (!_conversations.TryAdd(id, c)) throw new InvalidOperationException($"Conversation '{id}' already exists.");
        _steps[id] = new List<ConversationStep>();
        return c;
    }

    public AgentConversation? Get(string id)
        => _conversations.TryGetValue(id, out var c) ? c : null;

    public IReadOnlyList<ConversationStep> Steps(string id)
        => _steps.TryGetValue(id, out var list) ? list.ToArray() : Array.Empty<ConversationStep>();

    /// <summary>(3.3.0) Begin executing the conversation in the background.</summary>
    public async Task StartAsync(string id, ConversationPermissions permissions, CancellationToken outerCt = default)
    {
        if (!_conversations.TryGetValue(id, out var current) || current.State != ConversationState.Queued)
        {
            throw new InvalidOperationException($"Conversation '{id}' is not in Queued state.");
        }
        var started = current with { State = ConversationState.Running, StartedAtUtc = _clock() };
        _conversations[id] = started;

        var cts = new CancellationTokenSource();
        _running[id] = cts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, cts.Token);

        try
        {
            await _executor.RunAsync(started, permissions, step =>
            {
                var list = _steps[id];
                lock (list) list.Add(step);
            }, linked.Token).ConfigureAwait(false);
            _conversations[id] = started with { State = ConversationState.Finished, FinishedAtUtc = _clock(), ResultJson = "{}" };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _conversations[id] = started with { State = ConversationState.Stopped, FinishedAtUtc = _clock() };
        }
        catch (Exception ex)
        {
            _conversations[id] = started with { State = ConversationState.Failed, FinishedAtUtc = _clock(), FailureReason = ex.Message };
        }
        finally
        {
            _running.TryRemove(id, out _);
            cts.Dispose();
        }
    }

    /// <summary>(3.3.0) Stop a running conversation from the UI.</summary>
    public void Stop(string id)
    {
        if (_running.TryGetValue(id, out var cts))
        {
            cts.Cancel();
        }
    }
}
