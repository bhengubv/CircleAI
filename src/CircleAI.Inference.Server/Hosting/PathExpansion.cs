// PathExpansion.cs
//
// Expands %LOCALAPPDATA% / %APPDATA% (Windows) and $HOME (Unix) tokens in
// configured directory paths so users can ship an appsettings.json that
// works across platforms.

namespace CircleAI.Inference.Server.Hosting;

internal static class PathExpansion
{
    public static string ExpandUserPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var expanded = Environment.ExpandEnvironmentVariables(raw);

        // On Unix, %VAR% syntax isn't expanded by ExpandEnvironmentVariables —
        // map the most-used Windows tokens to their Unix equivalents.
        if (expanded.Contains("%LOCALAPPDATA%", StringComparison.Ordinal))
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localApp))
                localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            expanded = expanded.Replace("%LOCALAPPDATA%", localApp, StringComparison.Ordinal);
        }
        if (expanded.Contains("%APPDATA%", StringComparison.Ordinal))
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(roaming))
                roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            expanded = expanded.Replace("%APPDATA%", roaming, StringComparison.Ordinal);
        }
        if (expanded.Contains("$HOME", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Replace("$HOME", home, StringComparison.Ordinal);
        }
        return expanded;
    }
}
