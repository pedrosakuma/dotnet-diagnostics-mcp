# Multi-stage Dockerfile for dotnet-diagnostics-mcp server.
#
# Build:  docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .
# Run:    docker run --rm -p 8787:8080 -e MCP_BEARER_TOKEN=changeme dotnet-diagnostics-mcp:dev
#
# When deployed as a Kubernetes sidecar, the server attaches to the target .NET
# process via its Diagnostic IPC socket; see deploy/k8s/README.md for the
# pod-level requirements (shareProcessNamespace + shared emptyDir over /tmp).

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Bring in the files needed to restore first so the layer caches well.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/DotnetDiagnosticsMcp.Core/DotnetDiagnosticsMcp.Core.csproj src/DotnetDiagnosticsMcp.Core/
COPY src/DotnetDiagnosticsMcp.Server/DotnetDiagnosticsMcp.Server.csproj src/DotnetDiagnosticsMcp.Server/

RUN dotnet restore src/DotnetDiagnosticsMcp.Server/DotnetDiagnosticsMcp.Server.csproj

# Copy the rest of the source needed by the server project.
COPY src/ src/

RUN dotnet publish src/DotnetDiagnosticsMcp.Server/DotnetDiagnosticsMcp.Server.csproj \
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
RUN groupadd --system --gid 10001 diagnosticsmcp \
 && useradd --system --uid 10001 --gid diagnosticsmcp --create-home --shell /usr/sbin/nologin diagnosticsmcp

# Install elfutils (eu-stack) for Linux NativeAOT thread snapshots, plus Linux perf
# (linux-tools-common wrapper + linux-tools-generic kernel-versioned binary) for
# NativeAOT CPU sampling fallback and off-CPU sched capture. Ubuntu Noble (the .NET 10
# base) does not ship the transitional `linux-perf` meta-package; PerfBinaryResolver
# iterates /usr/lib/linux-tools-* to locate the binary, so a host-kernel mismatch is
# benign for our usage.
#
# At runtime the container additionally needs CAP_PERFMON (or perf_event_paranoid <= 1
# on the host) for perf to actually collect — see deploy/k8s/ephemeral-attach.patch.json
# for the K8s recipe.
#
# Pass --build-arg INSTALL_PERF=false to opt OUT (keeps the image ~80MB smaller but
# disables collect_off_cpu_sample and the perf-replay thread-snapshot fallback).
ARG INSTALL_PERF=true
RUN apt-get update \
 && apt-get install -y --no-install-recommends elfutils \
 && if [ "$INSTALL_PERF" = "true" ]; then \
        apt-get install -y --no-install-recommends \
            linux-tools-common linux-tools-generic; \
    fi \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
RUN chown -R diagnosticsmcp:diagnosticsmcp /app
USER diagnosticsmcp

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "DotnetDiagnosticsMcp.Server.dll"]
