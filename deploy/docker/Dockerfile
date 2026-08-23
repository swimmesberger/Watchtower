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

# Non-root user. /data no longer holds any state Watchtower needs: since ADR-0024 the keys, the
# data-protection key ring, the ACME account and the issued certificates are rows in PostgreSQL like
# everything else, so any instance can serve any host. The mount stays for the one upgrade that needs
# it — a pre-upgrade watchtower.db until `--import-sqlite` carries it across, and the old
# /data/auth-keys and /data/proxy-certs until the first normal start imports them
# (docs/upgrading.md). A fresh install can drop it.
RUN groupadd -r watchtower && useradd -r -g watchtower watchtower \
    && mkdir -p /data && chown watchtower:watchtower /data

WORKDIR /app
COPY --from=build /app/publish/ ./
COPY --from=web /repo/src/watchtower-web/dist/ ./wwwroot/

VOLUME /data

# The management plane is a Kestrel endpoint baked into the image; the public ingress is not (ADR-0022).
#   Http       — 8080, the MANAGEMENT plane: Watchtower's own UI and API. Never publish this to the
#                internet. Bind it to a private interface (127.0.0.1:8080:8080) and reach the UI from
#                outside through a login host over 443 instead. A named endpoint rather than
#                ASPNETCORE_URLS, which this image ignores; move it with
#                Kestrel__Endpoints__Http__Url=http://+:9000.
#   ProxyHttp  — 8081 by default, plain-HTTP INGRESS: ACME HTTP-01 validation and the plain half of the
#                proxy. Published as 80:8081.
#   ProxyHttps — 8443 by default, TLS INGRESS: the reverse proxy's TLS listener, which picks a
#                certificate per connection from the SNI name. Published as 443:8443.
# The two ingress listeners are REVERSE-PROXY SETTINGS, not image configuration: they exist only while
# the built-in provider is active, and their ports are WATCHTOWER__PROXY__YARP__HTTPPORT and
# __HTTPSPORT (0 turns one off). Enabling the proxy, switching provider or moving a port binds and
# unbinds them at runtime — no restart. Setting either variable here pins it, like every other
# WATCHTOWER__* setting; leaving both unset lets the Settings page own them. A stray
# Kestrel__Endpoints__ProxyHttp__Url / __ProxyHttps__Url — the pre-addendum knob — is ignored: the app
# masks those keys out of the configuration Kestrel reads.
# On the ingress endpoints a request whose Host is not a route (or a realm login host) is answered with
# 404 and never reaches Watchtower's own pipeline. Nothing can reach a listener unless the operator
# publishes its port — see the commented ingress ports in docker-compose.yml.
# EXPOSE documents the defaults; the ingress two are only listening while the proxy is on.
EXPOSE 8080 8081 8443
ENV Kestrel__Endpoints__Http__Url=http://+:8080

# The docker.sock must be mounted at runtime; grant the watchtower user the host's docker GID
# (e.g. `group_add: ["999"]` in compose) so it can talk to the daemon.
USER watchtower

ENTRYPOINT ["dotnet", "Watchtower.Api.dll"]
