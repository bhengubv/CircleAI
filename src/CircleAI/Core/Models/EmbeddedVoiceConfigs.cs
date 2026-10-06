#nullable enable

// EmbeddedVoiceConfigs.cs
//
// The MMS voices' `model.onnx.json` sidecars, carried inside the assembly.
//
// WHY THEY ARE NOT DOWNLOADED. The registry pins each sidecar as a remote bundle
// file with a SHA-256, exactly like the 114 MB model beside it. Measured
// 2026-08-23, 43 of the 47 returned 404: they were generated once by a script
// that was never committed and the bytes were then lost, so the registry was
// promising files that no longer existed anywhere. Every one of those voices
// downloaded its model and its tokens.txt and then failed on a 2 KB sidecar.
//
// The whole set is 91 KB — smaller than one app icon, and it is our own work
// product rather than an upstream artefact. Shipping it in the assembly removes
// the failure permanently: there is no address to go stale, no credential
// needed to publish it, and nothing extra for a user to install.
//
// The SHA in the registry still governs. `ModelDownloadService` writes the
// embedded bytes to the bundle directory and then runs the ordinary
// verify-then-skip path over them, so a sidecar that does not match its pin
// fails the same way a corrupt download would.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CircleAI.Core.Models;

/// <summary>
/// Small voice bundle files compiled into this assembly, keyed by the bundle-file
/// name the registry uses (e.g. <c>mms-swh/model.onnx.json</c>).
/// </summary>
public static class EmbeddedVoiceConfigs
{
    private const string Prefix = "CircleAI.Core.Models.VoiceConfigs.";

    /// <summary>
    /// The small companion files a voice bundle needs beside its model. Only
    /// these names are recognised, so a resource added by accident cannot start
    /// answering for a bundle file nobody meant to embed.
    /// </summary>
    /// <remarks>
    /// <c>language_ids.json</c> is 157 bytes and load-bearing: a multilingual
    /// voice has to be TOLD its language id, and without the file every South
    /// African language synthesises as Afrikaans. Its bundle was published with
    /// the model alone, so the int8 variant was missing both companions.
    /// </remarks>
    private static readonly string[] Companions =
    {
        ".model.onnx.json",
        ".language_ids.json",
    };

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Map = new(Build);

    /// <summary>
    /// Bundle-file names this assembly can satisfy without a network call.
    /// </summary>
    public static IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)Map.Value.Keys;

    /// <summary>
    /// The bytes for <paramref name="bundleFileName"/>, or null when this
    /// assembly carries no sidecar for it.
    /// </summary>
    /// <param name="bundleFileName">
    /// As the registry spells it — <c>mms-swh/model.onnx.json</c>. Backslashes
    /// are accepted too, because a caller that has already been through
    /// <see cref="Path"/> may hand over a platform-separated name.
    /// </param>
    public static byte[]? TryGet(string? bundleFileName)
    {
        if (string.IsNullOrWhiteSpace(bundleFileName)) return null;

        var key = bundleFileName.Replace('\\', '/');
        if (!Map.Value.TryGetValue(key, out var resource)) return null;

        using var stream = typeof(EmbeddedVoiceConfigs).Assembly
            .GetManifestResourceStream(resource);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IReadOnlyDictionary<string, string> Build()
    {
        // Resource names arrive as "CircleAI.Core.Models.VoiceConfigs.mms-swh.model.onnx.json".
        // The voice id is what sits between the prefix and the suffix — taken by
        // trimming both ends rather than by splitting on '.', because the voice
        // ids themselves contain no dots today but the suffix certainly does.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in typeof(EmbeddedVoiceConfigs).Assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            foreach (var companion in Companions)
            {
                if (!name.EndsWith(companion, StringComparison.Ordinal)) continue;

                var voice = name[Prefix.Length..^companion.Length];
                if (voice.Length == 0) continue;

                map[$"{voice}/{companion[1..]}"] = name;
                break;
            }
        }

        return map;
    }
}
