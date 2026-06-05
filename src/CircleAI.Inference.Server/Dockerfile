# syntax=docker/dockerfile:1.6
#
# CircleAI.Inference.Server — multi-stage Dockerfile.
#
# Build stage uses the .NET 9 SDK; runtime stage uses the ASP.NET 9 runtime
# image (Debian slim). The MNN native runtime is NOT baked in — the server
# fetches the right bundle on first launch via NativeRuntimeFetcher, so the
# image stays platform-correct on x86_64 and arm64 without per-arch tags.
#
# Build:    docker build -t circleai/inference-server:latest .
# Run:      docker run --rm -p 8080:8080 \
#               -v circleai-data:/data \
#               -e ASPNETCORE_URLS=http://+:8080 \
#               -e CircleAIServer__RuntimeCacheRoot=/data/runtime \
#               -e CircleAIServer__ModelStorageRoot=/data/models \
#               circleai/inference-server:latest
# Health:   curl http://localhost:8080/v1/healthz

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy only the project + its references — keeps the build cache hot on
# changes that don't touch project files.
COPY ["src/CircleAI.Core/CircleAI.Core.csproj",                                 "src/CircleAI.Core/"]
COPY ["src/CircleAI.Inference/CircleAI.Inference.csproj",                       "src/CircleAI.Inference/"]
COPY ["src/CircleAI.Embeddings/CircleAI.Embeddings.csproj",                     "src/CircleAI.Embeddings/"]
COPY ["src/CircleAI.Hosting.InferenceBridge/CircleAI.Hosting.InferenceBridge.csproj", "src/CircleAI.Hosting.InferenceBridge/"]
COPY ["src/CircleAI.Runtime/CircleAI.Runtime.csproj",                           "src/CircleAI.Runtime/"]
COPY ["src/CircleAI.Companion/CircleAI.Companion.csproj",                       "src/CircleAI.Companion/"]
COPY ["src/CircleAI.Inference.Server/CircleAI.Inference.Server.csproj",         "src/CircleAI.Inference.Server/"]

RUN dotnet restore "src/CircleAI.Inference.Server/CircleAI.Inference.Server.csproj"

# Now bring in the rest of the source for the actual build.
COPY src/ src/

RUN dotnet publish \
    "src/CircleAI.Inference.Server/CircleAI.Inference.Server.csproj" \
    -c Release \
    --no-restore \
    -o /publish

# ── Runtime image ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create a non-root user to run the service.
RUN useradd --create-home --shell /bin/false circleai \
    && mkdir -p /data/runtime /data/models \
    && chown -R circleai:circleai /data
USER circleai

COPY --from=build --chown=circleai:circleai /publish ./

ENV ASPNETCORE_URLS=http://+:8080
ENV CircleAIServer__RuntimeCacheRoot=/data/runtime
ENV CircleAIServer__ModelStorageRoot=/data/models
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# k8s/containerd liveness + readiness probes line up with these.
HEALTHCHECK --interval=15s --timeout=3s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/v1/healthz || exit 1

ENTRYPOINT ["dotnet", "CircleAI.Inference.Server.dll"]
