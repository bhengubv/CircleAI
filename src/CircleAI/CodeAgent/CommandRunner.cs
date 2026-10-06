// CommandRunner.cs
//
// The shell / REPL seam that did not exist. The agent loop needs to "run a
// command, observe the output" (openclaw-android runs a coding CLI in Termux) —
// but on-device we NEVER auto-run arbitrary shell. So the default is fail-closed
// (DisabledCommandRunner) and a real runner (ProcessCommandRunner) requires the
// host to opt in with an explicit executable allow-list.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.CodeAgent;

/// <summary>A command the agent wants to run: executable + args + working dir + timeout.</summary>
/// <param name="Executable">Program to run (e.g. <c>dotnet</c>). Matched against the allow-list.</param>
/// <param name="Arguments">Argument vector — passed verbatim, never shell-interpolated.</param>
/// <param name="WorkingDirectory">Directory to run in. Falls back to the process CWD when empty.</param>
/// <param name="TimeoutMs">Wall-clock budget; the process is killed past it. 0 disables the timeout.</param>
public sealed record CommandRequest(
    string                Executable,
    IReadOnlyList<string> Arguments,
    string                WorkingDirectory,
    int                   TimeoutMs = 60_000);

/// <summary>Outcome of a <see cref="CommandRequest"/>.</summary>
/// <param name="Executed"><c>false</c> when the runner declined to run it (see <paramref name="Denied"/>).</param>
/// <param name="ExitCode">Process exit code; <c>-1</c> when not executed or timed out.</param>
/// <param name="Stdout">Captured standard output (truncated to a cap).</param>
/// <param name="Stderr">Captured standard error (truncated to a cap).</param>
/// <param name="TimedOut"><c>true</c> when the process was killed for exceeding its budget.</param>
/// <param name="Denied">Why the runner refused, when <paramref name="Executed"/> is <c>false</c>.</param>
public sealed record CommandResult(
    bool    Executed,
    int     ExitCode,
    string  Stdout,
    string  Stderr,
    bool    TimedOut,
    string? Denied = null)
{
    /// <summary>The command ran to completion with a zero exit code.</summary>
    public bool Success => Executed && !TimedOut && ExitCode == 0;

    /// <summary>A "did not run" result carrying the reason.</summary>
    public static CommandResult NotRun(string reason) =>
        new(Executed: false, ExitCode: -1, Stdout: "", Stderr: "", TimedOut: false, Denied: reason);
}

/// <summary>Runs a command and returns what it printed. Implementations decide what is allowed.</summary>
public interface ICommandRunner
{
    /// <summary>Stable identifier for logs / diagnostics.</summary>
    string BackendId { get; }

    /// <summary>Run <paramref name="request"/>, or decline (returning <see cref="CommandResult.NotRun"/>).</summary>
    ValueTask<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default);
}

/// <summary>
/// Fail-closed default: never runs anything. On-device, arbitrary command
/// execution is a foot-gun; the host must deliberately swap in a
/// <see cref="ProcessCommandRunner"/> with an allow-list to enable it.
/// </summary>
public sealed class DisabledCommandRunner : ICommandRunner
{
    /// <summary>Shared instance — holds no state.</summary>
    public static readonly DisabledCommandRunner Instance = new();

    /// <inheritdoc/>
    public string BackendId => "disabled";

    /// <inheritdoc/>
    public ValueTask<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default) =>
        ValueTask.FromResult(CommandResult.NotRun(
            "command execution is disabled. The host must opt in with a ProcessCommandRunner allow-list."));
}

/// <summary>
/// Real command runner. Executes an out-of-process program, captures stdout /
/// stderr, and enforces a wall-clock timeout. Every executable must be on an
/// explicit allow-list — an empty allow-list is rejected, so this can never
/// become an unrestricted shell by accident.
/// </summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    private readonly HashSet<string> _allowed;
    private readonly int _maxOutputChars;

    /// <summary>
    /// Construct with the executables the host permits. Each request's
    /// executable is matched either exactly or by file-name, so both
    /// <c>dotnet</c> and <c>/usr/bin/dotnet</c> match an allow-list of
    /// <c>{ "dotnet" }</c>.
    /// </summary>
    public ProcessCommandRunner(IEnumerable<string> allowedExecutables, int maxOutputChars = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(allowedExecutables);
        _allowed = new HashSet<string>(allowedExecutables, StringComparer.OrdinalIgnoreCase);
        if (_allowed.Count == 0)
            throw new ArgumentException(
                "An allow-list with at least one executable is required. Refusing to run an unrestricted shell.",
                nameof(allowedExecutables));
        _maxOutputChars = maxOutputChars > 0 ? maxOutputChars : 64 * 1024;
    }

    /// <inheritdoc/>
    public string BackendId => "process";

    /// <inheritdoc/>
    public async ValueTask<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Executable))
            return CommandResult.NotRun("no executable specified");

        var byName = Path.GetFileName(request.Executable);
        if (!_allowed.Contains(request.Executable) && !_allowed.Contains(byName))
            return CommandResult.NotRun($"'{request.Executable}' is not on the command allow-list");

        var psi = new ProcessStartInfo
        {
            FileName               = request.Executable,
            WorkingDirectory       = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                                       ? Environment.CurrentDirectory
                                       : request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        var args = request.Arguments ?? Array.Empty<string>();
        foreach (var a in args)
            psi.ArgumentList.Add(a ?? string.Empty);

        using var proc  = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.AppendLine(e.Data); } };

        try
        {
            if (!proc.Start())
                return CommandResult.NotRun("failed to start process");
        }
        catch (Exception ex)
        {
            return CommandResult.NotRun($"failed to start '{request.Executable}': {ex.Message}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.TimeoutMs > 0)
            timeoutCts.CancelAfter(request.TimeoutMs);

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Distinguish "we hit the timeout" from "the caller cancelled us".
            timedOut = !ct.IsCancellationRequested;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            if (ct.IsCancellationRequested)
                throw;
        }

        var outText = Truncate(stdout.ToString(), _maxOutputChars);
        var errText = Truncate(stderr.ToString(), _maxOutputChars);
        var exit    = timedOut ? -1 : SafeExitCode(proc);
        return new CommandResult(Executed: true, ExitCode: exit, Stdout: outText, Stderr: errText, TimedOut: timedOut);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch { return -1; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n...[truncated {s.Length - max} chars]";
}
