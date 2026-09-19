// ConsumerSkillStore.cs
//
// An in-memory ISkillStore whose search matches the way the shipped library does.
//
// NO LONGER THE CONSUMER PACK'S PRIMARY STORE. The pack now ships as a prebuilt
// SQLite database (see ConsumerSkillPack) and opens as a SqliteSkillStore. This
// class is kept for two jobs: the empty fallback ConsumerSkillPack.Load returns
// if that database cannot be unpacked, and an in-memory identifying store the
// tests use as a stand-in library. Its match semantics still mirror the library's
// identifyingMatchOnly, which is what makes it a faithful stand-in.
//
// WHY NOT JUST InMemorySkillStore. That store answers SearchAsync with a WHOLE
// STRING Contains - a query of "Help me build my CV" is looked for verbatim in a
// skill's fields, so a multi-word opener from a Services tile matches nothing.
// The SQLite library the app ships does the right thing: it splits the query
// into significant terms and matches each against a skill's NAME and TAGS only
// (identifyingMatchOnly). This store does the same, in memory, so a consumer
// skill is selected for a tile opener exactly as a library skill would be.
//
// IDENTIFYING MATCH ONLY, LIKE THE LIBRARY. A skill earns its place in the
// prompt by being ABOUT what was asked - its name and its tags - not by a word
// buried in its body. That is why every consumer SKILL.md carries the domain in
// its tags: work, rights, money, grant, bank. See SqliteSkillStore and
// SkillLibrary.Open, whose semantics this mirrors.

using System.Collections.Concurrent;

namespace CircleAI.Skills;

/// <summary>
/// Thread-safe in-memory <see cref="ISkillStore"/> whose search matches
/// significant query terms against skill NAME and TAGS (identifying match),
/// mirroring the shipped <see cref="SqliteSkillStore"/> with
/// <c>identifyingMatchOnly</c>. Populated at startup from parsed SKILL.md.
/// </summary>
public sealed class ConsumerSkillStore : ISkillStore
{
    private readonly ConcurrentDictionary<string, SkillDetail> _skills = new(StringComparer.Ordinal);

    /// <summary>Add or replace a skill. Synchronous — the store is in memory.</summary>
    public void Add(string? id, string name, string description, string instructions, IReadOnlyList<string> tags)
    {
        var effectiveId = string.IsNullOrWhiteSpace(id) ? InMemorySkillStore.GenerateSlug(name) : id.Trim();
        _skills[effectiveId] = new SkillDetail(
            Id: effectiveId,
            Name: name,
            Description: description,
            Instructions: instructions,
            Tags: tags ?? Array.Empty<string>(),
            Source: SkillSource.InMemory,
            LastModified: DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SkillSummary>>(
            _skills.Values
                .Select(ToSummary)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

    /// <inheritdoc />
    public Task<SkillDetail?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _skills.TryGetValue(id, out var detail);
        return Task.FromResult<SkillDetail?>(detail);
    }

    /// <inheritdoc />
    /// <remarks>
    /// SIGNIFICANT TERMS, NAME AND TAGS ONLY. Same two moves as the library:
    /// drop the function words that would match everything (see
    /// <see cref="CircleAI.Core.SearchTerms"/>), then a skill is a hit if any
    /// surviving term appears in its name or a tag. Ranked by how many terms hit,
    /// so the skill the opener is most about comes first for the top-5 cut in
    /// <see cref="SkillContextBuilder"/>.
    /// </remarks>
    public Task<IReadOnlyList<SkillSummary>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var terms = CircleAI.Core.SearchTerms.Significant(query);
        if (terms.Count == 0)
            return Task.FromResult<IReadOnlyList<SkillSummary>>(Array.Empty<SkillSummary>());

        var ranked = _skills.Values
            .Select(s => (skill: s, hits: TermHits(s, terms)))
            .Where(x => x.hits > 0)
            .OrderByDescending(x => x.hits)
            .ThenBy(x => x.skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => ToSummary(x.skill))
            .ToList();

        return Task.FromResult<IReadOnlyList<SkillSummary>>(ranked);
    }

    /// <inheritdoc />
    public Task<SkillDetail> UpsertAsync(string? id, SkillDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Add(id, draft.Name, draft.Description, draft.Instructions, draft.Tags ?? Array.Empty<string>());
        var effectiveId = string.IsNullOrWhiteSpace(id) ? InMemorySkillStore.GenerateSlug(draft.Name) : id.Trim();
        return Task.FromResult(_skills[effectiveId]);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _skills.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    private static int TermHits(SkillDetail s, IReadOnlyList<string> terms)
    {
        var hits = 0;
        foreach (var t in terms)
            if (s.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase)))
                hits++;
        return hits;
    }

    private static SkillSummary ToSummary(SkillDetail d) =>
        new(d.Id, d.Name, d.Description, d.Tags, d.Source);
}
