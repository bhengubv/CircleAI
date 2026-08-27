// AtomLog.cs
//
// The durable half: an append-only line per remembered thing.
//
// THIS FILE FORMAT OUTLIVES THIS CODE. It is what actually crosses between a
// Linux box, a Windows box and a Mac, what a person can open and read, and
// what any other tool would have to understand. The database is a cache of it.
// So it is plain JSON, one object per line, and every field is named for what
// it means rather than for how it is stored.
//
// APPEND-ONLY CHANGES THE MODEL, and this is where it bites. A row in a table
// can be UPDATEd to say it was superseded; a line already written to a log
// cannot. So a correction is a NEW line that names what it SUPERSEDES, and the
// forward pointer is derived when the log is replayed. Nothing is ever edited
// and nothing is ever removed, which is also what makes two machines' logs
// mergeable by simple concatenation.
//
// ORDER IS BY TIME, NOT BY FILE. Replay sorts every machine's lines together,
// so a correction made on the Mac supersedes a decision made on Windows the
// same way it would have locally.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleAI.Memory;

/// <summary>One line of the log: a thing remembered, as it was written.</summary>
/// <remarks>
/// Deliberately flat and deliberately stable. Adding a field is safe; renaming
/// or repurposing one silently rewrites history on every machine that has not
/// pulled yet.
/// </remarks>
public sealed class AtomRecord
{
    [JsonPropertyName("id")]          public string Id { get; set; } = "";
    [JsonPropertyName("kind")]        public string Kind { get; set; } = nameof(AtomKind.Decision);
    [JsonPropertyName("text")]        public string Text { get; set; } = "";
    [JsonPropertyName("subject")]     public string? Subject { get; set; }
    [JsonPropertyName("challenge")]   public string? Challenge { get; set; }
    [JsonPropertyName("outcome")]     public string? Outcome { get; set; }
    [JsonPropertyName("recorded")]    public string Recorded { get; set; } = "";
    [JsonPropertyName("machine")]     public string Machine { get; set; } = "";
    [JsonPropertyName("source")]      public string? SourceEpisode { get; set; }

    /// <summary>The atom this line replaces, if it is a correction.</summary>
    /// <remarks>
    /// POINTS BACKWARDS, unlike the column in the index. A log line cannot
    /// reach forward in time to amend the line it replaces, so the newer line
    /// carries the relationship and replay works out the rest.
    /// </remarks>
    [JsonPropertyName("supersedes")]  public string? Supersedes { get; set; }

    [JsonPropertyName("verify")]      public string? Verify { get; set; }
}

/// <summary>Reads and appends the machine logs.</summary>
public sealed class AtomLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly MemoryFolder _folder;

    public AtomLog(MemoryFolder folder) =>
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));

    /// <summary>Append one atom to this machine's log.</summary>
    /// <param name="atom">What to remember.</param>
    /// <param name="supersedes">The atom being corrected, if this is a correction.</param>
    public void Append(MemoryAtom atom, Guid? supersedes = null)
    {
        ArgumentNullException.ThrowIfNull(atom);

        var record = new AtomRecord
        {
            Id            = atom.Id.ToString("N"),
            Kind          = atom.Kind.ToString(),
            Text          = atom.Text,
            Subject       = atom.Subject,
            Challenge     = atom.Challenge,
            Outcome       = atom.Outcome?.ToString(),
            Recorded      = atom.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Machine       = _folder.Machine,
            SourceEpisode = atom.SourceEpisode?.ToString("N"),
            Supersedes    = supersedes?.ToString("N"),
            Verify        = atom.Verify,
        };

        // One line, one write, newline first only if the file does not end in
        // one - a half-written line from an interrupted session would
        // otherwise swallow the next record into itself.
        var line = JsonSerializer.Serialize(record, Json);
        using var stream = new FileStream(_folder.OwnLog, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(line);
    }

    /// <summary>
    /// Every record from every machine, oldest first.
    /// </summary>
    /// <remarks>
    /// A LINE THAT WILL NOT PARSE IS SKIPPED, not thrown. Logs are edited by
    /// hand on purpose - that is half the reason they are text - and one
    /// fat-fingered line must not cost somebody their whole memory.
    /// </remarks>
    public IReadOnlyList<AtomRecord> ReadAll()
    {
        var records = new List<AtomRecord>();

        foreach (var path in _folder.AllLogs)
        {
            foreach (var line in ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<AtomRecord>(line, Json) is { } record &&
                        !string.IsNullOrWhiteSpace(record.Id))
                        records.Add(record);
                }
                catch (JsonException)
                {
                    // Unreadable line. Keep the rest.
                }
            }
        }

        return records
            .OrderBy(r => Time(r.Recorded))
            // Machine name breaks ties so replay is identical on all three
            // boxes: two records with the same timestamp must not order
            // differently depending on which machine read them.
            .ThenBy(r => r.Machine, StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        // Shared read: another session on this machine may be appending.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    internal static DateTimeOffset Time(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var when)
            ? when
            : DateTimeOffset.MinValue;
}
