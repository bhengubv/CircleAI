// Circle33DepBotTests.cs

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.DepBot;
using Xunit;

namespace CircleAI.Tests;

public class Circle33DepBotTests : IDisposable
{
    private readonly string _dir;
    public Circle33DepBotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"depbot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public async Task Scan_NpmAndPyAndNuget()
    {
        File.WriteAllText(Path.Combine(_dir, "package.json"),
            """{"dependencies":{"react":"18.2.0"},"devDependencies":{"vite":"5.0.0"}}""");
        File.WriteAllText(Path.Combine(_dir, "requirements.txt"),
            "# comment\nrequests==2.31.0\nnumpy>=1.24");
        File.WriteAllText(Path.Combine(_dir, "x.csproj"),
            """<Project><ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup></Project>""");

        var a = new FilesystemDependencyAnalyzer();
        var deps = await a.ScanAsync(_dir);

        Assert.Contains(deps, d => d.Ecosystem == "npm"   && d.Name == "react"           && d.CurrentVersion == "18.2.0");
        Assert.Contains(deps, d => d.Ecosystem == "npm"   && d.Name == "vite");
        Assert.Contains(deps, d => d.Ecosystem == "pypi"  && d.Name == "requests"        && d.CurrentVersion == "2.31.0");
        Assert.Contains(deps, d => d.Ecosystem == "nuget" && d.Name == "Newtonsoft.Json" && d.CurrentVersion == "13.0.3");
    }

    [Fact]
    public async Task ApplyUpdate_Nuget_RewritesCsprojVersion()
    {
        var path = Path.Combine(_dir, "x.csproj");
        File.WriteAllText(path,
            """<Project><ItemGroup><PackageReference Include="Pkg" Version="1.0.0" /></ItemGroup></Project>""");

        var u = new TextRewriteDependencyUpdater();
        await u.ApplyUpdateAsync(_dir, new DependencyUpdate("nuget", "Pkg", "1.0.0", "2.0.0", false));

        Assert.Contains("Version=\"2.0.0\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task ApplyUpdate_Npm_RewritesPackageJsonVersion()
    {
        var path = Path.Combine(_dir, "package.json");
        File.WriteAllText(path, """{"dependencies":{"react":"18.0.0"}}""");

        var u = new TextRewriteDependencyUpdater();
        await u.ApplyUpdateAsync(_dir, new DependencyUpdate("npm", "react", "18.0.0", "19.0.0", true));

        Assert.Contains("\"react\": \"19.0.0\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task ApplyUpdate_Pypi_RewritesRequirements()
    {
        var path = Path.Combine(_dir, "requirements.txt");
        File.WriteAllText(path, "requests==2.31.0\nnumpy>=1.24");

        var u = new TextRewriteDependencyUpdater();
        await u.ApplyUpdateAsync(_dir, new DependencyUpdate("pypi", "requests", "2.31.0", "2.32.0", false));

        Assert.Contains("requests==2.32.0", File.ReadAllText(path));
    }

    [Fact]
    public async Task Scan_MissingDir_Throws()
    {
        var a = new FilesystemDependencyAnalyzer();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            a.ScanAsync(Path.Combine(Path.GetTempPath(), "doesnt-exist-" + Guid.NewGuid())).AsTask());
    }
}
