// ConversationStore.cs
//
// Registering the store, once, for all three heads.
//
// An extension rather than three copies of the same AddFluxor call: the heads
// have drifted apart before - the Web.Client head missing a registration the
// server head had is exactly how a component crashes only after Blazor switches
// to WebAssembly - and a scan of the wrong assembly finds no reducers and fails
// silently, leaving a store that never changes and a screen that never updates.

using Fluxor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Samples.It.Shared.State;

public static class ConversationStore
{
    /// <summary>Adds the conversation store and its memory effects.</summary>
    /// <remarks>
    /// SCANS THIS ASSEMBLY, taken from a type in it rather than from
    /// <c>Assembly.GetExecutingAssembly()</c> — the executing assembly at the
    /// point this runs is the HEAD, not the shared library, and scanning the head
    /// finds none of the reducers or effects.
    /// <para>
    /// A head registers its own <see cref="IRemembers"/> BEFORE calling this and
    /// keeps it. One that does not gets <see cref="RemembersNothing"/>, which is
    /// the honest behaviour for a browser: nothing is remembered and nothing
    /// pretends to be. Registered here rather than left absent because Fluxor
    /// silently drops an effect whose dependencies cannot be resolved — the loop
    /// would simply not run, with nothing said about it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddConversationStore(this IServiceCollection services)
    {
        services.AddFluxor(o => o.ScanAssemblies(typeof(ConversationState).Assembly));
        services.TryAddSingleton<IRemembers, RemembersNothing>();
        return services;
    }
}
