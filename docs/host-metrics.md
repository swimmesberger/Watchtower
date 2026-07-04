# Host metrics

The Dashboard's host-health strip shows the host's CPU, memory, load average, and disk usage. This
reading is **opt-in**: it needs a read-only `/proc` mount and one env var. Without it, Watchtower still
reports everything else — per-container and per-stack CPU/memory, volumes, networks, and published-port
exposure all work with nothing but the Docker socket. Enable host metrics only if you want the host
totals on the Dashboard.

## Why it needs a mount

Watchtower runs inside a container, so by default it sees only its own cgroup — not the host. Host CPU,
memory, and load come from the kernel's `/proc` filesystem (`/proc/stat`, `/proc/meminfo`,
`/proc/loadavg`), which is the host's, not the container's, only when you mount it in. Container stats
need no such mount: they come from the Docker Engine API over the socket you already grant, so they are
never gated on this.

## Mounts

| Mount | Purpose | Notes |
| --- | --- | --- |
| `/proc:/host/proc:ro` | Host CPU, memory, load average | Read-only. Required for the CPU/RAM/Load cells. |
| `/:/host/rootfs:ro` | Full host disk usage | Read-only, **broad, and optional.** Without it, disk falls back to Docker's own `df`. |

The rootfs bind exposes the whole host filesystem read-only to the container. It is optional and
deliberately called out: skip it unless you want the true host disk figure rather than Docker's view.

## Environment variables

| Env | Example | Purpose |
| --- | --- | --- |
| `WATCHTOWER_HOST_PROC` | `/host/proc` | Points Watchtower at the mounted host `/proc`. Unset ⇒ host CPU/RAM/Load unavailable. |
| `WATCHTOWER_HOST_ROOTFS` | `/host/rootfs` | Optional. Points at the mounted host root for disk via `DriveInfo`. Unset ⇒ disk falls back to Docker's `df`. |

These are **plain** environment variables (like `WATCHTOWER_DOCKER_CONFIG`), not `WATCHTOWER__*`
options — they name the mount path, and their absence is what switches host metrics off. The value must
match the container-side path of the mount (`/host/proc` in the examples below).

## Compose example

Add the `/proc` mount and `WATCHTOWER_HOST_PROC` to the Watchtower service. The rootfs mount and
`WATCHTOWER_HOST_ROOTFS` are commented out — uncomment both together only if you want full host disk:

```yaml
services:
  watchtower:
    image: ghcr.io/swimmesberger/watchtower:latest
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - /proc:/host/proc:ro
      # optional, for full host disk usage instead of Docker's df:
      # - /:/host/rootfs:ro
    environment:
      WATCHTOWER_HOST_PROC: /host/proc
      # WATCHTOWER_HOST_ROOTFS: /host/rootfs
```

## Security

Both mounts are `:ro` — Watchtower reads them and cannot write. Weigh them against what's already
granted: the **Docker socket is the far larger grant** (it is effectively root on the host — it can
create privileged containers), and you have already mounted it. Against that, a read-only view of the
host's `/proc` is minor. The rootfs bind is broader — it exposes the whole filesystem read-only — which
is why it's optional and off by default; leave it out and disk simply falls back to Docker's `df`.

- `/proc:/host/proc:ro` — read the host's live CPU/memory/load counters. Does not expose file contents
  outside `/proc`; cannot modify anything.
- `/:/host/rootfs:ro` — read the host's disk usage (and, being the whole root, its file contents).
  Read-only; still, only mount it if the true host disk figure is worth it.

## Verification

After `docker compose up -d`, open the Dashboard: the host-health strip should populate within **~10s**
(the sampler ticks about every 10 seconds). If the CPU/RAM/Load cells instead show *"Host metrics
unavailable"*, the `/proc` mount or `WATCHTOWER_HOST_PROC` is missing — recheck both. Container and
per-stack metrics populate regardless, so an empty host strip alongside working container rows points
squarely at the mount.

## Fallback behavior

Everything except the three host-total cells works with no mount at all:

- **Without `WATCHTOWER_HOST_PROC`** — `HostMetrics.available` is `false` with reason
  `host-proc-not-mounted`; the strip shows an "enable host metrics" banner linking here. Per-container
  and per-stack CPU/memory, volumes, networks, and the port-exposure map are unaffected.
- **Without `WATCHTOWER_HOST_ROOTFS`** — disk is reported from Docker's `system df` instead of the host
  root; the Dashboard labels this source `docker-df` (Docker's view of disk, not the full host).

So enabling host metrics is a pure add-on: it lights up the three host-total cells and the true disk
figure, and changes nothing else.
