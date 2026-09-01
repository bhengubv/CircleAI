/**
 * workflows/paca_deploy
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/paca_deploy.ts
//
// (3.3.0) Paca-style single-command install (PacaDeploy.cs). Generates a
// docker compose stack for one of three modes (dev / prod / e2e), bundles
// Postgres 16 + Valkey 8 + MinIO + nginx by default, supports external PG /
// external S3 / skip-AI overrides, generates a fresh .env at install with
// strong random secrets, and exposes a plugin install/uninstall script
// generator.
//
// Uses node:crypto randomBytes — the runtime analogue of
// RandomNumberGenerator.GetBytes.

import { randomBytes } from "../platform";

/** (3.3.0) Deployment mode. Mirrors C# `PacaDeployMode`. */
export enum PacaDeployMode {
  Dev = "dev",
  Prod = "prod",
  E2E = "e2e",
}

/**
 * (3.3.0) Optional overrides. Mirrors C# `PacaDeployOverrides`.
 * @param useExternalPostgres If set, omit the bundled postgres service and write its DSN into .env.
 * @param useExternalS3 If set, omit MinIO and write external S3 endpoint into .env.
 * @param skipAiAgent If true, omit the AI-runtime container (for very thin installs).
 */
export interface PacaDeployOverrides {
  readonly useExternalPostgres: string | null;
  readonly useExternalS3: string | null;
  readonly skipAiAgent: boolean;
}

/** Constructs {@link PacaDeployOverrides} with the C# record defaults (all off). */
export function pacaDeployOverrides(overrides: Partial<PacaDeployOverrides> = {}): PacaDeployOverrides {
  return {
    useExternalPostgres: overrides.useExternalPostgres ?? null,
    useExternalS3: overrides.useExternalS3 ?? null,
    skipAiAgent: overrides.skipAiAgent ?? false,
  };
}

/** (3.3.0) Compose-file + .env pair the installer writes. Mirrors C# `PacaDeployArtifact`. */
export interface PacaDeployArtifact {
  readonly composeYaml: string;
  readonly envFile: string;
}

/** (3.3.0) Generates compose + .env files for the paca stack. Mirrors C# `PacaDeployer`. */
export const PacaDeployer = {
  /** (3.3.0) Build the compose + env pair for a given mode. */
  build(mode: PacaDeployMode, overrides?: PacaDeployOverrides | null): PacaDeployArtifact {
    const ov = overrides ?? pacaDeployOverrides();
    const lines: string[] = [];
    lines.push("version: '3.9'");
    lines.push("services:");

    lines.push("  paca-web:");
    lines.push(`    image: bhengubv/paca-web:${mode === PacaDeployMode.Prod ? "stable" : "latest"}`);
    lines.push("    env_file: [.env]");
    lines.push("    ports:");
    lines.push(`      - "${mode === PacaDeployMode.Prod ? 443 : 8080}:8080"`);

    if (isEmpty(ov.useExternalPostgres)) {
      lines.push("  paca-postgres:");
      lines.push("    image: postgres:16-alpine");
      lines.push("    environment:");
      lines.push("      POSTGRES_USER:     ${PACA_PG_USER}");
      lines.push("      POSTGRES_PASSWORD: ${PACA_PG_PASSWORD}");
      lines.push("      POSTGRES_DB:       ${PACA_PG_DB}");
      lines.push("    volumes: [paca_pg_data:/var/lib/postgresql/data]");
    }

    lines.push("  paca-valkey:");
    lines.push("    image: valkey/valkey:8");

    if (isEmpty(ov.useExternalS3)) {
      lines.push("  paca-minio:");
      lines.push("    image: minio/minio:latest");
      lines.push("    environment:");
      lines.push("      MINIO_ROOT_USER:     ${PACA_S3_KEY}");
      lines.push("      MINIO_ROOT_PASSWORD: ${PACA_S3_SECRET}");
      lines.push("    command: server /data");
    }

    lines.push("  paca-nginx:");
    lines.push("    image: nginx:1.27-alpine");

    if (!ov.skipAiAgent) {
      lines.push("  paca-ai:");
      lines.push("    image: bhengubv/paca-ai:latest");
      lines.push("    env_file: [.env]");
    }

    if (isEmpty(ov.useExternalPostgres)) {
      lines.push("volumes:");
      lines.push("  paca_pg_data: {}");
    }

    const composeYaml = lines.map((l) => `${l}\n`).join("");
    const env = buildEnvFile(mode, ov);
    return { composeYaml, envFile: env };
  },

  /** (3.3.0) Build the bash install-plugin script that drives the plugin lifecycle from CLI. */
  buildInstallPluginScript(pluginName: string): string {
    if (isBlank(pluginName)) throw new Error("pluginName required");
    return (
      "#!/usr/bin/env bash\n" +
      "set -euo pipefail\n" +
      `echo "[paca] Building WASM module for ${pluginName}..."\n` +
      `wasm-pack build --target web ./plugins/${pluginName}\n` +
      `echo "[paca] Building frontend bundle..."\n` +
      `cd ./plugins/${pluginName}/frontend && pnpm install && pnpm build\n` +
      "cd -\n" +
      `echo "[paca] Registering plugin with the API..."\n` +
      `paca-cli plugins install ./plugins/${pluginName}/dist\n` +
      'echo "[paca] Done."'
    );
  },

  /** (3.3.0) Bash script that uninstalls + cleans plugin artifacts. */
  buildUninstallPluginScript(pluginName: string): string {
    if (isBlank(pluginName)) throw new Error("pluginName required");
    return (
      "#!/usr/bin/env bash\n" +
      "set -euo pipefail\n" +
      `echo "[paca] Uninstalling ${pluginName}..."\n` +
      `paca-cli plugins uninstall ${pluginName}\n` +
      `rm -rf ./plugins/${pluginName}/dist\n` +
      'echo "[paca] Done."'
    );
  },
} as const;

function buildEnvFile(mode: PacaDeployMode, overrides: PacaDeployOverrides): string {
  const lines: string[] = [];
  lines.push(`PACA_MODE=${mode.toString().toLowerCase()}`);
  lines.push("PACA_PG_USER=paca");
  lines.push(`PACA_PG_PASSWORD=${randomSecret(32)}`);
  lines.push("PACA_PG_DB=paca");
  if (!isEmpty(overrides.useExternalPostgres)) {
    lines.push(`PACA_PG_URL=${overrides.useExternalPostgres}`);
  }
  lines.push("PACA_VALKEY_URL=redis://paca-valkey:6379");
  lines.push(`PACA_S3_KEY=${randomSecret(20)}`);
  lines.push(`PACA_S3_SECRET=${randomSecret(40)}`);
  if (!isEmpty(overrides.useExternalS3)) {
    lines.push(`PACA_S3_ENDPOINT=${overrides.useExternalS3}`);
  }
  lines.push(`PACA_JWT_SIGNING_SECRET=${randomSecret(48)}`);
  lines.push(`PACA_AI_ENABLED=${(!overrides.skipAiAgent).toString().toLowerCase()}`);
  return lines.map((l) => `${l}\n`).join("");
}

/** URL-safe base64; trim padding; truncate to the requested length. Mirrors C# `RandomSecret`. */
function randomSecret(length: number): string {
  // Generate enough entropy that the url-safe encoding is at least `length` chars.
  const bytes = randomBytes(length);
  const encoded = bytes.toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return encoded.slice(0, length);
}

function isEmpty(s: string | null): boolean {
  return s === null || s.length === 0;
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
