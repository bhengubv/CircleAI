// AgentInvocationException.cs
//
// Raised by IAgentPeerProtocol.InvokeAsync when a peer Declines the
// invocation or otherwise fails to return a Response envelope.

namespace Circle.AI.Agents.Peer;

/// <summary>
/// Thrown when a peer declines an <see cref="AgentMessageKind.Invoke"/> or
/// returns an error response.
/// </summary>
public sealed class AgentInvocationException : Exception
{
    /// <summary>The peer that declined or errored, if known.</summary>
    public string? PeerUhid { get; }

    /// <summary>The decline envelope returned by the peer, if any.</summary>
    public AgentMessage? DeclineMessage { get; }

    /// <summary>Creates a new <see cref="AgentInvocationException"/>.</summary>
    public AgentInvocationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new <see cref="AgentInvocationException"/>.</summary>
    public AgentInvocationException(string message, string peerUhid)
        : base(message)
    {
        PeerUhid = peerUhid;
    }

    /// <summary>Creates a new <see cref="AgentInvocationException"/> carrying the decline envelope.</summary>
    public AgentInvocationException(string message, string peerUhid, AgentMessage declineMessage)
        : base(message)
    {
        PeerUhid = peerUhid;
        DeclineMessage = declineMessage;
    }

    /// <summary>Creates a new <see cref="AgentInvocationException"/>.</summary>
    public AgentInvocationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
