// PluginContext.cs
//
// (3.2.0) Default IPluginContext + permission-gated wrapper. Mirrors
// CircleUp's PluginContext + PermissionedPluginContext — same shape,
// `vault.*` permissions renamed to `workspace.*`.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace CircleAI.Plugins;

/// <summary>(3.2.0) Default <see cref="IPluginContext"/>.</summary>
public sealed class PluginContext : IPluginContext
{
    private readonly Func<string?> _workspacePath;

    public PluginContext(Func<string?> workspacePathAccessor, IPluginEvents events, ILogger logger)
    {
        _workspacePath = workspacePathAccessor ?? (() => null);
        Events         = events ?? throw new ArgumentNullException(nameof(events));
        Logger         = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string?       WorkspacePath => _workspacePath();
    public IPluginEvents Events        { get; }
    public ILogger       Logger        { get; }
}

/// <summary>
/// (3.2.0) Wraps an inner context and gates capabilities by a granted-
/// permission set. Mirrors CircleUp's PermissionedPluginContext.
/// </summary>
public sealed class PermissionedPluginContext : IPluginContext
{
    public static class Permissions
    {
        public const string WorkspaceRead   = "workspace.read";
        public const string WorkspaceWrite  = "workspace.write";
        public const string EventsSubscribe = "events.subscribe";
    }

    private readonly IPluginContext _inner;
    private readonly HashSet<string> _granted;
    private readonly IPluginEvents _events;

    public PermissionedPluginContext(IPluginContext inner, IEnumerable<string> grantedPermissions)
    {
        _inner   = inner   ?? throw new ArgumentNullException(nameof(inner));
        _granted = new HashSet<string>(grantedPermissions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _events  = _granted.Contains(Permissions.EventsSubscribe) ? _inner.Events : new SilentEvents();
    }

    public string? WorkspacePath
        => _granted.Contains(Permissions.WorkspaceRead) || _granted.Contains(Permissions.WorkspaceWrite)
            ? _inner.WorkspacePath
            : null;

    public IPluginEvents Events => _events;
    public ILogger       Logger => _inner.Logger;

    /// <summary>Drop-on-the-floor event bus for permission-denied plugins.</summary>
    private sealed class SilentEvents : IPluginEvents
    {
        public IDisposable Subscribe(string eventName, Action<object?> handler) => NoopDisposable.Instance;
        public void Raise(string eventName, object? payload) { }
        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
