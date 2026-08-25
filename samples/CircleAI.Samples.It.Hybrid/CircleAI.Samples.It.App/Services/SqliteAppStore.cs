// SqliteAppStore.cs
//
// Everything the app remembers, in ONE file.
//
// WHY NOT Preferences. That was the first attempt and it was wrong: state ended
// up scattered across four mechanisms - SharedPreferences XML for settings, a
// SQLite database for the CV, installed.json manifests for models, and the model
// files themselves. Four stores is four things to back up, four to restore, and
// four to lose. It is the reason setting the phone up again means setting ALL of
// it up again.
//
// One SQLite file can be copied off the device, sideloaded onto another, restored
// after a reinstall, and inspected when something is wrong. The project already
// works this way - SqliteCareerStore, SqliteKnowledgeGraph, SqliteEpisodicStore,
// SqliteGoalStore - and settings had no business being the exception.

using Microsoft.Data.Sqlite;

namespace CircleAI.Samples.It.App.Services;

/// <summary>Key-value state for the app, kept in SQLite beside everything else.</summary>
public sealed class SqliteAppStore : IDisposable
{
    private readonly SqliteConnection _db;
    private bool _disposed;

    /// <summary>Opens (and creates) the store at a path.</summary>
    /// <param name="databasePath">A file under the app's storage.</param>
    public SqliteAppStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();

        Execute("""
            -- KEY-VALUE, DELIBERATELY, unlike the career store's real schema.
            -- These are settings: a fixed set of scalars that changes shape every
            -- time somebody adds a toggle. A column per setting means a migration
            -- per toggle, and a migration nobody writes is a setting that silently
            -- resets.
            CREATE TABLE IF NOT EXISTS setting (
                key   TEXT PRIMARY KEY NOT NULL,
                value TEXT
            );
            """);
    }

    /// <summary>The stored value, or <paramref name="fallback"/>.</summary>
    public string? Get(string key, string? fallback = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM setting WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string ?? fallback;
    }

    /// <summary>Store a value, or remove it when null.</summary>
    public void Set(string key, string? value)
    {
        using var cmd = _db.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM setting WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
        }
        else
        {
            // Upsert, so a setting written twice does not throw on the second.
            cmd.CommandText = """
                INSERT INTO setting (key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>Convenience for the several boolean settings.</summary>
    public bool GetBool(string key, bool fallback)
        => bool.TryParse(Get(key), out var b) ? b : fallback;

    /// <summary>Convenience for the several boolean settings.</summary>
    public void SetBool(string key, bool value) => Set(key, value ? "true" : "false");

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _db.Dispose();
        _disposed = true;
    }
}
