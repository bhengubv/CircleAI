// HostExec.cs
//
// Tiny helper that runs a one-shot external command and captures stdout.
// Used by Linux/macOS probes to read sysctl/lscpu/lspci output without
// importing platform-specific NuGet packages. Failure (non-zero exit,
// missing binary, timeout) is mapped to an empty string — probes are
// best-effort and never throw.

using System.Diagnostics;

namespace CircleAI.Runtime.Capabilities.Internal;

internal static class HostExec
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and
    /// returns its captured stdout (trimmed). Returns the empty string when
    /// the binary is missing, the process exits non-zero, or the timeout is
    /// hit. Never throws.
    /// </summary>
    public static async Task<string> CaptureStdoutAsync(
        string fileName,
        string arguments,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return string.Empty;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);

            var stdoutTask = p.StandardOutput.ReadToEndAsync(linked.Token);

            try
            {
                await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return string.Empty;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            return p.ExitCode == 0 ? stdout.Trim() : string.Empty;
        }
        catch
        {
            // Missing binary, perm error, anything — best effort, return empty.
            return string.Empty;
        }
    }
}
