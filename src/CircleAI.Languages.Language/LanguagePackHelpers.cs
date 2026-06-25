// LanguagePackHelpers.cs — (3.3.0)
//
// Real helpers shared across language packs: pack registry, BCP-47
// matching, locale-hint merge.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Languages.Language;

public sealed class LanguagePackRegistry
{
    private readonly ConcurrentDictionary<string, ILanguagePack> _byTag = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ILanguagePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _byTag[pack.Metadata.BcpTag] = pack;
    }

    public ILanguagePack? GetByExactTag(string bcpTag)
        => string.IsNullOrWhiteSpace(bcpTag) ? null : _byTag.GetValueOrDefault(bcpTag);

    public ILanguagePack? GetByLanguage(string langPrefix)
    {
        if (string.IsNullOrWhiteSpace(langPrefix)) return null;
        var prefix = langPrefix.Split('-')[0];
        return _byTag.Values.FirstOrDefault(p =>
            p.Metadata.BcpTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ILanguagePack> ForRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region)) throw new ArgumentException("region required");
        return _byTag.Values.Where(p => p.Metadata.SpokenInRegions.Contains(region, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    public IReadOnlyList<string> AllTags() => _byTag.Keys.OrderBy(k => k).ToArray();
}

public static class LocaleHintMerge
{
    public static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> primary, IReadOnlyDictionary<string, string> secondary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        var merged = new Dictionary<string, string>(secondary, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in primary) merged[kv.Key] = kv.Value;
        return merged;
    }
}
