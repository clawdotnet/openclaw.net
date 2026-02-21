# ── Stage 1: Build + Test + Publish (NativeAOT) ────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Install native AOT prerequisites (clang, zlib)
RUN apt-get update && \
    apt-get install -y --no-install-recommends clang zlib1g-dev && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy solution & props first for layer caching
COPY Directory.Build.props OpenClaw.Net.sln ./

# Copy csproj files individually for restore caching
COPY src/OpenClaw.Core/OpenClaw.Core.csproj      src/OpenClaw.Core/
COPY src/OpenClaw.Agent/OpenClaw.Agent.csproj     src/OpenClaw.Agent/
COPY src/OpenClaw.Channels/OpenClaw.Channels.csproj src/OpenClaw.Channels/
COPY src/OpenClaw.Gateway/OpenClaw.Gateway.csproj src/OpenClaw.Gateway/
COPY src/OpenClaw.Tests/OpenClaw.Tests.csproj     src/OpenClaw.Tests/

# Restore (cached unless csproj files change)
RUN dotnet restore OpenClaw.Net.sln

# Copy all source
COPY src/ src/

# Run tests
RUN dotnet test --no-restore --verbosity minimal -c Release

# Publish Gateway as NativeAOT single-file binary
RUN dotnet publish src/OpenClaw.Gateway/OpenClaw.Gateway.csproj \
    -c Release \
    -o /app \
    --no-restore

# ── Stage 2: Runtime (minimal, no SDK) ─────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime

# Non-root user (chiseled images use app user by default)
WORKDIR /app

COPY --from=build /app .

# Create memory directory with correct permissions
RUN mkdir -p /app/memory

# Default environment variables
ENV ASPNETCORE_URLS=http://+:18789 \
    OPENCLAW__BindAddress=0.0.0.0 \
    OPENCLAW__Port=18789 \
    OPENCLAW__Memory__StoragePath=/app/memory

EXPOSE 18789

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD ["/app/OpenClaw.Gateway", "--health-check"]

ENTRYPOINT ["/app/OpenClaw.Gateway"]
