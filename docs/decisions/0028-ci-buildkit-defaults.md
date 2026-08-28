# ADR-0028: CI runners carry the host's BuildKit knowledge — a generated default buildkitd config, and a reusable docker-driver workflow

- Status: Accepted (implemented; amended 2026-08-28 — the snapshotter auto-detection this
  originally shipped was reverted to an explicit-only knob after its premise was corrected,
  see decision 1 and the consequences)
- Date: 2026-08-28
- Related: [docs/ci-runners/design.md](../ci-runners/design.md) §"Container image builds",
  [issue #65](https://github.com/swimmesberger/Watchtower/issues/65) (the evidence and the
  analysis this decision rests on).

## Context

A repository on a Watchtower-provisioned runner that builds an image the documented way —
`docker/setup-buildx-action`, i.e. the `docker-container` driver — gets a BuildKit daemon
in its own container, which reads none of the host daemon's configuration. On the NAS that
produced issue #65 this had two consequences:

1. **Silent ~10× slower builds.** BuildKit's OCI worker resolves its snapshotter with
   `auto`: overlayfs, then fuse-overlayfs, then `native`. DSM's 4.4 kernel fails both
   probes, and `native` has no copy-on-write — every layer materialisation is a full
   recursive copy of the accumulated stage. A fully-cached MuxBox publish job spent 13m20s
   with nothing to upload; 26 layer extractions totalled 798s, a 93-byte layer costing a
   minute on top of the .NET SDK. Nothing in the job log names the cause. The host offers
   no fix: kernel, storage driver (btrfs — a containerd snapshotter, not an OCI-worker
   one) and engine version are all pinned by DSM.
2. **Per-repo host knowledge.** The plain-HTTP registry needs a `buildkitd-config-inline`
   stanza in every consuming workflow, because the daemon's `insecure-registries` setting
   does not reach an out-of-daemon BuildKit.

Both violate what the CI-runners design promised: zero-ceremony enablement, workflow YAML
that stays fully standard, and — per the standing rule that configuration lives in
Watchtower and is delivered at the point of use — no host facts hand-copied into repos.

## Decision

**1. Watchtower generates a default buildkitd config and ships it into every runner.**
buildx reads `$BUILDX_CONFIG/buildkitd.default.toml` whenever the workflow passes no
config of its own, so every `docker/setup-buildx-action` inherits it with no workflow
change (a workflow's own `buildkitd-config(-inline)` still wins outright). The file
(`CiBuildkitConfig`) carries:

- `[registry."…"] http = true / insecure = true` for exactly the registries the host
  daemon treats as insecure, read from `GET /info` — the daemon is the authority on which
  registries this box reaches without TLS, so this needs no new Watchtower state and
  tracks the host automatically.
- `[worker.oci] snapshotter = …`, **only when the new instance-wide
  `Ci:BuildkitSnapshotter` option explicitly names one** (`none`/`auto`/unset all mean
  "emit nothing"). *Amended:* the first version of this ADR shipped `/proc/filesystems`-
  based auto-detection here, built on the belief that BuildKit's `auto` chain never tries
  fuse-overlayfs — buildkitd starting cleanly with a forced fuse-overlayfs worker on the
  NAS seemed to prove a viable snapshotter the probe had skipped. Both halves of that were
  wrong: `auto` does call `fuseoverlayfs.Supported()` before falling back to `native`
  (moby/buildkit `cmd/buildkitd/main_oci_worker.go`), `Supported()` performs a *real*
  fuse-overlayfs test mount (containerd/fuse-overlayfs-snapshotter `check.go`), and an
  explicitly named snapshotter *skips* that check — so the clean start proved nothing,
  while the `native` outcome proved a real mount had failed on that box. A detection
  heuristic weaker than BuildKit's own mount test can only agree with it or wrongly
  override it, and a wrong override turns quietly-slow builds into builds that fail at
  the first layer mount. So the default emits nothing, and the knob is expert-only: for
  an operator who has verified a snapshotter with a real mount (procedure in the design
  doc) on a host whose probe is demonstrably wrong, or who needs one the probe never
  tries (e.g. `stargz`). Instance-wide because a working snapshotter is a property of
  the host kernel, not of any repo.

Delivery is a third per-repo volume (`watchtower-ci-buildx-{repo}`) mounted at
`/home/runner/_buildx` and exported as `BUILDX_CONFIG`. A volume, not a file bind, because
dockerd creates missing bind parents root-owned — a mount under `~/.docker` would break
the next `docker login` in a job (the `_work` trap again). The existing volume-init
container writes the file and chowns the volume roots, re-running whenever the generated
content changes, so a registry added at runtime reaches jobs within one reconcile pass.
The runner spec-hash material gained a version prefix so pre-existing idle runners are
recycled once and pick the mount up.

**2. The fast path on hosts without a working OCI snapshotter is the docker driver, and
it is encoded once, in a reusable workflow.** Where no OCI snapshotter works, the config
file cannot fix the `docker-container` driver, and the right answer is not to use it: the
daemon's own builder uses the host storage driver (real CoW even on btrfs), keeps its
cache on the host between ephemeral runners, and inherits `insecure-registries` natively.
[`build-push-image.yml`](../../.github/workflows/build-push-image.yml) encodes that
driver choice with its consequences (`provenance: false`; no registry cache
import/export), reading the `REGISTRY` variable and credentials Watchtower already syncs.
Consuming repos carry one `uses:` line and `secrets: inherit`.

## Consequences

- MuxBox-class workflows drop `buildkitd-config-inline`, and either switch to the
  reusable workflow or simply drop `setup-buildx-action`; the ~10× extraction penalty
  disappears with the `native` snapshotter.
- The open question from issue #65 — whether fuse-overlayfs can be made to work on DSM —
  leans **no** on the evidence: BuildKit's probe already attempted a real fuse-overlayfs
  mount in the builder container there and failed (that is what the `native` fallback
  means), and the startup-only probe that briefly seemed to say otherwise proved nothing
  (see the amendment in decision 1). The definitive hand-check — an actual mount, whose
  procedure is in the design doc — remains to be run; unless it passes, the reusable
  docker-driver workflow is the fast path on that host, exactly as the issue's
  Proposal B anticipated.
- With the docker driver, build cache accumulates in the daemon instead of dying with the
  builder container. `docker builder prune` is the manual relief valve; wiring it into
  Watchtower's maintenance story is recorded as future work in the design doc.
- Runners on small hosts still will not reach GitHub-hosted speeds (`exporting layers` is
  gzip-bound); the design doc says so, so nobody hunts for a second bug.
