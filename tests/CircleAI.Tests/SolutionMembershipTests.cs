// SolutionMembershipTests.cs
//
// OPEN-GAPS A′7: the bUnit suite - 390 tests over the screens - was in no solution, so when it
// stopped compiling nothing noticed, and the solution did not list the product library itself either.
// Both are in CircleAI.sln now, and this holds every test project there. (The solution also names 165
// projects from the pre-3.8.0 layout that no longer exist; taking those lines out is a deletion, and
// that is the owner's call - the register says so.)

using System.Text.RegularExpressions;
using Xunit;

namespace CircleAI.Tests;

public sealed class SolutionMembershipTests
{
    private static DirectoryInfo Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CircleAI.sln"))) directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("CircleAI.sln is not above the test output.");
    }

    private static HashSet<string> SolutionProjects(DirectoryInfo root) =>
        Regex.Matches(File.ReadAllText(Path.Combine(root.FullName, "CircleAI.sln")), "\"([^\"]+\\.csproj)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Every_test_project_and_the_library_are_in_the_solution()
    {
        var root = Root();
        var listed = SolutionProjects(root);
        // One level down - tests/<project>/<project>.csproj - never into bin or obj.
        var tests = Directory.GetDirectories(Path.Combine(root.FullName, "tests"))
            .SelectMany(d => Directory.GetFiles(d, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(p => Path.GetRelativePath(root.FullName, p).Replace('\\', '/'))
            .ToList();

        Assert.NotEmpty(tests);
        Assert.All(tests, project => Assert.True(listed.Contains(project), $"{project} is in no solution, so nothing builds it"));
        Assert.Contains("src/CircleAI/CircleAI.csproj", listed);
    }
}
