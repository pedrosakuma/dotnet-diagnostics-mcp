# Dockerfile for BadCodeSample.
# Build from repo root:
#   docker build -t badcode-sample:dev -f samples/BadCodeSample/Dockerfile .

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY samples/Directory.Build.props samples/
COPY samples/BadCodeSample/ samples/BadCodeSample/

RUN dotnet publish samples/BadCodeSample/BadCodeSample.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=1 \
    DOTNET_NOLOGO=1

COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "BadCodeSample.dll"]
