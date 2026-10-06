// DesktopPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Desktop;

public sealed record WindowDescriptor(string WindowId, string Title, string ProcessName, int X, int Y, int Width, int Height, bool IsForeground);
public sealed record DesktopShortcut(string ShortcutId, string KeyChord, string Action);
public sealed record DesktopSession(string SessionId, string UserName, DateTimeOffset StartedUtc, IReadOnlyList<string> ActiveWorkspaces);

public interface IDesktopBoard
{
    void Track(WindowDescriptor w);
    WindowDescriptor? GetWindow(string id);
    IReadOnlyList<WindowDescriptor> WindowsOf(string processName);
    void RegisterShortcut(DesktopShortcut s);
    string? ActionFor(string keyChord);
    void OpenSession(DesktopSession s);
    DesktopSession? GetSession(string id);
}

public sealed class InMemoryDesktopBoard : IDesktopBoard
{
    private readonly ConcurrentDictionary<string, WindowDescriptor> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DesktopShortcut> _shortcuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DesktopSession> _sessions = new(StringComparer.Ordinal);

    public void Track(WindowDescriptor w) { ArgumentNullException.ThrowIfNull(w); _windows[w.WindowId] = w; }
    public WindowDescriptor? GetWindow(string id) => _windows.GetValueOrDefault(id);
    public IReadOnlyList<WindowDescriptor> WindowsOf(string processName)
        => _windows.Values.Where(w => string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase)).ToArray();
    public void RegisterShortcut(DesktopShortcut s) { ArgumentNullException.ThrowIfNull(s); _shortcuts[s.KeyChord] = s; }
    public string? ActionFor(string keyChord)
    {
        if (string.IsNullOrWhiteSpace(keyChord)) throw new ArgumentException("keyChord required");
        return _shortcuts.TryGetValue(keyChord, out var s) ? s.Action : null;
    }
    public void OpenSession(DesktopSession s) { ArgumentNullException.ThrowIfNull(s); _sessions[s.SessionId] = s; }
    public DesktopSession? GetSession(string id) => _sessions.GetValueOrDefault(id);
}
