// workflows_deploy.go
//
// Ports CircleAI.Workflows/PacaDeploy.cs — paca-style single-command install.
// Generates a docker-compose stack for one of three modes (dev/prod/e2e),
// bundles Postgres 16 + Valkey 8 + MinIO + nginx by default, honours external-PG
// / external-S3 / skip-AI overrides, and emits a fresh .env with strong random
// secrets. Also generates the plugin install/uninstall bash scripts.
//
//	PacaDeployMode (enum)  -> int consts (Dev=0, Prod=1, E2E=2)
//	PacaDeployOverrides / PacaDeployArtifact (records) -> structs
//	PacaDeployer (static)   -> package funcs
//
// The compose/env text is produced line-for-line as the C# StringBuilder does
// (AppendLine → "\n"), so the byte output matches. RandomSecret uses
// crypto/rand + URL-safe base64, generating enough bytes that the truncation to
// `length` chars always succeeds (the C# takes length bytes → ~1.34×length
// base64 chars → truncates back to length).

package circleai

import (
	"crypto/rand"
	"encoding/base64"
	"errors"
	"strconv"
	"strings"
)

// PacaDeployMode is a deployment mode. Ports PacaDeployMode (Dev=0, Prod=1, E2E=2).
type PacaDeployMode int

const (
	// PacaDeployDev — development mode.
	PacaDeployDev PacaDeployMode = 0
	// PacaDeployProd — production mode.
	PacaDeployProd PacaDeployMode = 1
	// PacaDeployE2E — end-to-end test mode.
	PacaDeployE2E PacaDeployMode = 2
)

func (m PacaDeployMode) lower() string {
	switch m {
	case PacaDeployProd:
		return "prod"
	case PacaDeployE2E:
		return "e2e"
	default:
		return "dev"
	}
}

// PacaDeployOverrides holds optional deploy overrides. Ports the
// PacaDeployOverrides record. UseExternalPostgres / UseExternalS3 empty = use
// the bundled service.
type PacaDeployOverrides struct {
	UseExternalPostgres string
	UseExternalS3       string
	SkipAIAgent         bool
}

// PacaDeployArtifact is the compose-file + .env pair the installer writes. Ports
// the PacaDeployArtifact record.
type PacaDeployArtifact struct {
	ComposeYAML string
	EnvFile     string
}

// BuildPacaDeploy builds the compose + env pair for a given mode. Ports
// PacaDeployer.Build.
func BuildPacaDeploy(mode PacaDeployMode, overrides PacaDeployOverrides) (PacaDeployArtifact, error) {
	var sb strings.Builder
	line := func(s string) { sb.WriteString(s); sb.WriteString("\n") }

	line("version: '3.9'")
	line("services:")

	line("  paca-web:")
	webTag := "latest"
	webPort := "8080"
	if mode == PacaDeployProd {
		webTag = "stable"
		webPort = "443"
	}
	line("    image: bhengubv/paca-web:" + webTag)
	line("    env_file: [.env]")
	line("    ports:")
	line("      - \"" + webPort + ":8080\"")

	if overrides.UseExternalPostgres == "" {
		line("  paca-postgres:")
		line("    image: postgres:16-alpine")
		line("    environment:")
		line("      POSTGRES_USER:     ${PACA_PG_USER}")
		line("      POSTGRES_PASSWORD: ${PACA_PG_PASSWORD}")
		line("      POSTGRES_DB:       ${PACA_PG_DB}")
		line("    volumes: [paca_pg_data:/var/lib/postgresql/data]")
	}

	line("  paca-valkey:")
	line("    image: valkey/valkey:8")

	if overrides.UseExternalS3 == "" {
		line("  paca-minio:")
		line("    image: minio/minio:latest")
		line("    environment:")
		line("      MINIO_ROOT_USER:     ${PACA_S3_KEY}")
		line("      MINIO_ROOT_PASSWORD: ${PACA_S3_SECRET}")
		line("    command: server /data")
	}

	line("  paca-nginx:")
	line("    image: nginx:1.27-alpine")

	if !overrides.SkipAIAgent {
		line("  paca-ai:")
		line("    image: bhengubv/paca-ai:latest")
		line("    env_file: [.env]")
	}

	if overrides.UseExternalPostgres == "" {
		line("volumes:")
		line("  paca_pg_data: {}")
	}

	env, err := buildPacaEnvFile(mode, overrides)
	if err != nil {
		return PacaDeployArtifact{}, err
	}
	return PacaDeployArtifact{ComposeYAML: sb.String(), EnvFile: env}, nil
}

// BuildInstallPluginScript builds the bash install-plugin script. Ports
// PacaDeployer.BuildInstallPluginScript. Returns an error if pluginName is blank.
func BuildInstallPluginScript(pluginName string) (string, error) {
	if strings.TrimSpace(pluginName) == "" {
		return "", errors.New("pluginName required")
	}
	return "#!/usr/bin/env bash\n" +
		"set -euo pipefail\n" +
		"echo \"[paca] Building WASM module for " + pluginName + "...\"\n" +
		"wasm-pack build --target web ./plugins/" + pluginName + "\n" +
		"echo \"[paca] Building frontend bundle...\"\n" +
		"cd ./plugins/" + pluginName + "/frontend && pnpm install && pnpm build\n" +
		"cd -\n" +
		"echo \"[paca] Registering plugin with the API...\"\n" +
		"paca-cli plugins install ./plugins/" + pluginName + "/dist\n" +
		"echo \"[paca] Done.\"", nil
}

// BuildUninstallPluginScript builds the bash uninstall-plugin script. Ports
// PacaDeployer.BuildUninstallPluginScript. Returns an error if pluginName is blank.
func BuildUninstallPluginScript(pluginName string) (string, error) {
	if strings.TrimSpace(pluginName) == "" {
		return "", errors.New("pluginName required")
	}
	return "#!/usr/bin/env bash\n" +
		"set -euo pipefail\n" +
		"echo \"[paca] Uninstalling " + pluginName + "...\"\n" +
		"paca-cli plugins uninstall " + pluginName + "\n" +
		"rm -rf ./plugins/" + pluginName + "/dist\n" +
		"echo \"[paca] Done.\"", nil
}

func buildPacaEnvFile(mode PacaDeployMode, overrides PacaDeployOverrides) (string, error) {
	var sb strings.Builder
	line := func(s string) { sb.WriteString(s); sb.WriteString("\n") }

	pgPw, err := randomSecret(32)
	if err != nil {
		return "", err
	}
	s3Key, err := randomSecret(20)
	if err != nil {
		return "", err
	}
	s3Secret, err := randomSecret(40)
	if err != nil {
		return "", err
	}
	jwtSecret, err := randomSecret(48)
	if err != nil {
		return "", err
	}

	line("PACA_MODE=" + mode.lower())
	line("PACA_PG_USER=paca")
	line("PACA_PG_PASSWORD=" + pgPw)
	line("PACA_PG_DB=paca")
	if overrides.UseExternalPostgres != "" {
		line("PACA_PG_URL=" + overrides.UseExternalPostgres)
	}
	line("PACA_VALKEY_URL=redis://paca-valkey:6379")
	line("PACA_S3_KEY=" + s3Key)
	line("PACA_S3_SECRET=" + s3Secret)
	if overrides.UseExternalS3 != "" {
		line("PACA_S3_ENDPOINT=" + overrides.UseExternalS3)
	}
	line("PACA_JWT_SIGNING_SECRET=" + jwtSecret)
	line("PACA_AI_ENABLED=" + strconv.FormatBool(!overrides.SkipAIAgent))
	return sb.String(), nil
}

// randomSecret returns a URL-safe base64 secret truncated to length chars.
// Ports PacaDeployer.RandomSecret. It draws length bytes (as the C# does), then
// draws extra bytes if needed to guarantee at least length base64 chars before
// truncation (base64 of length bytes is always >= length chars, so one draw
// suffices, but the loop is defensive).
func randomSecret(length int) (string, error) {
	if length <= 0 {
		return "", nil
	}
	need := length
	for {
		buf := make([]byte, need)
		if _, err := rand.Read(buf); err != nil {
			return "", err
		}
		s := base64.RawURLEncoding.EncodeToString(buf)
		if len(s) >= length {
			return s[:length], nil
		}
		need++
	}
}
