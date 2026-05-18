# Multi-stage Dockerfile for dotnet-dbg-mcp server.
#
# Build:  docker build -t dotnet-dbg-mcp:dev -f deploy/Dockerfile .
# Run:    docker run --rm -p 8787:8080 -e MCP_BEARER_TOKEN=changeme dotnet-dbg-mcp:dev
#
# When deployed as a Kubernetes sidecar, the server attaches to the target .NET
# process via its Diagnostic IPC socket; see deploy/k8s/README.md for the
# pod-level requirements (shareProcessNamespace + shared emptyDir over /tmp).

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Bring in the files needed to restore first so the layer caches well.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/DotnetDbgMcp.Core/DotnetDbgMcp.Core.csproj src/DotnetDbgMcp.Core/
COPY src/DotnetDbgMcp.Server/DotnetDbgMcp.Server.csproj src/DotnetDbgMcp.Server/

RUN dotnet restore src/DotnetDbgMcp.Server/DotnetDbgMcp.Server.csproj

# Copy the rest of the source needed by the server project.
COPY src/ src/

RUN dotnet publish src/DotnetDbgMcp.Server/DotnetDbgMcp.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# Kestrel listens on 8080 by default in our ASPNETCORE_URLS below.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=1 \
    DOTNET_NOLOGO=1

# Non-root user for least privilege. The sidecar still needs PID visibility on the
# *target* container to use DiagnosticsClient — see deploy/k8s/README.md.
RUN groupadd --system --gid 10001 dbgmcp \
 && useradd --system --uid 10001 --gid dbgmcp --create-home --shell /usr/sbin/nologin dbgmcp

COPY --from=build /app/publish ./
RUN chown -R dbgmcp:dbgmcp /app
USER dbgmcp

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "DotnetDbgMcp.Server.dll"]
