// SqliteKnowledgeGraph.cs
//
// (Phase E2) Personal knowledge graph backed by a SQLite triple store.
// Each row is one (subject, predicate, object) triple with provenance
// (source episode id + confidence). Implements IPersonalKnowledgeGraph
// from the HER/Jarvis surface so the real graph slots in where
// AdjacencyPersonalKnowledgeGraph was.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;
using Microsoft.Data.Sqlite;

namespace CircleAI.Companion;

/// <summary>(Phase E2) One (s, p, o) triple as stored. Carries provenance.</summary>
public sealed record KnowledgeTriple(
    string         Subject,
    string         Predicate,
    string         Object,
    string?        Source,
    float          Confidence,
    DateTimeOffset RecordedAtUtc);

/// <summary>(Phase E2) SQLite-backed personal knowledge graph.</summary>
public sealed class SqliteKnowledgeGraph : IPersonalKnowledgeGraph, IDisposable
{
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public SqliteKnowledgeGraph(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("connectionString required");
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS kg_nodes (
                id          TEXT PRIMARY KEY NOT NULL,
                kind        TEXT NOT NULL,
                name        TEXT NOT NULL,
                properties  TEXT
            );
            CREATE TABLE IF NOT EXISTS kg_triples (
                subject     TEXT NOT NULL,
                predicate   TEXT NOT NULL,
                object      TEXT NOT NULL,
                source      TEXT,
                confidence  REAL NOT NULL,
                recorded_at TEXT NOT NULL,
                PRIMARY KEY (subject, predicate, object)
            );
            CREATE INDEX IF NOT EXISTS ix_kg_triples_subject ON kg_triples (subject);
            CREATE INDEX IF NOT EXISTS ix_kg_triples_object  ON kg_triples (object);
            CREATE INDEX IF NOT EXISTS ix_kg_triples_predicate ON kg_triples (predicate);
            """;
        cmd.ExecuteNonQuery();
    }

    public ValueTask UpsertNodeAsync(KnowledgeNode node, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(node);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO kg_nodes (id, kind, name, properties)
            VALUES ($id, $kind, $name, $props);
            """;
        cmd.Parameters.AddWithValue("$id",   node.Id);
        cmd.Parameters.AddWithValue("$kind", node.Kind);
        cmd.Parameters.AddWithValue("$name", node.Name);
        cmd.Parameters.AddWithValue("$props", (object?)SerialiseProps(node.Properties) ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask UpsertRelationAsync(KnowledgeRelation rel, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(rel);
        AddTriple(rel.FromId, rel.Relation, rel.ToId, source: null, confidence: 1.0f);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<KnowledgeNode>> NeighboursAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT n.id, n.kind, n.name, n.properties
              FROM kg_triples t
              JOIN kg_nodes   n ON n.id = t.object
             WHERE t.subject = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        var hits = new List<KnowledgeNode>();
        while (reader.Read())
        {
            hits.Add(new KnowledgeNode(
                Id:         reader.GetString(0),
                Kind:       reader.GetString(1),
                Name:       reader.GetString(2),
                Properties: DeserialiseProps(reader.IsDBNull(3) ? null : reader.GetString(3))));
        }
        return ValueTask.FromResult<IReadOnlyList<KnowledgeNode>>(hits);
    }

    /// <summary>(Phase E2) Add a triple with full provenance.</summary>
    public void AddTriple(string subject, string predicate, string @object, string? source, float confidence)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(subject))   throw new ArgumentException("subject required");
        if (string.IsNullOrWhiteSpace(predicate)) throw new ArgumentException("predicate required");
        if (string.IsNullOrWhiteSpace(@object))   throw new ArgumentException("object required");
        if (confidence < 0 || confidence > 1)     throw new ArgumentOutOfRangeException(nameof(confidence));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO kg_triples (subject, predicate, object, source, confidence, recorded_at)
            VALUES ($s, $p, $o, $src, $conf, $ts);
            """;
        cmd.Parameters.AddWithValue("$s",    subject);
        cmd.Parameters.AddWithValue("$p",    predicate);
        cmd.Parameters.AddWithValue("$o",    @object);
        cmd.Parameters.AddWithValue("$src",  (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conf", confidence);
        cmd.Parameters.AddWithValue("$ts",   DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>(Phase E2) Read raw triples for a subject (use for inspection / debugging).</summary>
    public IReadOnlyList<KnowledgeTriple> ReadTriples(string subject)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("subject required");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT subject, predicate, object, source, confidence, recorded_at
              FROM kg_triples
             WHERE subject = $s;
            """;
        cmd.Parameters.AddWithValue("$s", subject);
        using var reader = cmd.ExecuteReader();
        var hits = new List<KnowledgeTriple>();
        while (reader.Read())
        {
            hits.Add(new KnowledgeTriple(
                Subject:       reader.GetString(0),
                Predicate:     reader.GetString(1),
                Object:        reader.GetString(2),
                Source:        reader.IsDBNull(3) ? null : reader.GetString(3),
                Confidence:    (float)reader.GetDouble(4),
                RecordedAtUtc: DateTimeOffset.Parse(reader.GetString(5))));
        }
        return hits;
    }

    /// <summary>(Phase D5) All triples — used by HippoRAG for graph walks.</summary>
    public IReadOnlyList<KnowledgeTriple> AllTriples()
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT subject, predicate, object, source, confidence, recorded_at FROM kg_triples;";
        using var reader = cmd.ExecuteReader();
        var hits = new List<KnowledgeTriple>();
        while (reader.Read())
        {
            hits.Add(new KnowledgeTriple(
                Subject:       reader.GetString(0),
                Predicate:     reader.GetString(1),
                Object:        reader.GetString(2),
                Source:        reader.IsDBNull(3) ? null : reader.GetString(3),
                Confidence:    (float)reader.GetDouble(4),
                RecordedAtUtc: DateTimeOffset.Parse(reader.GetString(5))));
        }
        return hits;
    }

    public KnowledgeNode? GetNode(string id)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, name, properties FROM kg_nodes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new KnowledgeNode(
            Id:         reader.GetString(0),
            Kind:       reader.GetString(1),
            Name:       reader.GetString(2),
            Properties: DeserialiseProps(reader.IsDBNull(3) ? null : reader.GetString(3)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Close();
        _conn.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static string? SerialiseProps(IReadOnlyDictionary<string, string>? props)
    {
        if (props is null || props.Count == 0) return null;
        return System.Text.Json.JsonSerializer.Serialize(props);
    }

    private static IReadOnlyDictionary<string, string> DeserialiseProps(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, string>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        catch { return new Dictionary<string, string>(); }
    }
}
