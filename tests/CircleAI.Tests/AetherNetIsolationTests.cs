// AetherNetIsolationTests.cs
//
// The mesh transport is CARRIER-INDEPENDENT. The pooled-inference product
// assemblies hold the borrow/serve logic behind the abstract INetworkTransport
// seam, and must link NO AetherNet package — AetherNet is an optional runtime
// carrier wired only at the app/plug level, interchangeable with any other
// sealed transport.
//
// This was FALSE on 2026-09-15: CircleAI.Mesh ProjectReferenced CircleAI.AetherNet,
// which PackageReferences AetherNet.* 2.*, so the engine transitively linked all
// four packages behind five pure-C# capability types that needed none of them.
// "A0" relocated those types to CircleAI.Networking and dropped the reference.
//
// A build cannot catch a regression here: re-adding the reference compiles fine,
// and every consumer that can see it is fine too. Only a walk of the project
// graph says whether the dependency crept back.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CircleAI.Tests;

public class AetherNetIsolationTests
{
    // The assemblies that make up pooled inference. Each must stay AetherNet-free
    // so the mesh can ride an AetherNet carrier OR an internet node-relay OR the
    // in-memory test transport, interchangeably.
    [Theory]
    [InlineData("CircleAI.Mesh")]
    [InlineData("CircleAI.Mesh.Hosting")]
    [InlineData("CircleAI.Assistant")]
    public void Pooled_inference_assembly_links_no_AetherNet_package(string project)
    {
        var csproj = Path.Combine(Root(), "src", project, project + ".csproj");
        Assert.True(File.Exists(csproj), $"{project} not found at {csproj}");

        var offenders = new List<string>();
        Walk(csproj, new List<string> { project }, offenders, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.True(offenders.Count == 0,
            $"{project} transitively links an AetherNet package — the mesh must stay " +
            "carrier-independent (see A0). Offending reference path(s):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Depth-first over ProjectReferences (resolved by real relative path so nested
    /// projects are followed), collecting any <c>AetherNet.*</c> PackageReference.
    /// </summary>
    private static void Walk(string csprojPath, List<string> path, List<string> offenders, HashSet<string> visited)
    {
        var full = Path.GetFullPath(csprojPath);
        if (!visited.Add(full)) return;
        if (!File.Exists(full)) return;   // reference resolved outside the tree — nothing to walk

        var xml = File.ReadAllText(full);
        var dir = Path.GetDirectoryName(full)!;

        foreach (Match m in Regex.Matches(xml, @"<PackageReference\s+Include=""([^""]+)"""))
        {
            var id = m.Groups[1].Value;
            if (id.StartsWith("AetherNet", StringComparison.OrdinalIgnoreCase))
                offenders.Add(string.Join(" -> ", path) + $"  ==>  links package {id}");
        }

        foreach (Match m in Regex.Matches(xml, @"<ProjectReference\s+Include=""([^""]+\.csproj)"""))
        {
            var rel = m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
            var refPath = Path.Combine(dir, rel);
            var name = Path.GetFileNameWithoutExtension(refPath);
            Walk(refPath, [.. path, name], offenders, visited);
        }
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
