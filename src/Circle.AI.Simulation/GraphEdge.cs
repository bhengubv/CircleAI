namespace Circle.AI.Simulation;

/// <summary>
/// A directed, weighted edge between two <see cref="GraphNode"/> instances.
/// Fixture-validated.
/// </summary>
public sealed record GraphEdge(
    Guid   Id,
    Guid   SourceId,
    Guid   TargetId,
    string Relation,    // e.g. "mentions", "causes", "resolves", "depends_on"
    float  Weight,      // 0.0–1.0; strength of the relationship
    DateTimeOffset CreatedAt
)
{
    /// <summary>
    /// Creates a new <see cref="GraphEdge"/> with a generated <see cref="Guid"/> ID
    /// and the current UTC timestamp. The <paramref name="weight"/> is clamped to [0.0, 1.0].
    /// </summary>
    /// <param name="sourceId">The ID of the source node.</param>
    /// <param name="targetId">The ID of the target node.</param>
    /// <param name="relation">The relationship label (e.g. "mentions", "causes", "tagged_with").</param>
    /// <param name="weight">Strength of the relationship; clamped to [0.0, 1.0].</param>
    /// <returns>A new <see cref="GraphEdge"/> instance.</returns>
    public static GraphEdge Create(Guid sourceId, Guid targetId, string relation, float weight = 1.0f) =>
        new(Guid.NewGuid(), sourceId, targetId, relation,
            Math.Clamp(weight, 0f, 1f), DateTimeOffset.UtcNow);
}
