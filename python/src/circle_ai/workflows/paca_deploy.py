# paca_deploy.py
#
# Port of CircleAI.Workflows PacaDeploy.cs (C# — the EXACT spec).
#
# (3.3.0) Paca-style single-command install. Generates a docker compose stack
# for one of three modes (dev / prod / e2e), bundles Postgres 16 + Valkey 8 +
# MinIO + nginx by default, supports external PG / external S3 / skip-AI
# overrides, generates a fresh .env at install with strong random secrets, and
# exposes a plugin install/uninstall script generator.
#
# RandomSecret uses secrets.token_bytes(length) → URL-safe base64 (+/ → -_),
# padding stripped, truncated to the requested length — mirroring the C#
# RandomNumberGenerator.GetBytes(length) path. Enum .ToLowerInvariant() and C#
# bool.ToString().ToLowerInvariant() ("true"/"false") are reproduced exactly.

from __future__ import annotations

import base64
import secrets
from dataclasses import dataclass
from enum import IntEnum
from typing import Optional


class PacaDeployMode(IntEnum):
    """(3.3.0) Deployment mode."""

    Dev = 0
    Prod = 1
    E2E = 2


@dataclass(frozen=True, slots=True)
class PacaDeployOverrides:
    """(3.3.0) Optional overrides.

    ``use_external_postgres``: if set, omit the bundled postgres service and
    write its DSN into .env. ``use_external_s3``: if set, omit MinIO and write
    external S3 endpoint into .env. ``skip_ai_agent``: if True, omit the
    AI-runtime container (for very thin installs)."""

    use_external_postgres: Optional[str] = None
    use_external_s3: Optional[str] = None
    skip_ai_agent: bool = False


@dataclass(frozen=True, slots=True)
class PacaDeployArtifact:
    """(3.3.0) Compose-file + .env pair the installer writes."""

    compose_yaml: str
    env_file: str


def _mode_name(mode: PacaDeployMode) -> str:
    # C# enum.ToString().ToLowerInvariant(): Dev->"dev", Prod->"prod", E2E->"e2e".
    return mode.name.lower()


class PacaDeployer:
    """(3.3.0) Generates compose + .env files for the paca stack."""

    @staticmethod
    def build(mode: PacaDeployMode, overrides: Optional[PacaDeployOverrides] = None) -> PacaDeployArtifact:
        """(3.3.0) Build the compose + env pair for a given mode."""
        if overrides is None:
            overrides = PacaDeployOverrides()
        lines = []
        lines.append("version: '3.9'")
        lines.append("services:")

        lines.append("  paca-web:")
        lines.append(f"    image: bhengubv/paca-web:{'stable' if mode == PacaDeployMode.Prod else 'latest'}")
        lines.append("    env_file: [.env]")
        lines.append("    ports:")
        lines.append(f"      - \"{443 if mode == PacaDeployMode.Prod else 8080}:8080\"")

        if not overrides.use_external_postgres:
            lines.append("  paca-postgres:")
            lines.append("    image: postgres:16-alpine")
            lines.append("    environment:")
            lines.append("      POSTGRES_USER:     ${PACA_PG_USER}")
            lines.append("      POSTGRES_PASSWORD: ${PACA_PG_PASSWORD}")
            lines.append("      POSTGRES_DB:       ${PACA_PG_DB}")
            lines.append("    volumes: [paca_pg_data:/var/lib/postgresql/data]")

        lines.append("  paca-valkey:")
        lines.append("    image: valkey/valkey:8")

        if not overrides.use_external_s3:
            lines.append("  paca-minio:")
            lines.append("    image: minio/minio:latest")
            lines.append("    environment:")
            lines.append("      MINIO_ROOT_USER:     ${PACA_S3_KEY}")
            lines.append("      MINIO_ROOT_PASSWORD: ${PACA_S3_SECRET}")
            lines.append("    command: server /data")

        lines.append("  paca-nginx:")
        lines.append("    image: nginx:1.27-alpine")

        if not overrides.skip_ai_agent:
            lines.append("  paca-ai:")
            lines.append("    image: bhengubv/paca-ai:latest")
            lines.append("    env_file: [.env]")

        if not overrides.use_external_postgres:
            lines.append("volumes:")
            lines.append("  paca_pg_data: {}")

        # C# StringBuilder.AppendLine emits a trailing newline after every line.
        compose = "\n".join(lines) + "\n"
        env = PacaDeployer._build_env_file(mode, overrides)
        return PacaDeployArtifact(compose, env)

    @staticmethod
    def build_install_plugin_script(plugin_name: str) -> str:
        """(3.3.0) Build the bash install-plugin script that drives the plugin
        lifecycle from CLI."""
        if plugin_name is None or plugin_name.strip() == "":
            raise ValueError("pluginName required")
        return (
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            f'echo "[paca] Building WASM module for {plugin_name}..."\n'
            f"wasm-pack build --target web ./plugins/{plugin_name}\n"
            'echo "[paca] Building frontend bundle..."\n'
            f"cd ./plugins/{plugin_name}/frontend && pnpm install && pnpm build\n"
            "cd -\n"
            'echo "[paca] Registering plugin with the API..."\n'
            f"paca-cli plugins install ./plugins/{plugin_name}/dist\n"
            'echo "[paca] Done."'
        )

    @staticmethod
    def build_uninstall_plugin_script(plugin_name: str) -> str:
        """(3.3.0) Bash script that uninstalls + cleans plugin artifacts."""
        if plugin_name is None or plugin_name.strip() == "":
            raise ValueError("pluginName required")
        return (
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            f'echo "[paca] Uninstalling {plugin_name}..."\n'
            f"paca-cli plugins uninstall {plugin_name}\n"
            f"rm -rf ./plugins/{plugin_name}/dist\n"
            'echo "[paca] Done."'
        )

    @staticmethod
    def _build_env_file(mode: PacaDeployMode, overrides: PacaDeployOverrides) -> str:
        lines = []
        lines.append(f"PACA_MODE={_mode_name(mode)}")
        lines.append("PACA_PG_USER=paca")
        lines.append(f"PACA_PG_PASSWORD={PacaDeployer._random_secret(32)}")
        lines.append("PACA_PG_DB=paca")
        if overrides.use_external_postgres:
            lines.append(f"PACA_PG_URL={overrides.use_external_postgres}")
        lines.append("PACA_VALKEY_URL=redis://paca-valkey:6379")
        lines.append(f"PACA_S3_KEY={PacaDeployer._random_secret(20)}")
        lines.append(f"PACA_S3_SECRET={PacaDeployer._random_secret(40)}")
        if overrides.use_external_s3:
            lines.append(f"PACA_S3_ENDPOINT={overrides.use_external_s3}")
        lines.append(f"PACA_JWT_SIGNING_SECRET={PacaDeployer._random_secret(48)}")
        lines.append(f"PACA_AI_ENABLED={'true' if not overrides.skip_ai_agent else 'false'}")
        return "\n".join(lines) + "\n"

    @staticmethod
    def _random_secret(length: int) -> str:
        # URL-safe base64; trim padding; truncate to the requested length.
        raw = secrets.token_bytes(length)
        b64 = base64.b64encode(raw).decode("ascii").replace("+", "-").replace("/", "_").rstrip("=")
        return b64[:length]
