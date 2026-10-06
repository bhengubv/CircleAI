// Contracts.cs — (2.9.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.WindowsAutomation;

public sealed record UiElement(string ElementId, string Name, string Kind, int X, int Y, int Width, int Height);

public interface IUiAutomationDriver
{
    string BackendId { get; }
    ValueTask<IReadOnlyList<UiElement>> SnapshotAsync(CancellationToken ct = default);
    ValueTask ClickAsync(string elementId, CancellationToken ct = default);
    ValueTask TypeAsync(string text, CancellationToken ct = default);
    ValueTask KeyAsync(string keyName, CancellationToken ct = default);
}
