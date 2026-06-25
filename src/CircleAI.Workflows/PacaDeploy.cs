// PacaDeploy.cs
//
// (3.3.0) Paca-style single-command install. Generates a docker
// compose stack for one of three modes (dev / prod / e2e), bundles
// Postgres 16 + Valkey 8 + MinIO + nginx by default, supports
// external PG / external S3 / skip-AI overrides, generates a fresh
// .env at install with strong random secrets, and exposes a plugin
// install/uninstall script generator.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) Deployment mode.</summary>
public enum PacaDeployMode { Dev, Prod, E2E }

/// <summary>(3.3.0) Optional overrides.</summary>
/// <param name="UseExternalPostgres">If set, omit the bundled postgres service and write its DSN into .env.</param>
/// <param name="UseExternalS3">If set, omit MinIO and write external S3 endpoint into .env.</param>
/// <param name="SkipAiAgent">If true, omit the AI-runtime container (for very thin installs).</param>
public sealed record PacaDeployOverrides(
    string? UseExternalPostgres = null,
    string? UseExternalS3       = null,
    bool    SkipAiAgent         = false);

/// <summary>(3.3.0) Compose-file + .env pair the installer writes.</summary>
public sealed record PacaDeployArtifact(string ComposeYaml, string EnvFile);

/// <summary>(3.3.0) Generates compose + .env files for the paca stack.</summary>
public static class PacaDeployer
{
    /// <summary>(3.3.0) Build the compose + env pair for a given mode.</summary>
    public static PacaDeployArtifact Build(PacaDeployMode mode, PacaDeployOverrides? overrides = null)
    {
        overrides ??= new PacaDeployOverrides();
        var sb = new StringBuilder();
        sb.AppendLine("version: '3.9'");
        sb.AppendLine("services:");

        sb.AppendLine("  paca-web:");
        sb.AppendLine($"    image: bhengubv/paca-web:{(mode == PacaDeployMode.Prod ? "stable" : "latest")}");
        sb.AppendLine("    env_file: [.env]");
        sb.AppendLine("    ports:");
        sb.AppendLine($"      - \"{(mode == PacaDeployMode.Prod ? 443 : 8080)}:8080\"");

        if (string.IsNullOrEmpty(overrides.UseExternalPostgres))
        {
            sb.AppendLine("  paca-postgres:");
            sb.AppendLine("    image: postgres:16-alpine");
            sb.AppendLine("    environment:");
            sb.AppendLine("      POSTGRES_USER:     ${PACA_PG_USER}");
            sb.AppendLine("      POSTGRES_PASSWORD: ${PACA_PG_PASSWORD}");
            sb.AppendLine("      POSTGRES_DB:       ${PACA_PG_DB}");
            sb.AppendLine("    volumes: [paca_pg_data:/var/lib/postgresql/data]");
        }

        sb.AppendLine("  paca-valkey:");
        sb.AppendLine("    image: valkey/valkey:8");

        if (string.IsNullOrEmpty(overrides.UseExternalS3))
        {
            sb.AppendLine("  paca-minio:");
            sb.AppendLine("    image: minio/minio:latest");
            sb.AppendLine("    environment:");
            sb.AppendLine("      MINIO_ROOT_USER:     ${PACA_S3_KEY}");
            sb.AppendLine("      MINIO_ROOT_PASSWORD: ${PACA_S3_SECRET}");
            sb.AppendLine("    command: server /data");
        }

        sb.AppendLine("  paca-nginx:");
        sb.AppendLine("    image: nginx:1.27-alpine");

        if (!overrides.SkipAiAgent)
        {
            sb.AppendLine("  paca-ai:");
            sb.AppendLine("    image: bhengubv/paca-ai:latest");
            sb.AppendLine("    env_file: [.env]");
        }

        if (string.IsNullOrEmpty(overrides.UseExternalPostgres))
        {
            sb.AppendLine("volumes:");
            sb.AppendLine("  paca_pg_data: {}");
        }

        var env = BuildEnvFile(mode, overrides);
        return new PacaDeployArtifact(sb.ToString(), env);
    }

    /// <summary>(3.3.0) Build the bash install-plugin script that drives the plugin lifecycle from CLI.</summary>
    public static string BuildInstallPluginScript(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName)) throw new ArgumentException("pluginName required", nameof(pluginName));
        return $"""
        #!/usr/bin/env bash
        set -euo pipefail
        echo "[paca] Building WASM module for {pluginName}..."
        wasm-pack build --target web ./plugins/{pluginName}
        echo "[paca] Building frontend bundle..."
        cd ./plugins/{pluginName}/frontend && pnpm install && pnpm build
        cd -
        echo "[paca] Registering plugin with the API..."
        paca-cli plugins install ./plugins/{pluginName}/dist
        echo "[paca] Done."
        """;
    }

    /// <summary>(3.3.0) Bash script that uninstalls + cleans plugin artifacts.</summary>
    public static string BuildUninstallPluginScript(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName)) throw new ArgumentException("pluginName required", nameof(pluginName));
        return $"""
        #!/usr/bin/env bash
        set -euo pipefail
        echo "[paca] Uninstalling {pluginName}..."
        paca-cli plugins uninstall {pluginName}
        rm -rf ./plugins/{pluginName}/dist
        echo "[paca] Done."
        """;
    }

    private static string BuildEnvFile(PacaDeployMode mode, PacaDeployOverrides overrides)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PACA_MODE={mode.ToString().ToLowerInvariant()}");
        sb.AppendLine($"PACA_PG_USER=paca");
        sb.AppendLine($"PACA_PG_PASSWORD={RandomSecret(32)}");
        sb.AppendLine($"PACA_PG_DB=paca");
        if (!string.IsNullOrEmpty(overrides.UseExternalPostgres))
        {
            sb.AppendLine($"PACA_PG_URL={overrides.UseExternalPostgres}");
        }
        sb.AppendLine($"PACA_VALKEY_URL=redis://paca-valkey:6379");
        sb.AppendLine($"PACA_S3_KEY={RandomSecret(20)}");
        sb.AppendLine($"PACA_S3_SECRET={RandomSecret(40)}");
        if (!string.IsNullOrEmpty(overrides.UseExternalS3))
        {
            sb.AppendLine($"PACA_S3_ENDPOINT={overrides.UseExternalS3}");
        }
        sb.AppendLine($"PACA_JWT_SIGNING_SECRET={RandomSecret(48)}");
        sb.AppendLine($"PACA_AI_ENABLED={(!overrides.SkipAiAgent).ToString().ToLowerInvariant()}");
        return sb.ToString();
    }

    private static string RandomSecret(int length)
    {
        // URL-safe base64; trim padding; truncate to the requested length.
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..length];
    }
}
