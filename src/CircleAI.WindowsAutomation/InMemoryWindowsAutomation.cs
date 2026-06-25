// InMemoryWindowsAutomation.cs
//
// (3.3.0) Real-but-virtual UIA driver. Hosts snap a real Win32-UIA
// implementation in for production; this in-memory driver lets tests
// drive a virtual UI without touching the desktop. Click + Type + Key
// raise events the host can observe.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.WindowsAutomation;

public sealed record UiAutomationEvent(string Kind, string? ElementId, string? Payload);

public sealed class InMemoryUiAutomationDriver : IUiAutomationDriver
{
    private readonly ConcurrentDictionary<string, UiElement> _elements = new(StringComparer.Ordinal);
    private readonly List<Action<UiAutomationEvent>> _observers = new();
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public void Register(UiElement el) { ArgumentNullException.ThrowIfNull(el); _elements[el.ElementId] = el; }
    public void Observe(Action<UiAutomationEvent> obs) { ArgumentNullException.ThrowIfNull(obs); lock (_lock) _observers.Add(obs); }

    public ValueTask<IReadOnlyList<UiElement>> SnapshotAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<UiElement>>(_elements.Values.ToArray());

    public ValueTask ClickAsync(string elementId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(elementId)) throw new ArgumentException("elementId required", nameof(elementId));
        if (!_elements.ContainsKey(elementId))    throw new InvalidOperationException($"Unknown element '{elementId}'.");
        Notify(new UiAutomationEvent("click", elementId, null));
        return ValueTask.CompletedTask;
    }

    public ValueTask TypeAsync(string text, CancellationToken ct = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        Notify(new UiAutomationEvent("type", null, text));
        return ValueTask.CompletedTask;
    }

    public ValueTask KeyAsync(string keyName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyName)) throw new ArgumentException("keyName required", nameof(keyName));
        Notify(new UiAutomationEvent("key", null, keyName));
        return ValueTask.CompletedTask;
    }

    private void Notify(UiAutomationEvent ev)
    {
        Action<UiAutomationEvent>[] snap;
        lock (_lock) snap = _observers.ToArray();
        foreach (var o in snap)
        {
            try { o(ev); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.WindowsAutomation] UI observer threw: {ex.Message}"); }
        }
    }
}
