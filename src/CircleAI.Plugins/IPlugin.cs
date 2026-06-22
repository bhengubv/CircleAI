// IPlugin.cs
//
// (3.2.0) Plugin contract surface. Lift from CircleUp's IPlugin — vault
// + AgentTask references stripped, replaced with a generic string-keyed
// event bus so any consumer can publish whatever event names it wants.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CircleAI.Plugins;

/// <summary>
/// (3.2.0) Contract every CircleAI plugin implements. Plugins are .NET
/// assemblies that ship one or more <see cref="IPlugin"/> classes; the
/// host loads them via <see cref="PluginLoader"/> and gives them a
/// chance to register services + react to host events.
///
/// Plugins are deliberately limited to what we can keep stable. They
/// can subscribe to host events through <see cref="IPluginContext.Events"/>
/// and read the configured workspace path through <see cref="IPluginContext.WorkspacePath"/>.
/// </summary>
public interface IPlugin
{
    /// <summary>Unique identifier (matches the assembly name by default).</summary>
    string Id { get; }

    /// <summary>Human-readable label.</summary>
    string DisplayName { get; }

    /// <summary>SemVer string.</summary>
    string Version { get; }

    /// <summary>Called once at host startup. The plugin reads/writes services through the supplied context and wires its lifetime hooks.</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>Called when the host is shutting down or the plugin is being unloaded.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// (3.2.0) Stable surface plugins are allowed to use. Doesn't expose
/// <c>IServiceCollection</c> — plugins should not be able to swap out
/// host services. They get an event bus, a logger, and the configured
/// workspace path.
/// </summary>
public interface IPluginContext
{
    /// <summary>Host-configured workspace directory (or null when not set).</summary>
    string? WorkspacePath { get; }

    /// <summary>Event bus the host raises events into.</summary>
    IPluginEvents Events { get; }

    /// <summary>Logger scoped to this plugin.</summary>
    ILogger Logger { get; }
}

/// <summary>
/// (3.2.0) String-keyed event bus. The host raises events via
/// <see cref="Raise"/>; plugins subscribe with <see cref="Subscribe"/>.
/// Payload is opaque <see cref="object"/>; senders + listeners agree on
/// the concrete type per event name.
/// </summary>
public interface IPluginEvents
{
    /// <summary>Subscribe to events. Returns an unsubscribe handle.</summary>
    IDisposable Subscribe(string eventName, Action<object?> handler);

    /// <summary>Raise an event. Host-only API.</summary>
    void Raise(string eventName, object? payload);
}

/// <summary>(3.2.0) Thread-safe default <see cref="IPluginEvents"/>.</summary>
public sealed class PluginEvents : IPluginEvents
{
    private readonly ConcurrentDictionary<string, List<Action<object?>>> _handlers = new(StringComparer.Ordinal);

    public IDisposable Subscribe(string eventName, Action<object?> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        var list = _handlers.GetOrAdd(eventName, _ => new List<Action<object?>>());
        lock (list) list.Add(handler);
        return new Subscription(this, eventName, handler);
    }

    public void Raise(string eventName, object? payload)
    {
        if (!_handlers.TryGetValue(eventName, out var list)) return;
        Action<object?>[] snapshot;
        lock (list) snapshot = list.ToArray();
        foreach (var h in snapshot)
        {
            try { h(payload); }
            catch { /* an unhealthy plugin must not corrupt the host */ }
        }
    }

    private void Unsubscribe(string eventName, Action<object?> handler)
    {
        if (_handlers.TryGetValue(eventName, out var list))
        {
            lock (list) list.Remove(handler);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly PluginEvents _owner;
        private readonly string _name;
        private readonly Action<object?> _handler;
        private bool _disposed;

        public Subscription(PluginEvents owner, string name, Action<object?> handler)
        {
            _owner = owner;
            _name = name;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Unsubscribe(_name, _handler);
        }
    }
}

/// <summary>
/// (3.2.0) Well-known event names. Hosts can raise + plugins can
/// subscribe to these without coordinating ad-hoc names.
/// </summary>
public static class PluginEventNames
{
    public const string WorkspaceLoaded = "workspace.loaded";
    public const string ChatMessage     = "chat.message";
    public const string ModelLoaded     = "model.loaded";
    public const string ModelUnloaded   = "model.unloaded";
}
