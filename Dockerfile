# syntax=docker/dockerfile:1

# Multi-stage Dockerfile for building and running a HIVE .NET node (US-F0-02-T01).
#
# Scope of this task: produce a reproducible build stage and a slim runtime image
# for the .NET 8 application. Runtime environment variables, exposed ports and the
# Akka/role wiring are layered on top of this image in US-F0-02-T02+; they are kept
# out of this file on purpose.
#
# The same Dockerfile builds either executable host:
#   * Hive.Api    (default) - web host, serves /health and /diagnostics, runs any role.
#   * Hive.Worker (override) - non-HTTP host for the worker roles.
# Build the worker with:
#   docker build \
#     --build-arg APP_PROJECT=src/Hive.Worker/Hive.Worker.csproj \
#     --build-arg APP_DLL=Hive.Worker.dll .

ARG DOTNET_VERSION=8.0

# -----------------------------------------------------------------------------
# Build stage: restore and publish a framework-dependent build with the SDK.
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG APP_PROJECT=src/Hive.Api/Hive.Api.csproj
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy only the solution and project manifests first so that the NuGet restore
# layer is cached and reused while application source code changes.
COPY Hive.sln ./
COPY src/Hive.Domain/Hive.Domain.csproj src/Hive.Domain/
COPY src/Hive.Actors/Hive.Actors.csproj src/Hive.Actors/
COPY src/Hive.Infrastructure/Hive.Infrastructure.csproj src/Hive.Infrastructure/
COPY src/Hive.Api/Hive.Api.csproj src/Hive.Api/
COPY src/Hive.Worker/Hive.Worker.csproj src/Hive.Worker/
COPY tests/Hive.Tests/Hive.Tests.csproj tests/Hive.Tests/
RUN dotnet restore "${APP_PROJECT}"

# Copy the application sources and publish. UseAppHost=false skips the native
# launcher; the runtime stage invokes the managed entry assembly with `dotnet`.
COPY src/ src/
RUN dotnet publish "${APP_PROJECT}" \
    --configuration "${BUILD_CONFIGURATION}" \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

# -----------------------------------------------------------------------------
# Runtime stage: copy the published output onto the ASP.NET Core runtime image.
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
ARG APP_DLL=Hive.Api.dll
ENV APP_DLL=${APP_DLL}
WORKDIR /app
COPY --from=build /app/publish ./

# Run as the unprivileged user that ships with the .NET runtime image.
USER app

# Shell form with `exec` so `dotnet` replaces the shell as PID 1 and receives
# SIGTERM/SIGINT for a graceful node shutdown.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"${APP_DLL}\""]
