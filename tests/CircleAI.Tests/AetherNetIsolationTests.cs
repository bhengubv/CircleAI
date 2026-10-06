// AetherNetIsolationTests.cs
//
// The mesh transport is CARRIER-INDEPENDENT. The pooled-inference code holds the
// borrow/serve logic behind the abstract INetworkTransport seam and must depend on
// NO AetherNet type — AetherNet is an optional runtime carrier wired only at the
// app/plug level, interchangeable with any other sealed transport.
//
// This was FALSE on 2026-09-15: CircleAI.Mesh ProjectReferenced CircleAI.AetherNet,
// which PackageReferences AetherNet.* 2.*, so the engine transitively linked all
// four packages behind five pure-C# capability types that needed none of them.
// "A0" relocated those types to CircleAI.Networking and dropped the reference.
//
// A build cannot catch a regression here: re-adding the dependency compiles fine,
// and every consumer that can see it is fine too. Only an explicit check says
// whether it crept back.
//
// WHY THIS TEST CHANGED SHAPE AT 3.8.0, AND WHY THE GUARANTEE DID NOT.
// It used to walk the ProjectReference graph out of src/CircleAI.Mesh.csproj and
// fail on any AetherNet PackageReference it reached. The 169 libraries are now
// folders inside ONE project, and that project does reference AetherNet.Core,
// .Messaging, .Security and .Transport — because Aether/ and AetherNet/ are in it
// too. So the assembly-level question has only one answer now and it is "yes",
// which would make the old test either permanently red or quietly deleted.
//
// The assembly boundary was how the rule was MEASURED, never what it meant. What it
// meant is that the borrow/serve code must not reach for a carrier, and that is a
// property of the source, which survives the merge intact. So it is now checked on
// the source: no file under the pooled-inference folders may name an AetherNet type.
//
// This is stricter than the old walk in one respect — it catches a bare
// `using AetherNet.X;` that no project file would mention — and weaker in another:
// it cannot see a dependency introduced through a helper that lives elsewhere in
// the merged assembly. The second is the real cost of one DLL, and it is the reason
// the folder list below must stay honest about which folders are pooled inference.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CircleAI.Tests;

public class AetherNetIsolationTests
{
    // The folders that make up pooled inference. Each must stay AetherNet-free so
    // the mesh can ride an AetherNet carrier OR an internet node-relay OR the
    // in-memory test transport, interchangeably.
    [Theory]
    [InlineData("Mesh")]
    [InlineData("Mesh.Hosting")]
    [InlineData("Assistant")]
    public void Pooled_inference_code_names_no_AetherNet_type(string folder)
    {
        var dir = Path.Combine(Root(), "src", "CircleAI", folder);
        Assert.True(Directory.Exists(dir),
            $"'{folder}' is not at {dir}. If it moved, this test is guarding nothing.");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            // bin/obj hold generated copies and would report the same hit twice.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // A comment explaining that this code deliberately does NOT use
                // AetherNet is not a dependency on it — and the header above is
                // itself full of the word.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal) ||
                    trimmed.StartsWith("/*", StringComparison.Ordinal))
                    continue;

                // `using AetherNet.X;` or any `AetherNet.Type` reference. CircleAI's
                // own CircleAI.AetherNet namespace is a different thing and is
                // excluded by requiring AetherNet at the start of the qualified name.
                if (Regex.IsMatch(line, @"(?<![A-Za-z0-9_.])AetherNet\s*\."))
                {
                    offenders.Add($"{Path.GetRelativePath(Root(), file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"'{folder}' names an AetherNet type — the mesh must stay carrier-independent " +
            "(see A0). AetherNet belongs at the app/plug level, behind INetworkTransport. " +
            $"Offending line(s):\n  {string.Join("\n  ", offenders)}");
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
