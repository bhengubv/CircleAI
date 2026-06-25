// Circle33PacaDeployTests.cs
//
// (3.3.0) Tests for paca deploy manifests.

using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaDeployTests
{
    [Fact]
    public void Build_Dev_BundlesAllDefaults()
    {
        var a = PacaDeployer.Build(PacaDeployMode.Dev);
        Assert.Contains("paca-postgres", a.ComposeYaml);
        Assert.Contains("paca-valkey",   a.ComposeYaml);
        Assert.Contains("paca-minio",    a.ComposeYaml);
        Assert.Contains("paca-nginx",    a.ComposeYaml);
        Assert.Contains("paca-ai",       a.ComposeYaml);
        Assert.Contains("PACA_MODE=dev", a.EnvFile);
    }

    [Fact]
    public void Build_Prod_UsesStableImagesAnd443()
    {
        var a = PacaDeployer.Build(PacaDeployMode.Prod);
        Assert.Contains("bhengubv/paca-web:stable", a.ComposeYaml);
        Assert.Contains("\"443:8080\"",             a.ComposeYaml);
    }

    [Fact]
    public void Build_ExternalPostgres_OmitsBundled()
    {
        var a = PacaDeployer.Build(PacaDeployMode.Prod, new PacaDeployOverrides(
            UseExternalPostgres: "postgres://user:pass@external-db:5432/paca"));
        Assert.DoesNotContain("paca-postgres:", a.ComposeYaml);
        Assert.Contains("PACA_PG_URL=postgres://", a.EnvFile);
    }

    [Fact]
    public void Build_ExternalS3_OmitsMinio()
    {
        var a = PacaDeployer.Build(PacaDeployMode.Prod, new PacaDeployOverrides(
            UseExternalS3: "https://s3.amazonaws.com"));
        Assert.DoesNotContain("paca-minio:", a.ComposeYaml);
        Assert.Contains("PACA_S3_ENDPOINT=https://", a.EnvFile);
    }

    [Fact]
    public void Build_SkipAi_OmitsAiContainer()
    {
        var a = PacaDeployer.Build(PacaDeployMode.Prod, new PacaDeployOverrides(SkipAiAgent: true));
        Assert.DoesNotContain("paca-ai:", a.ComposeYaml);
        Assert.Contains("PACA_AI_ENABLED=false", a.EnvFile);
    }

    [Fact]
    public void Env_RandomisesSecretsPerInvocation()
    {
        var a = PacaDeployer.Build(PacaDeployMode.Dev);
        var b = PacaDeployer.Build(PacaDeployMode.Dev);
        Assert.NotEqual(ExtractEnvValue(a.EnvFile, "PACA_PG_PASSWORD"),
                        ExtractEnvValue(b.EnvFile, "PACA_PG_PASSWORD"));
    }

    [Fact]
    public void Build_AllThreeModes_Differ()
    {
        var dev  = PacaDeployer.Build(PacaDeployMode.Dev);
        var prod = PacaDeployer.Build(PacaDeployMode.Prod);
        var e2e  = PacaDeployer.Build(PacaDeployMode.E2E);
        Assert.Contains("dev",  dev.EnvFile);
        Assert.Contains("prod", prod.EnvFile);
        Assert.Contains("e2e",  e2e.EnvFile);
    }

    [Fact]
    public void InstallPluginScript_ContainsExpectedSteps()
    {
        var s = PacaDeployer.BuildInstallPluginScript("com.paca.bdd");
        Assert.Contains("wasm-pack", s);
        Assert.Contains("pnpm",      s);
        Assert.Contains("paca-cli",  s);
    }

    [Fact]
    public void UninstallPluginScript_RemovesDistDir()
    {
        var s = PacaDeployer.BuildUninstallPluginScript("com.paca.bdd");
        Assert.Contains("paca-cli plugins uninstall com.paca.bdd", s);
        Assert.Contains("rm -rf ./plugins/com.paca.bdd/dist", s);
    }

    private static string ExtractEnvValue(string envFile, string key)
    {
        foreach (var line in envFile.Split('\n'))
        {
            if (line.StartsWith(key + "="))
            {
                return line[(key.Length + 1)..].Trim();
            }
        }
        return "";
    }
}
