// LoggedToolBridge.cs
//
// WHICH TOOL RAN, SAID WHERE A DEVELOPER CAN READ IT.
//
// A tool turn that answers plausibly is indistinguishable from one that invented its number
// (CircleAISession's tool turn returns the list for exactly that reason) - and in the
// service's inference process there is no screen to show the list on. So each call says its
// tool, whether it worked and how long it took. Never the arguments or the result: a search
// query is the person's own words, and a log line must be safe to read.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Tools;

/// <summary>An <see cref="IToolBridge"/> that logs each call's tool and outcome - never its arguments or result.</summary>
public sealed class LoggedToolBridge : IToolBridge
{
    private readonly IToolBridge _inner;
    private readonly Action<string> _log;

    /// <summary>Logs every call through <paramref name="inner"/> to <paramref name="log"/>.</summary>
    public LoggedToolBridge(IToolBridge inner, Action<string> log)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> AvailableTools => _inner.AvailableTools;

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolDefinition>> GetAvailableToolsAsync(CancellationToken ct = default)
        => _inner.GetAvailableToolsAsync(ct);

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await _inner.InvokeAsync(invocation, ct).ConfigureAwait(false);
            Say($"tool ran: {invocation.ToolName} ({(result.Success ? "ok" : "failed")}) in {clock.ElapsedMilliseconds} ms");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say($"tool threw: {invocation.ToolName} ({ex.GetType().Name}) after {clock.ElapsedMilliseconds} ms");
            throw;
        }
    }

    // A log that throws must not take the tool's answer with it.
    private void Say(string line)
    {
        try { _log(line); }
        catch { /* the answer matters more than the line */ }
    }
}
