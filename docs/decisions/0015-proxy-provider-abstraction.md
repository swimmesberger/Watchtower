# ADR-0015: Pluggable reverse-proxy provider — built-in Caddy, or a Cloudflare Tunnel

- Status: Accepted — extended by [ADR-0022](0022-in-process-yarp-proxy.md) (2026-08-22): `yarp` (in
  process) is the third provider and the default; `caddy` is deprecated.
- Date: 2026-08-17
- Related: [ADR-0007](0007-pluggable-metrics-backend.md) (the provider pattern this copies),
  [ADR-0014](0014-env-wins-runtime-settings.md) (the runtime settings this rides on),
  [docs/reverse-proxy/](../reverse-proxy/README.md) (the provider landing page),
  [docs/reverse-proxy/cloudflare.md](../reverse-proxy/cloudflare.md) (the new provider's guide).

## Context

The reverse-proxy plane was hard-wired to Caddy: `CaddyManager` was injected as a concrete type into
the deploy queue, tenant teardown, and seven handlers. That engine is right for a host with open
ports 80/443 — but a large class of self-hosted deployments (home NAS behind CGNAT, machines that
can't or shouldn't expose ports) fronts services with a **Cloudflare Tunnel** instead: `cloudflared`
makes outbound connections, TLS terminates at Cloudflare's edge, and per-hostname routing plus
Zero Trust access live in the Cloudflare account. Watchtower's `routes` table already models exactly
what a tunnel's ingress rules and DNS records need — the operator was just left to click them together
in the dashboard by hand.

The codebase already has a proven shape for "same feature, switchable backend": the metrics plane
(ADR-0007) registers every backend unconditionally, routes per call off `IOptionsMonitor`, and lets a
runtime settings change re-route the next call.

## Decision

1. **`IProxyProvider`** (`Enabled`, `ApplyAsync`, `ConnectStackAsync`, `IsRunningAsync`) is the seam
   consumers inject; `ProxyProviderRouter` resolves the selected backend per call from
   `Proxy:Provider` (`caddy` default | `cloudflare` — ADR-0022 added `yarp` and made it the default).
   Providers are resolved from the container, so a test substitute registered at the interface keeps
   working.
2. **Each provider self-gates on "enabled AND selected".** Both subscribe to the options monitor and
   compute their own `ProxyTransition` (shared pure helper) from every change — so a provider switch
   is a `Stop` on one side and a `Start` on the other, atomically driven by one settings write, with
   no restart. Teardown removes only the provider's own data plane: Caddy keeps certificates,
   Cloudflare keeps the tunnel and DNS records.
3. **The network topology is shared, not duplicated.** `ProxyIngressNetworks` (extracted from
   `CaddyManager`) owns the per-stack `watchtower-ingress-{stackId}` networks and the
   `{project}-{service}` aliases; whichever proxy container is active joins them. The route table
   stays the single source of truth for both projections.
4. **`CloudflareTunnelProvider`** finds (or creates) a remotely-managed tunnel by name, replaces its
   ingress rules with a projection of the route table (`hostname → http://{alias}:{port}`, terminal
   `http_status:404` catch-all), and upserts one proxied CNAME per route domain
   (`{tunnelId}.cfargotunnel.com`). The API client follows the `GitHubApiClient` pattern; the token
   is validated against the API before the settings persist.
5. **cloudflared is managed by default, unmanaged by choice.** Managed: Watchtower runs
   `watchtower-cloudflared` over the Docker socket, exactly like the Caddy container. Unmanaged: the
   operator runs cloudflared (anywhere); Watchtower only manages the tunnel's remote configuration
   and DNS, optionally connecting a named operator-run container to the ingress networks so the
   generated service URLs resolve.

## Consequences

- The seven concrete `CaddyManager` injection sites moved to `IProxyProvider`; `CaddyManager` remains
  registered concretely for its hosted lifecycle and the realm-host site projection.
- On the cloudflare provider, Watchtower's forward-auth plane does not run in front of routes —
  access control belongs to Cloudflare (Zero Trust Access applications are phase 3, projected from
  `Route.AccessMode`). Apps see `Cf-Access-Jwt-Assertion`, not `X-Watchtower-*` headers.
- Single-zone assumption: all route domains must live under the configured `ZoneId`; a domain outside
  it fails its DNS upsert (logged, best-effort) while the rest proceed.
- The Cloudflare API token lives in the settings store (env-pinnable per ADR-0014), consistent with
  the InfluxDB token precedent — not in a `Credential` row, which is scoped to git/registry auth.
- `proxy.getStatus` gained a `provider` field; `caddyRunning` keeps its wire name but reports the
  active provider's data plane.
