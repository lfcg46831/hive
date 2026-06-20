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

# -----------------------------------------------------------------------------
# Runtime configuration contract (US-F0-02-T02).
#
# The image declares image-level defaults that are safe for every deployment and
# documents the per-deployment overrides supplied by Docker Compose (US-F0-02-T03+).
# All settings follow the .NET hierarchical env-var convention (`__` is the
# section separator, `__0` indexes array entries), so they map onto the same
# `appsettings`/`Hive:*` contract from §5.10 of the bible without code changes.
#
# Image-level defaults set below:
#   * Environment      -> Production, so the container never picks up the local
#                         all-in-one `appsettings.Development.json` role override;
#                         each host falls back to its own per-executable role set.
#   * HTTP port (api)  -> 8080, the Kestrel listen port for /health and
#                         /diagnostics. Harmless on the non-HTTP worker host.
#   * Akka cluster port-> 8081, the remoting/cluster bind port (Hive:Cluster:Port).
#
# Per-deployment overrides (NOT pinned here; provided by compose/env per service):
#   * HIVE__NODE__ROLES__0=<role>          Active node role(s). Defaults come from
#                                          each host's appsettings.json; compose
#                                          assigns roles per service (US-F0-02-T05).
#   * ConnectionStrings__PostgreSql=<conn> Required dependency. Left empty on
#                                          purpose: readiness stays not-ready until
#                                          an operator/compose supplies it (no
#                                          baked-in credentials).
#   * HIVE__CLUSTER__HOSTNAME=<dns-name>   Reachable name other nodes dial; the
#                                          stable compose DNS name in multi-node
#                                          topologies (US-F0-02-T06).
#   * HIVE__CLUSTER__SEEDNODES__0=akka.tcp://hive@<host>:<port>
#                                          Join target(s). When empty the node
#                                          self-seeds a single-node cluster.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    HIVE__CLUSTER__PORT=8081

# Document the ports the node may serve on: HTTP (api host) and Akka cluster.
# EXPOSE is metadata only; compose decides which ports are actually published.
EXPOSE 8080 8081

# Run as the unprivileged user that ships with the .NET runtime image.
USER app

# Shell form with `exec` so `dotnet` replaces the shell as PID 1 and receives
# SIGTERM/SIGINT for a graceful node shutdown.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"${APP_DLL}\""]
