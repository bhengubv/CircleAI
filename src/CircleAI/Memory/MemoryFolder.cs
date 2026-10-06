// MemoryFolder.cs
//
// Where memory lives, and which machine is writing to it.
//
// THREE MACHINES, ONE MEMORY. Linux, Windows and a Mac all have to see the
// same store, and the existing arrangement already answers how: the memory
// directory is a symlink into a git repository, so it travels by pull and push
// like everything else.
//
// THAT DECIDES THE FILE LAYOUT, not taste. A SQLite database is a binary blob
// and git cannot merge one - two machines writing the same day produce a
// conflict whose only resolutions are "keep mine" and "keep theirs", and both
// of those destroy memory. So the durable thing is an append-only text log,
// and there is ONE PER MACHINE: a file with a single writer can never conflict,
// which is a stronger guarantee than any merge strategy.
//
// The database is a local index built from the logs. It is disposable, it is
// never committed, and losing it costs a rebuild rather than a memory.

using System;
using System.IO;
using System.Linq;

namespace CircleAI.Memory;

/// <summary>The memory directory, and this machine's identity within it.</summary>
public sealed class MemoryFolder
{
    /// <param name="path">The directory holding the logs and the index.</param>
    /// <param name="machine">
    /// This machine's name. Becomes part of its log's filename, so it must be
    /// stable for a machine and different between machines.
    /// </param>
    public MemoryFolder(string path, string? machine = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A memory folder path is required.", nameof(path));

        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(Path);
        Machine = Sanitise(machine ?? DefaultMachineName());

        // A HOST NAME THAT IDENTIFIES NOTHING IS WORSE THAN NO HOST NAME. Every
        // Android device reports "localhost" for Environment.MachineName, so two
        // phones would both call themselves android-localhost and append to one
        // log - which is the merge problem this whole layout exists to avoid,
        // arriving through the front door. Found by running it on a P30.
        // The condition is the NAME, not where it came from: a caller that
        // passes "android-unnamed" is saying the same thing the environment
        // said, and deserves the same answer.
        if (Machine.EndsWith(Anonymous, StringComparison.Ordinal))
            Machine = Machine[..^Anonymous.Length] + "-" + Installed();
    }

    /// <summary>The directory itself.</summary>
    public string Path { get; }

    /// <summary>What this machine calls itself in the log filenames.</summary>
    public string Machine { get; }

    /// <summary>
    /// The log this machine appends to. Nothing else ever writes to it.
    /// </summary>
    /// <remarks>
    /// ONE WRITER PER FILE IS THE WHOLE SYNC STRATEGY. Git merges two files
    /// that only ever grew independently without asking anybody anything;
    /// it cannot merge two versions of one file that both grew.
    /// </remarks>
    public string OwnLog => System.IO.Path.Combine(Path, $"atoms.{Machine}.jsonl");

    /// <summary>Every machine's log, including this one's.</summary>
    public string[] AllLogs =>
        Directory.Exists(Path)
            ? Directory.GetFiles(Path, "atoms.*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// The local index, rebuilt from the logs and never committed.
    /// </summary>
    /// <remarks>
    /// Named per machine as well: two machines sharing a folder over a synced
    /// drive would otherwise fight over one SQLite file, and a half-written
    /// index is worse than no index because it looks like an answer.
    /// </remarks>
    public string IndexPath => System.IO.Path.Combine(Path, $"index.{Machine}.db");

    /// <summary>A connection string for the local index.</summary>
    public string IndexConnectionString => $"Data Source={IndexPath}";

    /// <summary>
    /// What a .gitignore in this folder has to say.
    /// </summary>
    /// <remarks>
    /// The index MUST NOT be committed. It is derived, it is binary, and git
    /// would present two machines' indexes as a conflict with no correct
    /// resolution - which is exactly the failure the log layout exists to
    /// avoid, reintroduced through the back door.
    /// </remarks>
    public const string GitIgnore = """
        # Derived, not memory. Rebuilt from the logs on demand.
        index.*.db
        index.*.db-wal
        index.*.db-shm

        # This machine's name for itself. Per-machine by definition - sharing it
        # would put two machines back in one log.
        .machine-id

        # How worn the paths are HERE. What was decided is shared; how often
        # somebody reached for it on this machine is not, and syncing it would
        # put one machine's habits in charge of what another finds easy to
        # bring to mind.
        wear.*.json
        wear.*.json.tmp
        """;

    /// <summary>Write the .gitignore if it is not already there.</summary>
    public void EnsureGitIgnore()
    {
        var file = System.IO.Path.Combine(Path, ".gitignore");
        if (!File.Exists(file)) File.WriteAllText(file, GitIgnore);
    }

    // ------------------------------------------------------------------
    // Machine identity
    // ------------------------------------------------------------------

    /// <summary>
    /// A name for this machine that is stable across sessions.
    /// </summary>
    /// <remarks>
    /// The host name, prefixed by platform so a machine that gets renamed or a
    /// pair that share a name still land in different files. A collision here
    /// does not corrupt anything - both machines simply append to one file,
    /// which is the merge problem back again - so it is worth the prefix.
    /// </remarks>
    /// <param name="host">
    /// What this machine calls itself, or null to ask the environment. Passed
    /// in by tests, which cannot make Windows answer "localhost".
    /// </param>
    public static string DefaultMachineName(string? host = null)
    {
        var platform =
            OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsMacOS()   ? "mac"     :
            OperatingSystem.IsLinux()   ? "linux"   :
            OperatingSystem.IsAndroid() ? "android" : "other";

        if (host is null)
        {
            try { host = Environment.MachineName; }
            catch { host = ""; }
        }

        // "localhost" is what every Android device answers, and an empty or
        // unknown name is no better. Say so plainly and let the caller settle it.
        if (string.IsNullOrWhiteSpace(host) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return platform + Anonymous;

        return $"{platform}-{host}";
    }

    /// <summary>The marker a name carries when the host could not identify it.</summary>
    private const string Anonymous = "-unnamed";

    /// <summary>
    /// A short id for this install, made once and kept.
    /// </summary>
    /// <remarks>
    /// NOT SHARED, and gitignored for the same reason the index is: two machines
    /// that agreed on their id would be two writers on one file. A phone that
    /// pulls the folder fresh mints its own.
    /// </remarks>
    private string Installed()
    {
        var file = System.IO.Path.Combine(Path, ".machine-id");

        try
        {
            if (File.Exists(file) &&
                File.ReadAllText(file).Trim() is { Length: > 0 } existing)
                return Sanitise(existing);

            var minted = Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(file, minted);
            return minted;
        }
        catch (IOException)
        {
            // A read-only folder still has to work. It will not be stable
            // across runs, which is worse than a file and better than a
            // collision with every other device.
            return Guid.NewGuid().ToString("N")[..8];
        }
    }

    /// <summary>Safe for a filename on all three operating systems.</summary>
    private static string Sanitise(string name)
    {
        var cleaned = new string(name
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray());

        return cleaned.Trim('-') is { Length: > 0 } ok ? ok : "unknown";
    }
}
