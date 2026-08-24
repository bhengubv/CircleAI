// GitHubReleaseLayoutTests.cs
//
// A GitHub release asset has TWO names that people keep collapsing into one:
// the asset name in the release (flat — releases have no directories) and the
// path the file unpacks to on disk (a real directory layout, and for at least
// one bundle it is load-bearing).
//
// WHY THIS TEST EARNS ITS PLACE. The first cut of the release support spelled
// the tag as a leading directory — "voices-v1/sys.dic". That builds a correct
// URL, downloads 103 MB, verifies its SHA, and unpacks it into a folder the
// Open JTalk phonemiser does not look in. Nothing errors. Nothing logs. The
// dictionary is simply not found and Japanese silently has no phonemiser, the
// same shape of failure as the 45 sidecars that 404'd for weeks. The bug is
// invisible at every layer except a device actually trying to speak Japanese,
// which is far too late to find out.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class GitHubReleaseLayoutTests
{
    private sealed record ReleaseFile(string Model, string Repo, string Name);

    private static IReadOnlyList<ReleaseFile> ReleaseFiles()
    {
        var asm = typeof(CircleAI.Core.Models.EmbeddedVoiceConfigs).Assembly;
        var res = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("Models.embedded_registry.json", StringComparison.Ordinal));

        using var stream = asm.GetManifestResourceStream(res)!;
        using var doc = JsonDocument.Parse(stream);

        var files = new List<ReleaseFile>();
        foreach (var model in doc.RootElement.GetProperty("Models").EnumerateArray())
        {
            if (!model.TryGetProperty("Source", out var src)
                || src.GetString() != "GitHubRelease") continue;
            if (!model.TryGetProperty("BundleFiles", out var bundle)) continue;

            var name = model.GetProperty("Name").GetString() ?? "?";
            var repo = model.TryGetProperty("Repo", out var r) ? r.GetString() ?? "" : "";

            foreach (var f in bundle.EnumerateArray())
                files.Add(new ReleaseFile(name, repo, f.GetProperty("Name").GetString() ?? ""));
        }
        return files;
    }

    /// <summary>
    /// The URL builder, reached directly. It is a pure function of (source,
    /// repo, name) and the whole failure lived inside it, so it is tested as
    /// one rather than through a 144 MB download.
    /// </summary>
    private static Uri Url(string repo, string fileName)
    {
        var m = typeof(ModelDownloadService).GetMethod(
            "BuildPrimaryUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Uri)m.Invoke(null, [CircleAI.Core.ModelSource.GitHubRelease, repo, fileName])!;
    }

    [Fact]
    public void The_registry_still_has_release_hosted_bundles()
    {
        // Without this the rest of the class would pass by having nothing to check.
        Assert.NotEmpty(ReleaseFiles());
    }

    [Fact]
    public void A_release_tag_rides_on_the_repo_not_on_the_file_name()
    {
        var offenders = ReleaseFiles()
            .Where(f => !f.Repo.Contains('@', StringComparison.Ordinal))
            .Select(f => $"{f.Model}: repo '{f.Repo}' names no release tag")
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0,
            "GitHubRelease entries spell the tag as owner/name@tag. Putting it in the "
            + "bundle file name instead makes the tag the on-disk folder:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_bundle_file_name_begins_with_its_own_release_tag()
    {
        // The exact regression: "voices-v1/sys.dic". A correct URL, and the file
        // lands in <store>/OpenJTalk-Dic-ja/voices-v1/ where nothing reads it.
        var offenders = ReleaseFiles()
            .Where(f =>
            {
                var at = f.Repo.IndexOf('@');
                if (at < 0) return false;
                var tag = f.Repo[(at + 1)..];
                return f.Name.StartsWith(tag + "/", StringComparison.Ordinal);
            })
            .Select(f => $"{f.Model}: {f.Name}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A bundle file name is the path it unpacks to, not the release tag:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_asset_is_the_last_segment_and_the_rest_is_layout()
    {
        foreach (var f in ReleaseFiles())
        {
            var tag = f.Repo[(f.Repo.IndexOf('@') + 1)..];
            var asset = f.Name.Split('/')[^1];
            var url = Url(f.Repo, f.Name);

            Assert.EndsWith($"/releases/download/{tag}/{asset}", url.AbsoluteUri,
                StringComparison.Ordinal);
            // The layout must NOT leak into the URL: releases are flat, and a
            // directory in the path is a 404 rather than a wrong file.
            Assert.DoesNotContain($"/{tag}/open-jtalk-dic/", url.AbsoluteUri,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_Japanese_dictionary_unpacks_where_the_phonemiser_looks()
    {
        // OpenJTalkPhonemizer searches <store>/OpenJTalk-Dic-ja/ and, failing
        // the names it knows, any subfolder holding sys.dic. This asserts the
        // cheap named path still hits, so the disk-walking fallback stays a
        // fallback rather than the only thing keeping Japanese alive.
        var dic = ReleaseFiles().Where(f => f.Model == "OpenJTalk-Dic-ja").ToList();
        Assert.NotEmpty(dic);

        var sysDic = dic.SingleOrDefault(f => f.Name.EndsWith("sys.dic", StringComparison.Ordinal));
        Assert.NotNull(sysDic);
        Assert.Equal("open-jtalk-dic/sys.dic", sysDic!.Name);

        // Every dictionary file lands in ONE directory. Open JTalk is handed a
        // folder, not a file list, and it opens the others by name from there.
        var folders = dic.Select(f => f.Name[..f.Name.LastIndexOf('/')])
                         .Distinct()
                         .ToList();
        Assert.Single(folders);
    }

    [Fact]
    public void A_repo_without_a_tag_still_builds_the_older_url()
    {
        // Backwards compatibility, stated as a fact rather than assumed: an
        // entry that has not moved to owner/name@tag keeps the previous
        // spelling, where the name carried the tag.
        var url = Url("bhengubv/circleai-voices", "voices-v1/ne_NP-google-medium.onnx");
        Assert.Equal(
            "https://github.com/bhengubv/circleai-voices/releases/download/voices-v1/ne_NP-google-medium.onnx",
            url.AbsoluteUri);
    }
}
