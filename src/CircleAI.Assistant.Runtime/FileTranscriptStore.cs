// FileTranscriptStore.cs
//
// A transcript per file, under the app's own storage.
//
// WHY A FOLDER AND NOT A DATABASE. A transcript is a whole document read whole,
// the corpus is one person's own recordings, and a file each means somebody can
// copy it off the phone, read it, or delete it with a file manager. A schema
// would be a second thing to migrate for a benefit nobody asked for.
//
// NOTHING LEAVES THE DEVICE. The path handed in is app-private storage. There is
// no sync, no backup and no upload, and forgetting one deletes the file. That is
// the product's posture, not a setting.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleAI.Assistant.Voice;

/// <summary>Keeps transcripts as JSON files in a folder.</summary>
public sealed class FileTranscriptStore : IKeepsTranscripts
{
    private const string Extension = ".transcript.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;

    /// <param name="folder">
    /// Where transcripts live. Created on first write, not in the constructor -
    /// a store that is only ever read from should not leave an empty directory
    /// behind on a phone that never recorded anything.
    /// </param>
    public FileTranscriptStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = folder;
    }

    /// <inheritdoc />
    public async Task<string> KeepAsync(
        string title, Transcript transcript, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        // NOTHING WORTH KEEPING IS NOT AN ERROR. A recording of silence
        // transcribes to an empty string, and writing a file for it would leave
        // somebody with a list of blank rows they have to clear out by hand.
        if (string.IsNullOrWhiteSpace(transcript.Text)) return string.Empty;

        Directory.CreateDirectory(_folder);

        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var saved = new SavedTranscript(
            id,
            string.IsNullOrWhiteSpace(title) ? "Recording" : title.Trim(),
            DateTimeOffset.UtcNow,
            transcript);

        // WRITTEN BESIDE AND MOVED INTO PLACE. A phone is killed mid-write far
        // more often than a desktop, and a half-written JSON file would come
        // back as a corrupt transcript that fails to parse for ever. The move is
        // atomic on every filesystem this runs on.
        var final = Path.Combine(_folder, id + Extension);
        var scratch = final + ".writing";

        await File.WriteAllTextAsync(scratch, JsonSerializer.Serialize(saved, Json), ct)
            .ConfigureAwait(false);
        File.Move(scratch, final, overwrite: true);

        return id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedTranscript>> AllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_folder)) return [];

        var kept = new List<SavedTranscript>();

        foreach (var path in Directory.EnumerateFiles(_folder, "*" + Extension))
        {
            ct.ThrowIfCancellationRequested();

            var one = await ReadAsync(path, ct).ConfigureAwait(false);
            if (one is not null) kept.Add(one);
        }

        return [.. kept.OrderByDescending(t => t.When)];
    }

    /// <inheritdoc />
    public Task<SavedTranscript?> GetAsync(string id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        return path is null ? Task.FromResult<SavedTranscript?>(null) : ReadAsync(path, ct);
    }

    /// <inheritdoc />
    public Task<bool> ForgetAsync(string id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (path is null || !File.Exists(path)) return Task.FromResult(false);

        try
        {
            File.Delete(path);
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>The file for an id, or null when the id is not one we would write.</summary>
    /// <remarks>
    /// NO PATH SEPARATORS AND NO DOTS. An id arrives from a caller, and a caller
    /// that passes "../../secrets" must not be able to read or delete a file
    /// outside this folder. Ours are date-and-guid, so anything containing a
    /// separator is not one of ours and is refused rather than sanitised.
    /// </remarks>
    private string? PathFor(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.IndexOfAny(['/', '\\', ':']) >= 0) return null;
        if (id.Contains("..", StringComparison.Ordinal)) return null;

        return Path.Combine(_folder, id + Extension);
    }

    /// <summary>Read one, or null when it cannot be read.</summary>
    /// <remarks>
    /// A FILE THAT WILL NOT PARSE IS SKIPPED, NOT THROWN. One corrupt transcript
    /// - a phone killed at exactly the wrong moment before the atomic write was
    /// added, a file copied in by hand - must not make the whole list
    /// unreadable. The rest of somebody's recordings are still theirs.
    /// </remarks>
    private static async Task<SavedTranscript?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SavedTranscript>(json, Json);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
