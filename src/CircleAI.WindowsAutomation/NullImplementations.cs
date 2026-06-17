// NullImplementations.cs — (2.9.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.WindowsAutomation;

public sealed class NullUiAutomationDriver : IUiAutomationDriver
{
    public static readonly NullUiAutomationDriver Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<UiElement>> SnapshotAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<UiElement>>(Array.Empty<UiElement>());
    public ValueTask ClickAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask TypeAsync(string text, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask KeyAsync(string key, CancellationToken ct = default)   => ValueTask.CompletedTask;
}
