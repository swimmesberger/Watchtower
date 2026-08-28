# ADR-0031: "Map host GPUs" is a per-service intent, resolved by probing the Docker host at deploy time

## Status

Accepted

## Context

ADR-0030 lets an operator map a host device like `/dev/dri/renderD128` into a stack's container by
literal path. For the case that motivated it — GPU-accelerated transcoding — literal paths are still
one notch too concrete:

- `renderD128` merely means "the first GPU in probe order". On a single-GPU host it is stable; the
  number is an implementation detail either way, and the operator should not need to know it.
- The value differs per host, so a literal path cannot be shared — which is why ADR-0030 rejected
  template-level mappings. An *intent* ("this service wants the host GPUs") is host-neutral and
  could be shared.
- Mapping the node is not always enough: the container's user must be in the device node's owning
  group (`render`/`video`), whose **GID differs per host**. This is the classic "device mapped but
  VAAPI still fails" trap, and no literal-path UI can solve it.

The kernel makes the concrete facts cheaply and *deterministically* discoverable. DRM render nodes
are always `/dev/dri/renderD<N>` (minors from 128), and per node sysfs reports the PCI vendor id
(`/sys/class/drm/renderD<N>/device/vendor` — `0x8086` Intel, `0x1002` AMD, `0x10de` NVIDIA), the
bound driver (`uevent`, e.g. `i915`, `amdgpu`), and the PCI address; `stat` on the node gives the
owning group's GID. Watchtower's own container does not see the host's `/dev` — but the backup
feature already established the pattern for that: a short-lived helper container (ADR-0016's
`busybox:stable`, operator-configurable) with the needed paths bind-mounted.

NVIDIA is the deliberate odd one out: mapping `/dev/nvidia*` nodes is not sufficient (the container
also needs the toolkit-injected user-space driver), so a device mapping would *look* supported and
fail inconsistently — the worst outcome.

## Decision

1. **GPU passthrough is stored as an intent, keyed `(stack, service)`** — a `stack_gpu_mappings`
   row meaning "map every mappable host GPU into this service". No paths are stored; the row is
   host-neutral. It is edited in the same Settings section and replaced atomically by the same
   `stacks.setDevices` call as the literal mappings (one save, one audit entry).

2. **A deploy resolves the intent against a live host probe.** `HostGpuProbe` runs the backup
   helper image with the host's `/dev` and `/sys` bind-mounted read-only (`NetworkMode: none`, no
   device grants — the default device cgroup denies opening the nodes; the probe only lists and
   stats). It reports each render node's path, vendor, driver, PCI address and owning GID, cached
   for a few minutes. A probe failure is a deploy-log warning and an empty catalog — never a failed
   deploy, and never a blocker for stacks that use no GPU intent.

3. **Resolution maps render nodes and injects the group.** Every non-NVIDIA render node becomes a
   `devices:` entry (same generated-override mechanism as ADR-0030), and the union of the mapped
   nodes' GIDs becomes a `group_add:` list on the service — Compose appends `group_add`, so the
   repository's own entries survive. NVIDIA nodes are skipped with a deploy-log note naming the
   toolkit (`gpus:`/CDI) as the supported route — a later ADR when someone needs it.

4. **A host without a mappable GPU is a note, not a warning.** That is the feature working as
   designed — the same stack deploys everywhere and gets the GPU where one exists. Unknown service
   names keep ADR-0030's warning treatment.

5. **The plan stays runtime-neutral** (ADR-0010): `DeviceMappingPlan` gains GPU intents, the probed
   catalog, and per-service supplemental group ids — all concepts Kubernetes expresses natively
   (`volumeDevices`/CDI, `supplementalGroups`). Only `ComposeOverrideFile` knows about `devices:`
   and `group_add:` syntax.

## Consequences

- The Settings UI can offer "map host GPU(s)" per service plus a read-out of what the probe found
  ("renderD128 — intel, i915, 0000:00:02.0"), including the honest empty state on GPU-less hosts
  and on Docker Desktop.
- Because the intent is host-neutral, template-level sharing becomes possible later — the reason
  ADR-0030 rejected it (literal paths) does not apply to intents. Not built yet.
- Multi-GPU hosts map *all* mappable GPUs. Selecting a specific one (which would need the stable
  `/dev/dri/by-path/pci-…-render` alias to survive probe-order shuffles) is deferred until a real
  multi-GPU need appears; the literal-path editor covers it meanwhile.
- The probe's device list is only as fresh as its cache and container hot-plug does not exist in
  Docker's model anyway: a GPU that appears or vanishes takes effect on the next deploy.
- One more place runs the helper image; it inherits the backup feature's pull-on-first-use and
  operator-configurable image reference.
