# ADR-0030: Host device mappings are a per-stack setting, injected through the generated override

## Status

Accepted

## Context

Some workloads need a host device inside a container — the canonical case is GPU-accelerated
transcoding via `/dev/dri/renderD128`. Compose expresses this with the service-level `devices:` key,
but the value is inherently **host-specific**: which render node exists (and whether one exists at
all) differs per machine, while the compose file in the product's repository is shared by every stack
of that product on every host (ADR-0026). Committing a device path to the repository either breaks
the deployments that lack the device or forces every consumer to define pass-through variables for
something that is not the application's concern.

Watchtower already has exactly one mechanism for adding configuration to somebody else's compose file
without touching the repository: the generated override file merged in as a second `--file`
(ADR-0012), which today carries injected environment variables and release image pins (ADR-0026
decision 6). And it has a house precedent for per-service UI configuration: the backup service
overrides of ADR-0020, stored per `(stack, service)` and replaced whole on save.

Compose's merge rule for `devices:` (like `volumes:`) merges entries **by container path**: entries
in a later file with a new target are appended, and an entry with the same target replaces the
earlier one. So an override file can add devices to a service that declares none, and coexist with a
repository that declares some.

## Decision

1. **Device mappings are stored per stack, keyed by compose service name** — a
   `stack_device_mappings` row per device: service, host path, container path, optional cgroup
   permissions (`r`/`w`/`m`). The set is replaced atomically via `stacks.setDevices` (the
   `stacks.setEnv` shape), read via `stacks.getDevices`, and edited in the stack's Settings tab. Rows
   are keyed by service name, not container id, so they survive redeploys and apply to every replica.

2. **The deploy renders them into the ADR-0012 generated override** as a `devices:` list under the
   service. The policy half is a `DeviceMappingPlan` — runtime-neutral per ADR-0010's seam rule: it
   names no Compose concept, so a future Kubernetes engine could apply the same plan as
   `volumeDevices`/CDI annotations — and only `ComposeOverrideFile` knows what YAML it becomes.

3. **A mapping for a service the resolved project does not contain is a warning, not a failure** —
   the same tolerance as image pinning: services come and go with the repository, and failing the
   deploy would break a fleet over a leftover row. The warning lands in the deploy output; so does
   one line per applied device, so "why does this container see the GPU" is answerable from the
   deploy log alone.

4. **On a container-path collision, the Watchtower mapping wins** (Compose's own merge semantics).
   This deliberately inverts ADR-0020's "labels win": device paths are per-host facts, and the
   per-host value must be able to override a repository default — the repo may declare a generic
   `/dev/dri`, one host may need a specific card. The deploy log names every applied device, so
   nothing is silently overridden. ADR-0014's actual hazard — a UI edit that silently never takes
   effect — cannot occur here, because the override always applies.

5. **No template-level twin, no NVIDIA `gpus`/CDI support for now.** Device paths are host-specific,
   which is the opposite of what a template shares across tenants; a fleet-wide default can be added
   later if a real need appears. NVIDIA GPUs want `deploy.resources.reservations.devices` (a
   different mechanism); out of scope until asked for.

## Consequences

- A host GPU (or serial port, TPU, …) reaches a stack's container with zero repository changes, and
  the same repository deploys unchanged on hosts without the device.
- The mapping grants the container access to a host device node — an operator-level capability, so
  the change is audit-logged like other stack lifecycle operations.
- The generated override is no longer byte-identical to its pre-device form only when mappings exist;
  a stack with none renders exactly what it rendered before (the ADR-0012/0026 invariant holds).
- Devices configured here are invisible to `docker compose` invocations made outside Watchtower —
  consistent with ADR-0012, which already accepted that Watchtower owns the whole invocation.
