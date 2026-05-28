using Circle.AI.Memory;

namespace Circle.AI.Simulation;

/// <summary>
/// Builds a <see cref="KnowledgeGraph"/> from a list of episodic memory entries.
/// </summary>
public interface IGraphBuilder
{
    /// <summary>
    /// Builds and returns a <see cref="KnowledgeGraph"/> extracted from the
    /// given <paramref name="entries"/>.
    /// </summary>
    /// <param name="entries">The episodic memory entries to process.</param>
    /// <returns>A populated <see cref="KnowledgeGraph"/>.</returns>
    KnowledgeGraph Build(IReadOnlyList<EpisodicMemoryEntry> entries);
}
