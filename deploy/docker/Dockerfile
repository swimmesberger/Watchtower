# syntax=docker/dockerfile:1

# ── Stage 1: build the React SPA ────────────────────────────────────────────────
# The prebuild step (npm run generate:rpc) reads ../../rpc-schema.json relative to the web
# directory, so the working tree is laid out to mirror the repo (/repo/src/watchtower-web).
FROM node:22-bookworm-slim AS web
WORKDIR /repo/src/watchtower-web
COPY src/watchtower-web/package*.json ./
RUN npm ci
COPY rpc-schema.json /repo/rpc-schema.json
COPY src/watchtower-web/ ./
RUN npm run build

# ── Stage 2: publish the .NET backend ───────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Watchtower.Application/ ./src/Watchtower.Application/
COPY src/Watchtower.Api/ ./src/Watchtower.Api/
RUN dotnet publish src/Watchtower.Api/Watchtower.Api.csproj -c Release -o /app/publish

# ── Stage 3: runtime with the Docker CLI + Compose plugin ────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# git (repository cloning) + the Docker CLI and Compose plugin (deploy execution).
# Uses Docker's official APT repository so `docker compose` tracks upstream.
# The .NET aspnet:10.0 image is Ubuntu (noble), so we use Docker's Ubuntu repo.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg git \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc \
    && chmod a+r /etc/apt/keyrings/docker.asc \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
        https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo \"$VERSION_CODENAME\") stable" \
        > /etc/apt/sources.list.d/docker.list \
    && apt-get update && apt-get install -y --no-install-recommends \
        docker-ce-cli docker-compose-plugin \
    && apt-get purge -y curl gnupg && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# Non-root user; /data holds the SQLite database and, when the built-in reverse proxy is enabled, the
# issued certificates and the ACME account key under /data/proxy-certs — so both survive a recreate in
# the same named volume, with no second mount to declare (ADR-0017).
RUN groupadd -r watchtower && useradd -r -g watchtower watchtower \
    && mkdir -p /data && chown watchtower:watchtower /data

WORKDIR /app
COPY --from=build /app/publish/ ./
COPY --from=web /repo/src/watchtower-web/dist/ ./wwwroot/

VOLUME /data

# Two Kestrel endpoints, named so each can be configured on its own:
#   Http       — the UI and the API, and the plain-HTTP half of the built-in reverse proxy.
#   ProxyHttps — the built-in reverse proxy's TLS listener, which picks a certificate per connection
#                from the SNI name. It is bound whether or not the proxy is enabled (a listener is a
#                process-lifetime thing), but nothing can reach it unless the operator publishes the
#                port — see the commented ingress ports in docker-compose.yml.
# Named endpoints rather than ASPNETCORE_URLS because the SNI callback can only be attached to a
# per-endpoint listener. Note the precedence this changes: configured endpoints OVERRIDE
# ASPNETCORE_URLS, which Kestrel now ignores with a warning. To move a port, override the variable
# by name (e.g. Kestrel__Endpoints__Http__Url=http://+:9000). Setting
# Kestrel__Endpoints__ProxyHttps__Url to an empty value turns the TLS listener off — the app
# filters the blank endpoint out before Kestrel reads it, and starts HTTP-only.
EXPOSE 8080 8443
ENV Kestrel__Endpoints__Http__Url=http://+:8080 \
    Kestrel__Endpoints__ProxyHttps__Url=https://+:8443

# The docker.sock must be mounted at runtime; grant the watchtower user the host's docker GID
# (e.g. `group_add: ["999"]` in compose) so it can talk to the daemon.
USER watchtower

ENTRYPOINT ["dotnet", "Watchtower.Api.dll"]
