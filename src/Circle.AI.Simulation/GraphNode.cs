namespace Circle.AI.Simulation;

/// <summary>
/// A node in the Circle AI knowledge graph. Represents any entity
/// extracted from episodic memory — person, topic, app, system event.
/// This type is fixture-validated: field names and types must not change
/// without updating fixtures/graph_schema.json.
/// </summary>
public sealed record GraphNode(
    Guid   Id,
    string Label,                                        // canonical entity label
    string Kind,                                         // "person" | "topic" | "app" | "event" | "system"
    IReadOnlyDictionary<string, string> Properties,     // arbitrary key-value metadata
    DateTimeOffset ExtractedAt
)
{
    /// <summary>
    /// Creates a new <see cref="GraphNode"/> with a generated <see cref="Guid"/> ID
    /// and the current UTC timestamp.
    /// </summary>
    /// <param name="label">The canonical entity label.</param>
    /// <param name="kind">The entity kind: "person", "topic", "app", "event", or "system".</param>
    /// <param name="properties">Optional arbitrary key-value metadata.</param>
    /// <returns>A new <see cref="GraphNode"/> instance.</returns>
    public static GraphNode Create(string label, string kind,
        IReadOnlyDictionary<string, string>? properties = null) =>
        new(Guid.NewGuid(), label, kind,
            properties ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);
}
