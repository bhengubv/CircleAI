# CircleAI Inference Server — Deploy guide

The server ships three first-class deployment paths: Docker / Kubernetes,
systemd on Linux, and Windows service. All three boot the same binary;
the host process detects which lifecycle to honour at startup.

---

## 1. Docker

### Build

```bash
docker build -f src/CircleAI.Inference.Server/Dockerfile \
  -t circleai/inference-server:1.2.0 .
```

### Run

```bash
docker run --rm -d \
  --name circleai-inference-server \
  -p 8080:8080 \
  -v circleai-data:/data \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e CircleAIServer__RuntimeCacheRoot=/data/runtime \
  -e CircleAIServer__ModelStorageRoot=/data/models \
  -e CircleAIServer__Auth__ApiKey__Keys__0='<your-api-key>' \
  circleai/inference-server:1.2.0
```

Health probe:

```bash
curl http://localhost:8080/v1/healthz   # {"status":"alive","at":...}
curl http://localhost:8080/v1/readyz    # 503 until at least one model loaded
```

### Kubernetes

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: circleai-inference-server
spec:
  replicas: 2
  selector: { matchLabels: { app: circleai } }
  template:
    metadata: { labels: { app: circleai } }
    spec:
      containers:
        - name: server
          image: circleai/inference-server:1.2.0
          ports: [{ containerPort: 8080 }]
          envFrom:
            - secretRef: { name: circleai-keys }
          env:
            - { name: CircleAIServer__RuntimeCacheRoot, value: /data/runtime }
            - { name: CircleAIServer__ModelStorageRoot, value: /data/models }
          volumeMounts:
            - { mountPath: /data, name: circleai-data }
          livenessProbe:
            httpGet: { path: /v1/healthz, port: 8080 }
            initialDelaySeconds: 10
            periodSeconds: 10
          readinessProbe:
            httpGet: { path: /v1/readyz, port: 8080 }
            initialDelaySeconds: 5
            periodSeconds: 5
          resources:
            requests: { cpu: "1", memory: "4Gi" }
            limits:   { cpu: "4", memory: "16Gi" }
      volumes:
        - name: circleai-data
          persistentVolumeClaim: { claimName: circleai-data }
```

The `circleai-keys` secret should contain the API keys under
`CircleAIServer__Auth__ApiKey__Keys__0`, `…__1`, etc. Use the
double-underscore form so the .NET configuration provider maps them
into the option tree.

### GPU pass-through (NVIDIA / CUDA)

Run with the NVIDIA Container Toolkit:

```bash
docker run --rm --gpus all -p 8080:8080 \
  -v circleai-data:/data \
  -e CircleAIServer__RuntimeCacheRoot=/data/runtime \
  circleai/inference-server:1.2.0
```

`CapabilityProbe` then sees the GPU through `nvidia-smi`, picks the
Cuda backend, and `NativeRuntimeFetcher` downloads
`mnn-{version}-linux-x64-cuda.tar.gz` on first launch.

---

## 2. systemd (bare-metal Linux)

### Install

```bash
# Publish a self-contained binary
dotnet publish src/CircleAI.Inference.Server -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -o /tmp/circleai-publish

sudo install -d /usr/local/lib/circleai
sudo cp -r /tmp/circleai-publish/* /usr/local/lib/circleai/
sudo install -m 0755 /usr/local/lib/circleai/CircleAI.Inference.Server \
  /usr/local/bin/CircleAI.Inference.Server

# Service unit + data dir
sudo useradd -r -s /usr/sbin/nologin circleai 2>/dev/null || true
sudo install -d -o circleai -g circleai /var/lib/circleai/runtime /var/lib/circleai/models
sudo install -m 0644 \
  src/CircleAI.Inference.Server/systemd/circleai-inference-server.service \
  /etc/systemd/system/

sudo systemctl daemon-reload
sudo systemctl enable --now circleai-inference-server
```

### Inspect

```bash
sudo systemctl status circleai-inference-server
sudo journalctl -u circleai-inference-server -f
curl http://localhost:8080/v1/healthz
```

### Customise

The service unit accepts override drop-ins in
`/etc/systemd/system/circleai-inference-server.service.d/`:

```ini
# /etc/systemd/system/circleai-inference-server.service.d/api-keys.conf
[Service]
Environment=CircleAIServer__Auth__ApiKey__Keys__0=prod-key-AAA
Environment=CircleAIServer__Auth__ApiKey__Keys__1=prod-key-BBB
```

Reload after editing:

```bash
sudo systemctl daemon-reload
sudo systemctl restart circleai-inference-server
```

---

## 3. Windows service

### Install

PowerShell (elevated):

```powershell
# Publish
dotnet publish src\CircleAI.Inference.Server -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -o C:\Program Files\CircleAI

# Install the service (runs the bundled PS1)
cd C:\Program Files\CircleAI
.\windows\install-windows-service.ps1 -Action Install `
  -BinaryPath "C:\Program Files\CircleAI\CircleAI.Inference.Server.exe"
```

### Inspect

```powershell
Get-Service CircleAI.Inference.Server
Get-EventLog -LogName Application -Source "CircleAI.Inference.Server" -Newest 20
```

### Customise (registry-backed env vars)

The install script writes a `MultiString` `Environment` value under
`HKLM:\SYSTEM\CurrentControlSet\Services\CircleAI.Inference.Server`. Edit
that to change API keys / paths:

```powershell
$svc  = 'HKLM:\SYSTEM\CurrentControlSet\Services\CircleAI.Inference.Server'
$envv = @(
  "ASPNETCORE_URLS=http://0.0.0.0:8080",
  "ASPNETCORE_ENVIRONMENT=Production",
  "CircleAIServer__RuntimeCacheRoot=C:\ProgramData\CircleAI\runtime",
  "CircleAIServer__ModelStorageRoot=C:\ProgramData\CircleAI\models",
  "CircleAIServer__Auth__ApiKey__Keys__0=prod-key-AAA"
)
Set-ItemProperty -Path $svc -Name Environment -Value $envv -Type MultiString
Restart-Service CircleAI.Inference.Server
```

### Uninstall

```powershell
.\install-windows-service.ps1 -Action Uninstall
```

---

## 4. Loading models

Hosted clients hit `/v1/admin/models/load`:

```bash
curl -X POST http://localhost:8080/v1/admin/models/load \
  -H "X-CircleAI-Api-Key: <your-api-key>" \
  -H "Content-Type: application/json" \
  -d '{
    "modelId": "qwen3-7b",
    "backend": "Cuda",
    "tier": "Tier2_Medium",
    "vramRequiredBytes": 6442450944,
    "ramRequiredBytes":  2147483648
  }'
```

This requires the host to have registered an `IBridgeFactory` that knows
how to materialise an `IInferenceBridge` for `(modelId, backend, tier)`.
The default `UnconfiguredBridgeFactory` returns 500 — production hosts
ship their own (typically wrapping a model-cache + Mnnbridge factory).

Unload:

```bash
curl -X DELETE http://localhost:8080/v1/admin/models/qwen3-7b \
  -H "X-CircleAI-Api-Key: <your-api-key>"
```

Diagnostics:

```bash
curl http://localhost:8080/v1/diagnostics \
  -H "X-CircleAI-Api-Key: <your-api-key>"
```

---

## 5. Hardening

- Default API key auth is **enabled**. Disable only in trusted networks
  (`Auth:ApiKey:Enabled=false`).
- The Dockerfile runs as a non-root `circleai` user.
- The systemd unit applies `ProtectSystem=strict`,
  `ProtectHome=true`, and pins `ReadWritePaths=/var/lib/circleai`.
- TLS is the operator's responsibility — front the server with nginx /
  Caddy / Cloud Run / ingress-nginx. The server does not terminate TLS
  itself.

---

## 6. Failure modes

| Symptom                                             | Cause                                                      | Fix                                                                    |
|-----------------------------------------------------|------------------------------------------------------------|------------------------------------------------------------------------|
| `503` from `/v1/chat/completions`                  | At `MaxConcurrentRequests` cap                              | Back off; raise cap; add replicas                                      |
| `507` from `/v1/admin/models/load`                 | Insufficient VRAM/RAM headroom                              | Unload smaller model first; pick lower tier                            |
| `500 factory_failed` on load                       | `IBridgeFactory.CreateAsync` threw                           | Check host logs; verify model file path + MNN backend availability     |
| First request takes 30–60 s                        | NativeRuntimeFetcher is downloading the MNN bundle           | Pre-warm by hitting `/v1/diagnostics` during deploy                    |
| `503` from `/v1/readyz`                            | No models registered yet                                    | Load at least one via `/v1/admin/models/load`                          |
| `504` mid-stream                                   | `RequestTimeoutSeconds` elapsed                              | Raise timeout; reduce `max_tokens`                                     |
| OpenTelemetry counters not appearing               | Host didn't register an OTLP exporter                         | Wire `services.AddOpenTelemetry().WithMetrics()` referencing `Meter "CircleAI"` |
