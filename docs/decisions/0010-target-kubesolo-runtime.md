# ADR-0010: The future runtime target is the Kubernetes API, via KubeSolo

- Status: Accepted
- Date: 2026-08-10
- Related: [ADR-0001](0001-rebuild-on-elarion.md), [ADR-0008](0008-public-app-api.md),
  [ADR-0009](0009-public-management-api.md)

## Context

Watchtower's runtime today is the Docker daemon driven through Compose: the deploy engine clones a
repo and shells out to `docker compose pull` / `up -d`, isolation and every container lookup hang off
the compose project label, the reverse proxy joins target containers to an edge network, logs come
from the Docker API, and teardown (ADR-0009) is `docker compose down`. That choice was never recorded
as an ADR — it was the water Watchtower swam in from day one.

Two things have changed. First, [docs/scaling-beyond-one-node.md](../scaling-beyond-one-node.md)
examined the multi-node future and concluded that Swarm is maintained-but-frozen while the industry's
gravity is the Kubernetes API — but adopting k3s means accepting cluster machinery (etcd or an
embedded store, leader election, overlay networking) that a single-host tool never uses. Second,
**KubeSolo** ([kubesolo.io](https://www.kubesolo.io/), by Portainer, MIT-licensed, v1.0.x as of
mid-2026) removes that trade-off: single-node Kubernetes with the clustering machinery *removed*
rather than disabled — API server, scheduler, controller manager and kubelet in one process,
claiming <200 MB RAM at idle, full Kubernetes API compatibility (manifests, Helm charts, CRDs), on
ARM/ARM64/x86_64/RISC-V. *(Claims verified against kubesolo.io on 2026-08-10; volatile — re-check
before acting.)*

That makes the Kubernetes API viable at Watchtower's natural size — one box — while buying what
Compose structurally lacks: a declarative reconcile loop (the deploy engine's queue/coalesce logic is
a hand-built one), real health probes and rolling restarts, first-class namespaces for tenant
isolation (today approximated by compose project names), and an ecosystem exit ramp: the same
manifests move to k3s or a managed cluster if a deployment ever outgrows one node.

## Decision

**Watchtower's future runtime target is the Kubernetes API, with KubeSolo as the reference
distribution.** Docker + Compose remains the shipping runtime until that migration happens; this ADR
records direction, not a schedule.

The point of targeting the *API* rather than a distribution is deliberate: it makes Watchtower
**theoretically cluster-capable**. The Kubernetes API is identical on KubeSolo and on a multi-node
cluster, so nothing in Watchtower's runtime layer should structurally rule a cluster out — while its
**features continue to target single-node workloads**, which is where the product actually lives. In
practice Watchtower runs on KubeSolo; the API choice is what keeps the bigger future open without
building for it now. KubeSolo is the practical deployment, not a ceiling.

What the direction means for code written from now on:

- **New runtime-facing code goes behind seams.** The Compose/Docker specifics are already
  concentrated in a few services (`ComposeCliService`, `DockerEngineClient`, `DeployQueueService`,
  `CaddyManager`, the provisioning/teardown services). Keep it that way: nothing outside
  `Services/` should learn Docker concepts, and new features should not deepen the coupling where a
  neutral shape costs nothing.
- **Domain abstractions are already runtime-neutral and must stay so.** Stack, template, tenant,
  route, grant, deploy event — none of these name Docker. The public surfaces (App API, Management
  API) expose status vocabularies and service/container shapes that map cleanly onto pods; their
  contracts must not grow Docker-specific fields.
- **The tenant model maps onto namespaces.** A tenant (today: a compose project) becomes a
  namespace; the compose-project-label scoping that ADR-0008/0009 lean on becomes namespace/label
  scoping. Anything new that assumes "project name" as the isolation key should treat it as an
  opaque runtime scope identifier.

## Consequences

- The **migration surface is enumerable and bounded**: the deploy engine (compose CLI → applying
  manifests/Helm through the Kube API), container introspection and log streaming (Docker API → pod
  API), the reverse proxy (edge-network joining → Services/Ingress, or Caddy fronting ClusterIPs),
  teardown (compose down → namespace deletion, which is also transactional in a way compose never
  was), volumes, and self-update (the coordinator-sibling dance becomes a Deployment rollout).
- The **stack definition question is opened, not answered**: stacks are "a git repo + a compose
  file" today. A Kube target means manifests or Helm charts (which KubeSolo runs unmodified), and
  possibly compose-to-manifest conversion for continuity. Deliberately deferred to the migration
  ADR.
- **KubeSolo is young** (1.0 in 2026) and single-vendor. The direction is really "the Kubernetes
  API"; KubeSolo is the distribution bet, and it is replaceable (k3s single-node would run the same
  manifests) if it stalls. Re-verify its state when the migration starts.
- The Swarm analysis in the scaling doc loses relevance; the k3s path becomes "same API, bigger
  distribution" instead of a rewrite.

### Rejected alternatives

- **Stay Compose-only indefinitely.** Simplest, but the hand-built reconcile/health/rollout logic
  grows without bound, and multi-node remains a rewrite instead of a redeploy.
- **Docker Swarm.** Lowest-friction from Compose, but maintained-not-evolving (see the scaling doc's
  verified findings); betting the future runtime on it points the wrong way.
- **k3s as the single-node target.** Same API, proven and multi-node capable — but it carries
  cluster machinery a one-box deployment never exercises; KubeSolo's single-process control loop is
  the better fit for Watchtower's "one Docker daemon, one SQLite file" philosophy. k3s remains the
  scale-up path, not the default.
- **Podman / systemd units.** Lighter still, but no reconcile loop, no ecosystem, and none of the
  namespace isolation the tenant model wants.
