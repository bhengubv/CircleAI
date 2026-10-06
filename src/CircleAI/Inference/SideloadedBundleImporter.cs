#nullable enable

// SideloadedBundleImporter.cs
//
// Takes a model somebody copied onto the phone and makes it a first-class
// installed model — or refuses it, loudly, with a reason.
//
// WHY THIS IS A FEATURE AND NOT A DEVELOPER HOOK. A 7 MB wake word or a 900 MB
// generalist is a real amount of money on a prepaid bundle, and the people this
// is built for are exactly the ones who will be handed a model over Bluetooth, on
// a memory card, or from a friend's laptop. Reading a side-loaded folder was
// already possible; what was missing is everything that makes it TRUSTWORTHY —
// nothing checked that the bytes were the bytes we published, and nothing moved
// them into the store, so the app kept treating an installed model as absent and
// offering to download it again.
//
// VERIFY, THEN IMPORT, IN THAT ORDER. The registry pins a SHA-256 for every file
// in every bundle, so a side-loaded copy can be held to exactly the standard a
// downloaded one is. That is the whole security story for this path: a model
// arriving by an untrusted route is checked against a hash we shipped, and one
// that does not match never reaches the store. Without it, "copy this folder onto
// your phone" is an invitation to run somebody else's weights.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>What happened when a side-loaded bundle was offered.</summary>
public enum SideloadOutcome
{
    /// <summary>Verified and moved into the model store.</summary>
    Imported,
    /// <summary>Already installed and intact; nothing to do.</summary>
    AlreadyInstalled,
    /// <summary>The folder does not hold this model.</summary>
    NotFound,
    /// <summary>Files are present but at least one does not match its published hash.</summary>
    Corrupt,
    /// <summary>The model is not in the catalogue, so there is nothing to check it against.</summary>
    Unknown,
    /// <summary>Verified, but the copy itself failed.</summary>
    CopyFailed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Detail">Plain language, safe to show someone.</param>
/// <param name="Files">How many files were verified.</param>
public sealed record SideloadResult(SideloadOutcome Outcome, string Detail, int Files = 0)
{
    public bool Usable => Outcome is SideloadOutcome.Imported or SideloadOutcome.AlreadyInstalled;
}

/// <summary>Verifies and installs a bundle copied onto the device by hand.</summary>
public sealed class SideloadedBundleImporter
{
    private readonly ModelRegistryService _registry;
    private readonly string _storageRoot;

    public SideloadedBundleImporter(ModelRegistryService registry, string storageRoot)
    {
        _registry = registry;
        _storageRoot = storageRoot;
    }

    /// <summary>
    /// Checks a folder against the catalogue and, if it matches, installs it.
    /// </summary>
    /// <param name="modelName">Registry name, e.g. "KWS-Zipformer-HeyB".</param>
    /// <param name="folder">Where the files were copied to.</param>
    public async Task<SideloadResult> ImportAsync(
        string modelName, string folder, CancellationToken ct = default)
    {
        var entry = _registry.GetLatestModel(modelName);
        if (entry?.BundleFiles is null || entry.BundleFiles.Count == 0)
            return new SideloadResult(SideloadOutcome.Unknown,
                $"“{modelName}” is not in the catalogue, so there is nothing to check this against.");

        if (!Directory.Exists(folder))
            return new SideloadResult(SideloadOutcome.NotFound, "That folder is not there.");

        // The published names are repo-relative ("kws-hey-b/encoder.int8.onnx") but
        // a person copying a folder across keeps the leaf names and rarely the
        // path, so both are accepted.
        var present = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        var verified = new List<(string Relative, string Source)>();
        foreach (var want in entry.BundleFiles)
        {
            ct.ThrowIfCancellationRequested();
            var leaf = Path.GetFileName(want.Name);

            if (!present.TryGetValue(leaf, out var source))
                return new SideloadResult(SideloadOutcome.NotFound,
                    $"This copy is missing {leaf}.");

            var info = new FileInfo(source);
            if (want.SizeBytes > 0 && info.Length != want.SizeBytes)
                return new SideloadResult(SideloadOutcome.Corrupt,
                    $"{leaf} is the wrong size — {info.Length:N0} bytes instead of {want.SizeBytes:N0}. " +
                    "The copy is probably incomplete.");

            if (!string.IsNullOrWhiteSpace(want.Sha256))
            {
                var actual = await Sha256Async(source, ct).ConfigureAwait(false);
                if (!string.Equals(actual, want.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new SideloadResult(SideloadOutcome.Corrupt,
                        $"{leaf} does not match the published version. " +
                        "It may have been damaged in transit, or it may not be ours.");
            }

            verified.Add((want.Name, source));
        }

        var target = Path.Combine(_storageRoot, modelName);
        if (Directory.Exists(target) &&
            verified.All(v => File.Exists(Path.Combine(target, v.Relative))))
            return new SideloadResult(SideloadOutcome.AlreadyInstalled,
                "This is already installed.", verified.Count);

        try
        {
            foreach (var (relative, source) in verified)
            {
                var dest = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // COPY, never move. The folder may be shared storage that someone
                // wants to pass on to the next phone, and consuming it would make
                // installing on one device destroy the copy for everyone else.
                File.Copy(source, dest, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            return new SideloadResult(SideloadOutcome.CopyFailed,
                $"Could not save it: {ex.Message}", verified.Count);
        }

        return new SideloadResult(SideloadOutcome.Imported,
            "Installed and checked.", verified.Count);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
