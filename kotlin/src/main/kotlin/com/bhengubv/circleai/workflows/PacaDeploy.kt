// PacaDeploy.kt
//
// Kotlin port of CircleAI.Workflows/PacaDeploy.cs.
//
// (3.3.0) Paca-style single-command install. Generates a docker compose stack
// for one of three modes (dev / prod / e2e), bundles Postgres 16 + Valkey 8 +
// MinIO + nginx by default, supports external PG / external S3 / skip-AI
// overrides, generates a fresh .env at install with strong random secrets, and
// exposes a plugin install/uninstall script generator.
//
// NOTE: `${...}` sequences that are SHELL/compose expansions (not Kotlin string
// templates) are written with the ${'$'} escape so Kotlin emits a literal '$'.

package com.bhengubv.circleai.workflows

import java.security.SecureRandom
import java.util.Base64
import java.util.Locale

/** (3.3.0) Deployment mode. */
enum class PacaDeployMode { Dev, Prod, E2E }

/**
 * (3.3.0) Optional overrides.
 *
 * @property useExternalPostgres If set, omit the bundled postgres service and
 *   write its DSN into .env.
 * @property useExternalS3 If set, omit MinIO and write external S3 endpoint.
 * @property skipAiAgent If true, omit the AI-runtime container.
 */
data class PacaDeployOverrides(
    val useExternalPostgres: String? = null,
    val useExternalS3: String? = null,
    val skipAiAgent: Boolean = false,
)

/** (3.3.0) Compose-file + .env pair the installer writes. */
data class PacaDeployArtifact(val composeYaml: String, val envFile: String)

/** (3.3.0) Generates compose + .env files for the paca stack. */
object PacaDeployer {

    private val random = SecureRandom()

    /** (3.3.0) Build the compose + env pair for a given mode. */
    fun build(mode: PacaDeployMode, overrides: PacaDeployOverrides? = null): PacaDeployArtifact {
        val ovr = overrides ?: PacaDeployOverrides()
        val sb = StringBuilder()
        sb.appendLine("version: '3.9'")
        sb.appendLine("services:")

        sb.appendLine("  paca-web:")
        sb.appendLine("    image: bhengubv/paca-web:${if (mode == PacaDeployMode.Prod) "stable" else "latest"}")
        sb.appendLine("    env_file: [.env]")
        sb.appendLine("    ports:")
        sb.appendLine("      - \"${if (mode == PacaDeployMode.Prod) 443 else 8080}:8080\"")

        if (ovr.useExternalPostgres.isNullOrEmpty()) {
            sb.appendLine("  paca-postgres:")
            sb.appendLine("    image: postgres:16-alpine")
            sb.appendLine("    environment:")
            sb.appendLine("      POSTGRES_USER:     ${'$'}{PACA_PG_USER}")
            sb.appendLine("      POSTGRES_PASSWORD: ${'$'}{PACA_PG_PASSWORD}")
            sb.appendLine("      POSTGRES_DB:       ${'$'}{PACA_PG_DB}")
            sb.appendLine("    volumes: [paca_pg_data:/var/lib/postgresql/data]")
        }

        sb.appendLine("  paca-valkey:")
        sb.appendLine("    image: valkey/valkey:8")

        if (ovr.useExternalS3.isNullOrEmpty()) {
            sb.appendLine("  paca-minio:")
            sb.appendLine("    image: minio/minio:latest")
            sb.appendLine("    environment:")
            sb.appendLine("      MINIO_ROOT_USER:     ${'$'}{PACA_S3_KEY}")
            sb.appendLine("      MINIO_ROOT_PASSWORD: ${'$'}{PACA_S3_SECRET}")
            sb.appendLine("    command: server /data")
        }

        sb.appendLine("  paca-nginx:")
        sb.appendLine("    image: nginx:1.27-alpine")

        if (!ovr.skipAiAgent) {
            sb.appendLine("  paca-ai:")
            sb.appendLine("    image: bhengubv/paca-ai:latest")
            sb.appendLine("    env_file: [.env]")
        }

        if (ovr.useExternalPostgres.isNullOrEmpty()) {
            sb.appendLine("volumes:")
            sb.appendLine("  paca_pg_data: {}")
        }

        val env = buildEnvFile(mode, ovr)
        return PacaDeployArtifact(sb.toString(), env)
    }

    /**
     * (3.3.0) Build the bash install-plugin script that drives the plugin
     * lifecycle from CLI.
     */
    fun buildInstallPluginScript(pluginName: String): String {
        require(pluginName.isNotBlank()) { "pluginName required" }
        return """
        #!/usr/bin/env bash
        set -euo pipefail
        echo "[paca] Building WASM module for $pluginName..."
        wasm-pack build --target web ./plugins/$pluginName
        echo "[paca] Building frontend bundle..."
        cd ./plugins/$pluginName/frontend && pnpm install && pnpm build
        cd -
        echo "[paca] Registering plugin with the API..."
        paca-cli plugins install ./plugins/$pluginName/dist
        echo "[paca] Done."
        """.trimIndent()
    }

    /** (3.3.0) Bash script that uninstalls + cleans plugin artifacts. */
    fun buildUninstallPluginScript(pluginName: String): String {
        require(pluginName.isNotBlank()) { "pluginName required" }
        return """
        #!/usr/bin/env bash
        set -euo pipefail
        echo "[paca] Uninstalling $pluginName..."
        paca-cli plugins uninstall $pluginName
        rm -rf ./plugins/$pluginName/dist
        echo "[paca] Done."
        """.trimIndent()
    }

    private fun buildEnvFile(mode: PacaDeployMode, overrides: PacaDeployOverrides): String {
        val sb = StringBuilder()
        sb.appendLine("PACA_MODE=${mode.name.lowercase(Locale.ROOT)}")
        sb.appendLine("PACA_PG_USER=paca")
        sb.appendLine("PACA_PG_PASSWORD=${randomSecret(32)}")
        sb.appendLine("PACA_PG_DB=paca")
        if (!overrides.useExternalPostgres.isNullOrEmpty()) {
            sb.appendLine("PACA_PG_URL=${overrides.useExternalPostgres}")
        }
        sb.appendLine("PACA_VALKEY_URL=redis://paca-valkey:6379")
        sb.appendLine("PACA_S3_KEY=${randomSecret(20)}")
        sb.appendLine("PACA_S3_SECRET=${randomSecret(40)}")
        if (!overrides.useExternalS3.isNullOrEmpty()) {
            sb.appendLine("PACA_S3_ENDPOINT=${overrides.useExternalS3}")
        }
        sb.appendLine("PACA_JWT_SIGNING_SECRET=${randomSecret(48)}")
        sb.appendLine("PACA_AI_ENABLED=${(!overrides.skipAiAgent).toString().lowercase(Locale.ROOT)}")
        return sb.toString()
    }

    private fun randomSecret(length: Int): String {
        // URL-safe base64; trim padding; truncate to the requested length.
        val bytes = ByteArray(length).also { random.nextBytes(it) }
        val encoded = Base64.getEncoder().encodeToString(bytes)
            .replace('+', '-').replace('/', '_').trimEnd('=')
        return if (encoded.length >= length) encoded.substring(0, length) else encoded
    }
}
