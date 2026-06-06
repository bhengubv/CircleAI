// NativeRuntimeRegistry.cs
//
// In-process registry of pre-built MNN runtime bundles. Loaded from
// NativeRuntimes/embedded_native_registry.json at process start. Pattern
// mirrors CircleAI.Core.Models.embedded_registry.json — non-object entries
// (notes, headers) are tolerated to keep the file human-editable.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Runtime.NativeRuntimes;

/// <summary>
/// Loads the embedded native-runtime registry and exposes lookup by tuple.
/// </summary>
public sealed class NativeRuntimeRegistry
{
    private readonly IReadOnlyList<NativeRuntimeBundle> _bundles;

    /// <summary>
    /// Load the registry from <c>embedded_native_registry.json</c> shipped
    /// inside the assembly.
    /// </summary>
    public static NativeRuntimeRegistry LoadEmbedded()
    {
        var asm = typeof(NativeRuntimeRegistry).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("embedded_native_registry.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "embedded_native_registry.json is not present in CircleAI.Runtime.dll. " +
                "Check the csproj <EmbeddedResource Include=> directive.");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Failed to open embedded resource '{name}'.");

        return LoadFromStream(stream);
    }

    /// <summary>
    /// Load from an explicit JSON stream. Useful for tests.
    /// </summary>
    public static NativeRuntimeRegistry LoadFromStream(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var list = new List<NativeRuntimeBundle>();

        if (!doc.RootElement.TryGetProperty("mnn_versions", out var versions))
            return new NativeRuntimeRegistry(list);

        foreach (var versionEntry in versions.EnumerateArray())
        {
            if (versionEntry.ValueKind != JsonValueKind.Object) continue;
            if (!versionEntry.TryGetProperty("version", out var v)
             || !versionEntry.TryGetProperty("bundles", out var bundlesArr)) continue;

            var mnnVersion = v.GetString() ?? "";
            foreach (var b in bundlesArr.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                if (!TryParseBundle(mnnVersion, b, out var bundle)) continue;
                list.Add(bundle);
            }
        }
        return new NativeRuntimeRegistry(list);
    }

    private static bool TryParseBundle(
        string mnnVersion, JsonElement b, out NativeRuntimeBundle bundle)
    {
        bundle = null!;

        if (!b.TryGetProperty("os",       out var osEl)) return false;
        if (!b.TryGetProperty("arch",     out var archEl)) return false;
        if (!b.TryGetProperty("backend",  out var backendEl)) return false;
        if (!b.TryGetProperty("url",      out var urlEl)) return false;

        if (!Enum.TryParse<OperatingSystemKind>(osEl.GetString(), ignoreCase: true, out var os)) return false;
        if (!Enum.TryParse<ArchitectureKind>(archEl.GetString(), ignoreCase: true, out var arch)) return false;
        if (!Enum.TryParse<BackendKind>(backendEl.GetString(), ignoreCase: true, out var backend)) return false;
        if (!Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var primaryUri)) return false;

        Uri? fallback = null;
        if (b.TryGetProperty("fallback_url", out var fbEl)
            && Uri.TryCreate(fbEl.GetString(), UriKind.Absolute, out var fbUri))
            fallback = fbUri;

        var sha = b.TryGetProperty("sha256", out var shaEl) ? shaEl.GetString() : null;

        // mnnbridge is the CircleAI shim — NOT shipped in Alibaba's bundle.
        // The bundle only carries MNN. mnnbridge resolution is handled
        // separately by CircleAI.Inference.NativeLibraryResolver via the
        // SDK's runtimes/{RID}/native/ fallback paths.
        //
        // For macOS / iOS, the framework binary is named just "MNN" (no
        // prefix or extension); the fetcher recognises the framework
        // layout and finds it under MNN.framework/Versions/<v>/MNN.
        var coreLib   = b.TryGetProperty("mnn_lib", out var mlEl)
            ? mlEl.GetString() ?? DefaultCoreLibName(os)
            : DefaultCoreLibName(os);

        bundle = new NativeRuntimeBundle(
            mnnVersion, os, arch, backend, primaryUri, fallback, sha, coreLib);
        return true;
    }

    private static string DefaultCoreLibName(OperatingSystemKind os) => os switch
    {
        OperatingSystemKind.Windows => "MNN.dll",
        OperatingSystemKind.MacOS or OperatingSystemKind.IOS => "MNN",
        _ => "libMNN.so",
    };

    private NativeRuntimeRegistry(IReadOnlyList<NativeRuntimeBundle> bundles) => _bundles = bundles;

    /// <summary>All loaded bundles.</summary>
    public IReadOnlyList<NativeRuntimeBundle> All => _bundles;

    /// <summary>
    /// Look up the newest bundle matching (<paramref name="os"/>, <paramref name="arch"/>,
    /// <paramref name="backend"/>). When several MNN versions are registered for the same
    /// tuple, the highest version string wins (string sort — bundle authors should use
    /// semantic version strings).
    /// </summary>
    public NativeRuntimeBundle? Find(
        OperatingSystemKind os, ArchitectureKind arch, BackendKind backend) =>
        _bundles
            .Where(b => b.Os == os && b.Arch == arch && b.Backend == backend)
            .OrderByDescending(b => b.MnnVersion, StringComparer.Ordinal)
            .FirstOrDefault();
}
