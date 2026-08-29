// MemoryServiceCollectionExtensions.cs
//
// Registering the device's memory once, so every module can take it.
//
// ONE MEMORY PER DEVICE. Not one per app and certainly not one per feature: a
// second store would be a second set of facts about the same person, disagreeing
// quietly. Everything that wants continuity takes IMemoryService, or takes its
// own view of it through AddModuleMemory.
//
// THE RETENTION A MODULE DECLARES HERE IS THE GUARANTEE. It is code because it
// has to hold on a device whose memory was wiped, edited, or has never been
// written to - a prohibition that can be forgotten is not a prohibition. The
// memory records it too, so a person can see it and argue with it, but the
// declaration is what actually binds.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Memory;

/// <summary>Registers the device's memory.</summary>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// The one memory this device holds.
    /// </summary>
    /// <param name="services">Where to register it.</param>
    /// <param name="folderPath">
    /// Where it lives. The app's own storage on a phone; a directory inside a
    /// git repository on a machine that shares its memory with others.
    /// </param>
    /// <param name="machine">
    /// What this device calls itself, or null to work it out. On a phone that
    /// means minting an id, because every Android device answers "localhost"
    /// and two of them would otherwise write to the same log.
    /// </param>
    public static IServiceCollection AddCircleMemory(
        this IServiceCollection services, string folderPath, string? machine = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("A memory needs somewhere to live.", nameof(folderPath));

        // TryAdd: a host that registered its own memory keeps it. Replacing it
        // here would give one device two sets of facts about one person.
        services.TryAddSingleton<IMemoryService>(_ => new MemoryService(folderPath, machine));

        return services;
    }

    /// <summary>
    /// A module's own view of that memory.
    /// </summary>
    /// <param name="services">Where to register it.</param>
    /// <param name="module">
    /// What the module is called - "interpret", "career", "banking". Atoms it
    /// records are filed under it, so what a module remembered can be read and
    /// corrected rather than melting into one pile.
    /// </param>
    /// <param name="retention">
    /// What it may keep. RulesOnly for anything handling words that are not the
    /// owner's, and anything whose answer must be re-decided every time - a
    /// live interpreter, a safety gate, an authorisation.
    /// </param>
    /// <example>
    /// <code>
    /// services.AddCircleMemory(FileSystem.AppDataDirectory);
    /// services.AddModuleMemory("career");
    /// services.AddModuleMemory("interpret", MemoryRetention.RulesOnly);
    /// </code>
    /// </example>
    public static IServiceCollection AddModuleMemory(
        this IServiceCollection services, string module,
        MemoryRetention retention = MemoryRetention.Everything)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(module))
            throw new ArgumentException("A module has to say what it is.", nameof(module));

        // Keyed, because a host registers several of these and each one has to
        // resolve to its own module rather than to whichever was registered last.
        services.AddKeyedSingleton<IModuleMemory>(
            module.Trim().ToLowerInvariant(),
            (provider, key) => new ModuleMemory(
                provider.GetRequiredService<IMemoryService>(),
                (string)key!,
                retention));

        return services;
    }
}
