// AgentHandoff.cs
//
// (3.3.0) Multi-agent handoff: swap the AI persona mid-call without
// dropping the carrier leg. Caller A is talking to "Reception"; the
// reception agent decides this is a billing question, hands off to
// "Billing" — same call, same audio stream, different system prompt
// and toolset.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One AI agent persona that can be handed control of a call.</summary>
/// <param name="AgentId">Stable id ("reception" / "billing" / "tier2-support").</param>
/// <param name="DisplayName">Friendly name surfaced to logging + analytics.</param>
/// <param name="SystemPrompt">Persona instructions.</param>
/// <param name="GreetingText">Optional first sentence the agent says when it takes over.</param>
public sealed record CallAgent(
    string  AgentId,
    string  DisplayName,
    string  SystemPrompt,
    string? GreetingText = null);

/// <summary>(3.3.0) Outcome of a handoff attempt.</summary>
public sealed record HandoffResult(
    bool       Succeeded,
    string?    FailureReason,
    CallAgent? ActiveAgent);

/// <summary>(3.3.0) Drives mid-call agent handoff.</summary>
public interface IAgentHandoffOrchestrator
{
    /// <summary>The agent currently in control of the call.</summary>
    CallAgent? CurrentAgent { get; }

    /// <summary>Available agents indexed by id.</summary>
    IReadOnlyDictionary<string, CallAgent> AgentCatalog { get; }

    /// <summary>Hand the call over to <paramref name="targetAgentId"/>; speaks the greeting via the supplied TTS.</summary>
    ValueTask<HandoffResult> HandoffAsync(
        ICallSession        session,
        string              targetAgentId,
        BriefingSynthesiser tts,
        CancellationToken   ct = default);

    /// <summary>Register / replace an agent in the catalog at runtime.</summary>
    void RegisterAgent(CallAgent agent);

    /// <summary>Set the initial agent on a fresh call without TTS (no greeting).</summary>
    void SetInitialAgent(string agentId);
}

/// <summary>(3.3.0) Default in-memory orchestrator. Thread-safe via simple lock.</summary>
public sealed class DefaultAgentHandoffOrchestrator : IAgentHandoffOrchestrator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CallAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private CallAgent? _current;
    private readonly ILogger _logger;

    public DefaultAgentHandoffOrchestrator(
        IEnumerable<CallAgent>?    seed   = null,
        ILogger<DefaultAgentHandoffOrchestrator>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        if (seed is not null)
        {
            foreach (var agent in seed)
            {
                _agents[agent.AgentId] = agent;
            }
        }
    }

    public CallAgent? CurrentAgent
    {
        get { lock (_gate) return _current; }
    }

    public IReadOnlyDictionary<string, CallAgent> AgentCatalog
    {
        get { lock (_gate) return new Dictionary<string, CallAgent>(_agents, StringComparer.OrdinalIgnoreCase); }
    }

    public void RegisterAgent(CallAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (string.IsNullOrWhiteSpace(agent.AgentId))
        {
            throw new ArgumentException("AgentId is required.", nameof(agent));
        }
        lock (_gate) { _agents[agent.AgentId] = agent; }
    }

    public void SetInitialAgent(string agentId)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(agentId, out var agent))
            {
                throw new InvalidOperationException($"Agent '{agentId}' is not registered.");
            }
            _current = agent;
        }
    }

    public async ValueTask<HandoffResult> HandoffAsync(
        ICallSession        session,
        string              targetAgentId,
        BriefingSynthesiser tts,
        CancellationToken   ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tts);
        if (string.IsNullOrWhiteSpace(targetAgentId))
        {
            return new HandoffResult(false, "targetAgentId is required", _current);
        }

        CallAgent target;
        CallAgent? previous;
        lock (_gate)
        {
            if (!_agents.TryGetValue(targetAgentId, out var found))
            {
                return new HandoffResult(false, $"Agent '{targetAgentId}' is not registered.", _current);
            }
            target   = found;
            previous = _current;
            if (previous?.AgentId.Equals(target.AgentId, StringComparison.OrdinalIgnoreCase) == true)
            {
                return new HandoffResult(true, null, previous);
            }
            _current = target;
        }

        _logger.LogInformation("Call {Id} handed off from {From} to {To}",
            session.Info.CallId, previous?.DisplayName ?? "(none)", target.DisplayName);

        if (!string.IsNullOrWhiteSpace(target.GreetingText))
        {
            try
            {
                var greetingPcm = await tts(target.GreetingText, ct).ConfigureAwait(false);
                if (!greetingPcm.IsEmpty)
                {
                    await session.SendAudioAsync(
                        new AudioFrame(greetingPcm, CallMediaFormat.Pcm24000, TimeSpan.Zero), ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Greeting playback failed during handoff to {Agent}", target.AgentId);
            }
        }

        return new HandoffResult(true, null, target);
    }
}
