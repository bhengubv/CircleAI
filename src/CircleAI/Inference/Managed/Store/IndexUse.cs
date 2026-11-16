// IndexUse.cs
//
// WHAT THE PERSON USES, KEPT ON FLASH. The owner, 2026-11-12: "The more you use it the more it learns what the user
// needs" (what to keep in RAM - the warm cached index). Every word falls in a thought group at every layer
// (IndexedFeedForward counts them); those counts are added here as turns end, so they outlive the engine being let go and
// the process going. At night Revise orders the cached portion by them, and RAM takes the hottest of that portion first.
//
// LATELY COUNTS FOR MORE. Each night halves what the nights before it left and adds the day's words, so a subject dropped
// a week ago gives way to this week's.
//
// Layout, little-endian: magic "CAIUSE01", the groups counted, when it was last revised (UTC ticks), the older use a
// group, the use since the last revision a group.

using System.Buffers.Binary;

namespace CircleAI.Inference.Managed.Store;

/// <summary>The use of an index's groups over the person's days, on flash (<see cref="IndexUse"/>'s layout).</summary>
public sealed class IndexUse
{
    private const ulong Magic = 0x3130_4553_5549_4143; // "CAIUSE01"
    private const int HeaderBytes = 24;
    private readonly long[] _older;   // what the nights before left: halved each night
    private readonly long[] _since;   // words a group since the last revision

    private IndexUse(string path, long[] older, long[] since, DateTimeOffset lastRevised)
    {
        FilePath = path;
        _older = older;
        _since = since;
        LastRevised = lastRevised;
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Groups counted: layers times groups a layer.</summary>
    public int Groups => _since.Length;

    /// <summary>When the cached portion was last revised from this; the earliest time there is when it never was.</summary>
    public DateTimeOffset LastRevised { get; private set; }

    /// <summary>Words counted since the last revision.</summary>
    public long Since => _since.Sum();

    /// <summary>
    /// The use at <paramref name="path"/> of an index with <paramref name="groups"/> groups (layers times groups a layer);
    /// nothing used yet when there is no file, or it counts another index's groups.
    /// </summary>
    public static IndexUse Open(string path, int groups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(groups, 1);
        try
        {
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == HeaderBytes + 2L * groups * sizeof(long) && BinaryPrimitives.ReadUInt64LittleEndian(bytes) == Magic
                    && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) == groups)
                {
                    var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12));
                    var older = new long[groups];
                    var since = new long[groups];
                    for (var i = 0; i < groups; i++)
                    {
                        older[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(HeaderBytes + i * sizeof(long)));
                        since[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(HeaderBytes + (groups + i) * sizeof(long)));
                    }
                    if (ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks)
                        return new IndexUse(path, older, since, new DateTimeOffset(ticks, TimeSpan.Zero));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: what was used is learnt again.
        }
        return new IndexUse(path, new long[groups], new long[groups], DateTimeOffset.MinValue);
    }

    /// <summary>Adds the <paramref name="words"/> that fell in each group (<see cref="IndexedFeedForward.TakeUsage"/>) and writes the file.</summary>
    public void Add(IReadOnlyList<long> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count != _since.Length) throw new ArgumentException($"The use is a count a group, {_since.Length} of them, not {words.Count}.", nameof(words));
        var any = false;
        for (var i = 0; i < _since.Length; i++)
        {
            if (words[i] <= 0) continue;
            _since[i] += words[i];
            any = true;
        }
        if (any) Save();
    }

    /// <summary>What Revise orders the groups by: half of what the nights before left, and every word since.</summary>
    public long[] Ranking()
    {
        var ranking = new long[_since.Length];
        for (var i = 0; i < ranking.Length; i++) ranking[i] = _older[i] / 2 + _since[i];
        return ranking;
    }

    /// <summary>The night's revision is done at <paramref name="now"/>: the ranking becomes what the nights left, and the count since starts again.</summary>
    public void Revised(DateTimeOffset now)
    {
        var ranking = Ranking();
        ranking.CopyTo(_older, 0);
        Array.Clear(_since);
        LastRevised = now.ToUniversalTime();
        Save();
    }

    // To a file of its own first, then into place. A phone that cannot write it keeps the count in RAM: the use is a
    // courtesy to tomorrow, never worth failing a turn for.
    private void Save()
    {
        try
        {
            var bytes = new byte[HeaderBytes + 2 * _since.Length * sizeof(long)];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), _since.Length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), LastRevised.UtcTicks);
            for (var i = 0; i < _since.Length; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(HeaderBytes + i * sizeof(long)), _older[i]);
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(HeaderBytes + (_since.Length + i) * sizeof(long)), _since[i]);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var partial = FilePath + ".partial";
            File.WriteAllBytes(partial, bytes);
            File.Move(partial, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in RAM only.
        }
    }
}
