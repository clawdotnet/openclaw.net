# P0 sidecar host image. Builds + runs the Strategos event-sourced saga sidecar that
# speaks OpenClaw's maf-durable-http contract on port 8080.
#
# Build:   docker build -t openclaw-strategos-host .
# Run:     docker run --rm -p 8080:8080 \
#             -e ConnectionStrings__Postgres="Host=host.docker.internal;Port=5432;Database=openclaw_strategos;Username=openclaw;Password=openclaw" \
#             -e Llm__Mode=Mock openclaw-strategos-host

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj first so the package restore layer is cached.
COPY samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj samples/OpenClaw.StrategosWorkflowHost/
COPY src/OpenClaw.Core/OpenClaw.Core.csproj src/OpenClaw.Core/
RUN dotnet restore samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj

# Copy the rest and publish.
COPY samples/OpenClaw.StrategosWorkflowHost/ samples/OpenClaw.StrategosWorkflowHost/
COPY src/OpenClaw.Core/ src/OpenClaw.Core/
RUN dotnet publish samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenClaw.StrategosWorkflowHost.dll"]