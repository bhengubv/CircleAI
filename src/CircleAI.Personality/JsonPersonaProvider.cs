// JsonPersonaProvider.cs
//
// Reference IPersonaProvider implementation. Serialises each Persona to
// "{rootDir}/{userId}.persona.json" via System.Text.Json. Idempotent and
// thread-safe via a SemaphoreSlim per userId. Designed for single-process
// use (the underlying File.Move is atomic at the OS level).

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircleAI.Core.Components;
using CircleAI.Core.Validation;

namespace CircleAI.Personality;

/// <summary>
/// File-system <see cref="IPersonaProvider"/> that stores each persona as a
/// JSON document under a configured root directory.
/// </summary>
[CircleAIVerificationStatus(VerificationLevel.WireProven,
    Notes = "POSIX/Windows file system. Atomic write-then-rename. Per-userId SemaphoreSlim correctness verified for single-process. NOT multi-replica safe — concurrent writes from multiple host processes can race on disk.")]
public sealed class JsonPersonaProvider : CircleAIComponentBase, IPersonaProvider
{
    /// <inheritdoc />
    public override string ComponentName => "JsonPersonaProvider";

    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Creates a new provider rooted at <paramref name="rootDirectory"/>.
    /// The directory is created if it does not already exist.
    /// </summary>
    /// <param name="rootDirectory">Directory under which persona JSON files are stored.</param>
    public JsonPersonaProvider(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <inheritdoc />
    public Task<Persona?> GetAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return RunOperationAsync<Persona?>(
            "GetAsync",
            async () =>
            {
                var path = PersonaPath(userId);
                if (!File.Exists(path)) return null;

                var gate = LockFor(userId);
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await using var fs = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 4096, useAsync: true);

                    return await JsonSerializer
                        .DeserializeAsync<Persona>(fs, s_opts, ct)
                        .ConfigureAwait(false);
                }
                finally { gate.Release(); }
            },
            ct,
            uhidIdentityId: userId);
    }

    /// <inheritdoc />
    public Task<Persona> SaveAsync(string userId, Persona persona, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(persona);

        return RunOperationAsync(
            "SaveAsync",
            async () =>
            {
                var refreshed = persona with { UpdatedAt = DateTimeOffset.UtcNow };
                var target = PersonaPath(userId);
                var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

                var gate = LockFor(userId);
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await using (var fs = new FileStream(
                        tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 4096, useAsync: true))
                    {
                        await JsonSerializer.SerializeAsync(fs, refreshed, s_opts, ct)
                            .ConfigureAwait(false);
                    }

                    File.Move(tmp, target, overwrite: true);
                    return refreshed;
                }
                catch
                {
                    try { File.Delete(tmp); } catch { /* best effort */ }
                    throw;
                }
                finally { gate.Release(); }
            },
            ct,
            uhidIdentityId: userId);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return RunOperationAsync(
            "ExistsAsync",
            () => Task.FromResult(File.Exists(PersonaPath(userId))),
            ct,
            uhidIdentityId: userId);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Persona> ExportAllAsync(CancellationToken ct = default)
    {
        return RunStreamAsync<Persona>(
            "ExportAllAsync",
            c => EnumerateAllImpl(c),
            ct);
    }

    private async IAsyncEnumerable<Persona> EnumerateAllImpl(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_rootDirectory)) yield break;

        foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*.persona.json"))
        {
            ct.ThrowIfCancellationRequested();
            Persona? persona;
            try
            {
                await using var fs = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, useAsync: true);

                persona = await JsonSerializer
                    .DeserializeAsync<Persona>(fs, s_opts, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Skip corrupted records during export rather than failing the whole stream.
                continue;
            }

            if (persona is not null) yield return persona;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private SemaphoreSlim LockFor(string userId) =>
        _locks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));

    private string PersonaPath(string userId)
    {
        var safe = string.Join("_", userId.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safe)) safe = "default";
        return Path.Combine(_rootDirectory, safe + ".persona.json");
    }
}
