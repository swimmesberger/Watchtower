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
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg git \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc \
    && chmod a+r /etc/apt/keyrings/docker.asc \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
        https://download.docker.com/linux/debian $(. /etc/os-release && echo \"$VERSION_CODENAME\") stable" \
        > /etc/apt/sources.list.d/docker.list \
    && apt-get update && apt-get install -y --no-install-recommends \
        docker-ce-cli docker-compose-plugin \
    && apt-get purge -y curl gnupg && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# Non-root user; /data holds the SQLite database (a named volume).
RUN groupadd -r watchtower && useradd -r -g watchtower watchtower \
    && mkdir -p /data && chown watchtower:watchtower /data

WORKDIR /app
COPY --from=build /app/publish/ ./
COPY --from=web /repo/src/watchtower-web/dist/ ./wwwroot/

VOLUME /data
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# The docker.sock must be mounted at runtime; grant the watchtower user the host's docker GID
# (e.g. `group_add: ["999"]` in compose) so it can talk to the daemon.
USER watchtower

ENTRYPOINT ["dotnet", "Watchtower.Api.dll"]
