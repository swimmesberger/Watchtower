# ADR-0032: NVIDIA GPUs resolve the same intent through the container toolkit

## Status

Accepted. Amends ADR-0031 decision 3, which skipped NVIDIA with a note.

## Context

ADR-0031 stores GPU passthrough as a per-service *intent* and resolves it against a live host probe.
It deliberately excluded NVIDIA, and the reason it gave is correct: mapping `/dev/nvidia*` is not
sufficient, because the container also needs the toolkit-injected user-space driver, so a device
mapping would look supported and fail inconsistently.

What that reasoning did not follow through on is that the *supported* NVIDIA route is itself an
intent. Compose asks for a GPU through a reservation:

```yaml
deploy:
  resources:
    reservations:
      devices:
        - driver: nvidia
          count: all
          capabilities: [gpu, video]
```

There are no paths in that, and no GIDs — nothing host-specific at all. It is a better fit for
ADR-0031's model than render nodes are, not a worse one. Excluding NVIDIA therefore left the one
GPU vendor most likely to be in a transcoding box unreachable by the feature built for transcoding
boxes.

Two further facts made the exclusion actively misleading rather than merely incomplete:

- **NVIDIA is usually invisible to the probe.** The probe walks `/sys/class/drm/renderD*`. NVIDIA
  only appears there when `nvidia-drm` is loaded, which is common on desktops and not guaranteed on
  the headless hosts that hold the cards. So the "needs the container toolkit" note fired on the
  machines that least needed it, and a genuine NVIDIA server got `No mappable host GPU was
  detected` — technically true, actively unhelpful, and with no hint that a working route exists.
- **The toolkit's presence is knowable.** It registers itself as a Docker runtime named `nvidia`,
  which `GET /info` reports. That matters because emitting a reservation on a host without the
  toolkit does not degrade — Compose fails the entire deploy with `could not select device driver
  "nvidia" with capabilities: [[gpu]]`.

## Decision

1. **The existing intent covers NVIDIA; no new row, no new control.** `stack_gpu_mappings` already
   means "give this service the host's GPUs". Resolution picks the mechanism per host: render nodes
   plus `group_add` where they exist (ADR-0031), a reservation where NVIDIA does. An operator moving
   a stack from an Intel host to an NVIDIA one changes nothing.

2. **NVIDIA is detected where it lives, not in the DRM listing.** The probe script additionally
   reports `/dev/nvidiactl`, which exists whenever the kernel driver is loaded. The DRM-based vendor
   check stays for the case where an NVIDIA card *does* expose a render node — that node is still
   never mapped by path.

3. **A reservation is only emitted when the daemon can honour it.** `HostGpuCatalog.NvidiaUsable`
   requires both the card and the `nvidia` runtime. A card without the toolkit reserves nothing and
   produces a note naming the toolkit — the safe direction, because the alternative is a failed
   deploy rather than a degraded one.

4. **`capabilities` includes `video`, not just `gpu`.** The toolkit only injects the encoder
   libraries when asked; with the common `[gpu]` or `compute,utility` spelling, CUDA works and NVENC
   fails to open the encoder. Video transcoding is the motivating workload, so the capability that
   makes it work is not optional here.

5. **The plan stays runtime-neutral** (ADR-0010). `ServiceDeviceMappings.NvidiaGpus` says the service
   wants the vendor's GPUs; only `ComposeOverrideFile` knows that Compose spells this as a device
   reservation. Kubernetes expresses the same thing as an `nvidia.com/gpu` resource request.

## Consequences

- A stack with a GPU intent now works on Intel, AMD and NVIDIA hosts without per-host editing, which
  is what ADR-0031 promised and could not deliver for a third of the fleet.
- The GPU-less note is now accurate: an NVIDIA host is no longer reported as having no GPU.
- One more reason to read `GET /info` on the probe path. It is cached with the rest of the catalog,
  and a failure to read it degrades to "no toolkit" rather than to an error.
- `count: all` maps every GPU, matching ADR-0031's choice for render nodes. Selecting one card is
  deferred for the same reason and would use `device_ids`.
- Compose honours `deploy.resources.reservations.devices` outside Swarm, unlike most of `deploy:`.
  That is genuinely surprising, so it is commented at the emitter rather than left to look like a
  mistake.
- Untested against real NVIDIA hardware in CI, like the rest of the GPU feature: the emitted block is
  the documented Compose form, and the capability list was verified against the toolkit's behaviour
  rather than assumed.
