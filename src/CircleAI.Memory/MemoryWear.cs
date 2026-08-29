// MemoryWear.cs
//
// How worn the path to each memory is, on THIS machine.
//
// WEAR IS LOCAL AND IT IS NOT MEMORY. What was decided is shared - it goes in
// the log, it travels by git, all three machines see it. How often somebody
// reached for it here is a different thing entirely: my use of a memory
// strengthens my access to it, not yours. Syncing wear would mean the Mac's
// habits deciding what the phone finds easy to bring to mind, which is not how
// anything works.
//
// So it lives beside the index, gitignored, per machine - and losing it costs
// familiarity rather than knowledge. Everything still recalls; it just recalls
// the way it did the first week.
//
// IT IS BUFFERED, because recall is the hot path. Marking a retrieval touches
// memory and nothing else; the file is written when somebody says so - at the
// end of a command, when an app is backgrounded. A crash costs the last few
// retrievals, which is usage data, not anything anybody said.
//
// IT CANNOT LEAVE A HALF-WRITTEN FILE. Written to a temporary name and moved
// into place, because the machine this matters most on is a phone that gets
// killed mid-write, and a corrupt wear file that threw would take the whole
// memory down with it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleAI.Memory;

/// <summary>What use one atom has had on this machine.</summary>
/// <param name="Retrievals">How many times it has been brought to mind here.</param>
/// <param name="LastRetrievedUtc">When that last happened.</param>
/// <param name="StabilityDays">How deeply it is learned, in days. Only grows.</param>
public sealed record MemoryTrace(
    int Retrievals,
    DateTimeOffset LastRetrievedUtc,
    double StabilityDays);

/// <summary>The local wear on a memory: what has been used, and how much it stuck.</summary>
public sealed class MemoryWear
{
    private readonly string? _path;
    private readonly Dictionary<Guid, MemoryTrace> _traces = new();
    private bool _dirty;

    /// <summary>Wear that is never written down. For a test, or a throwaway store.</summary>
    public MemoryWear() { }

    /// <param name="folder">The memory folder whose wear this is.</param>
    public MemoryWear(MemoryFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        _path = Path.Combine(folder.Path, $"wear.{folder.Machine}.json");
        Load();
    }

    /// <summary>How many atoms have been used here at all.</summary>
    public int Count => _traces.Count;

    /// <summary>What use this atom has had, or null if it has never been reached for.</summary>
    public MemoryTrace? For(Guid atom) => _traces.GetValueOrDefault(atom);

    /// <summary>How reachable this atom is right now.</summary>
    public double Reach(MemoryAtom atom, DateTimeOffset now) =>
        Forgetting.Reach(atom, For(atom.Id), now);

    /// <summary>Whether it has faded out of what recall offers.</summary>
    public bool Faded(MemoryAtom atom, DateTimeOffset now) =>
        Forgetting.Faded(atom, For(atom.Id), now);

    /// <summary>
    /// Record that this atom was brought to mind.
    /// </summary>
    /// <remarks>
    /// USE IS WHAT MAKES A MEMORY STICK. The gain is scaled by how nearly the
    /// thing had faded, so rescuing something at the edge is worth a great deal
    /// and asking the same question twice in a minute is worth almost nothing -
    /// otherwise anything asked often enough would become permanent whether or
    /// not it was ever in doubt.
    /// </remarks>
    public void Retrieved(MemoryAtom atom, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atom);

        var existing = For(atom.Id);
        var reach = Forgetting.Reach(atom, existing, now);

        _traces[atom.Id] = new MemoryTrace(
            Retrievals: (existing?.Retrievals ?? 0) + 1,
            LastRetrievedUtc: now,
            StabilityDays: Forgetting.Strengthened(
                existing?.StabilityDays ?? Forgetting.InitialStability(atom), reach));

        _dirty = true;
    }

    /// <summary>Record that several atoms were brought to mind together.</summary>
    public void Retrieved(IEnumerable<MemoryAtom> atoms, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        foreach (var atom in atoms) Retrieved(atom, now);
    }

    /// <summary>Forget that anything was ever used. Knowledge is untouched.</summary>
    public void Clear()
    {
        if (_traces.Count == 0) return;
        _traces.Clear();
        _dirty = true;
    }

    // ------------------------------------------------------------------
    // On disk
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,

        // Same reason as the log: the default encoder escapes anything outside
        // a conservative ASCII set, and a timestamp came out carrying an escape
        // for its own plus sign. Nothing here is ever put in a web page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One atom's wear, as it is written down.</summary>
    private sealed class Row
    {
        [JsonPropertyName("n")] public int Retrievals { get; set; }
        [JsonPropertyName("at")] public string At { get; set; } = "";
        [JsonPropertyName("s")] public double StabilityDays { get; set; }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;

        try
        {
            var rows = JsonSerializer.Deserialize<Dictionary<string, Row>>(
                File.ReadAllText(_path), Json);
            if (rows is null) return;

            foreach (var (id, row) in rows)
            {
                if (!Guid.TryParseExact(id, "N", out var atom)) continue;
                if (!DateTimeOffset.TryParse(row.At, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var when)) continue;

                _traces[atom] = new MemoryTrace(row.Retrievals, when, row.StabilityDays);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // WEAR IS NOT WORTH AN OUTAGE. A file somebody truncated, or one a
            // phone was killed halfway through writing, costs familiarity and
            // nothing else - so it starts again rather than taking the memory
            // down with it.
            _traces.Clear();
        }
    }

    /// <summary>Write it down, if anything changed.</summary>
    public void Flush()
    {
        if (!_dirty || _path is null) return;

        var rows = new Dictionary<string, Row>(_traces.Count);
        foreach (var (id, trace) in _traces)
            rows[id.ToString("N")] = new Row
            {
                Retrievals = trace.Retrievals,
                At = trace.LastRetrievedUtc.ToString("O", CultureInfo.InvariantCulture),
                StabilityDays = Math.Round(trace.StabilityDays, 3),
            };

        try
        {
            // Written aside and moved into place: the machine this matters on
            // is a phone that gets killed, and a half-written wear file would
            // be read back as damage.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(rows, Json));
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
        }
        catch (IOException)
        {
            // Somewhere read-only, or a folder that went away. The memory still
            // works; it simply will not remember having been used.
        }
    }
}
