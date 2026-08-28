# ADR-0029: Zero-downtime deploys — routed services warm up in a new generation, then traffic swaps

## Status

Proposed (2026-08-28).

## Context

Every stack deploy has a built-in downtime window. `docker compose up -d` recreates a changed
service stop-first — the old container is stopped and removed before the new one starts — and the
recreated containers only rejoin the stack's ingress network (`watchtower-ingress-{stackId}`) after
`up` returns, when `DeployQueueService` runs its post-deploy `ConnectStackAsync`. For the whole
recreate-plus-reconnect window the proxy's upstream alias either does not resolve or resolves to a
dead container, and visitors get a bare 502: the YARP provider (ADR-0022) forwards to a single
destination with no retry, no failover and no health awareness.

For a single hobby stack that blip is tolerable. For the products scenario (ADR-0026) it is not: a
release rollout fans the same blip out across every tenant of a product, and "we deployed" becomes
"every customer saw an error page at the same minute". The obvious shape of a fix is equally well
known — start the new containers next to the old ones, wait until they are demonstrably ready, swap
traffic atomically, then remove the old ones — and the proxy plane is already built for the swap:
the route table is an immutable snapshot replaced through a volatile reference (requests never see a
half-applied change), and a bump of the ADR-0024 change signal converges every instance.

What is missing is everything around the swap. Nothing in Watchtower reads container health —
`DockerEngineClient` does not even parse `State.Health` from the inspect response. Compose project
names, container names, network aliases, metrics series, backup plans, drift detection and the App
API all assume one container set per stack. And a naive "second copy of the stack" founders on state:
compose scopes named volumes to the project, so a duplicated database starts empty — and sharing the
volume instead means two engines writing one data directory. Any zero-downtime design has to say
which containers are duplicated, what readiness means, how traffic moves, and what the application
must promise in return.

## Decision

### 1. Zero-downtime deploys are an opt-in `DeploymentStrategy`, inherited product → template → stack

A new `DeploymentStrategy` (`recreate` | `blue-green`) lives on the product; templates and stacks
hold a nullable override (null = inherit), resolved stack ?? template ?? product like the existing
product-source resolution. The default is `recreate`: an existing stack's deploys stay byte-for-byte
what they are today. Putting the primary knob on the product matches ADR-0026 — the product is the
deployable unit, and "this product deploys without downtime" is a property of the product that every
tenant stack should inherit rather than re-declare.

The volume-recreate deploy flow always takes the `recreate` path regardless of strategy: it exists
to wipe and rebuild data volumes, so there is no continuity to preserve — and it additionally tears
down any live generation (§4) and resets the active slot.

### 2. Only routed services are duplicated; stateful services are shared, not swapped

The parallel "green" copy contains exactly the services a `Route` row (`Target = Service`,
ADR-0023) points at. Everything else — databases, queues, workers — stays in the base compose
project and keeps today's in-place recreate, which compose already skips for services whose
configuration did not change.

This is the industry-standard shape for a reason. Duplicating a database per deploy is somewhere
between wrong and catastrophic: a copied volume misses every write made after the copy, a shared
volume means two engines on one data directory, and a "read-only mode" cutover turns zero-downtime
into read-only downtime. Mature platforms warm-swap the application layer and share the data layer,
and push the remaining burden into a migration discipline (§6). A stack with no service routes has
nothing to swap traffic for and falls through to `recreate`.

The green services still need the shared world: the generated compose override (the ADR-0012
mechanism, extended) redeclares the base project's default network and every named volume as
`external`, pointing at the names compose already resolved under the base project
(`{base}_default`, `{base}_{volume}`). Volumes therefore keep their base-project labels, which is
what keeps backup volume discovery (ADR-0016/0017) correct without change. In the degenerate case
where every service is routed there is no base world to share, and each generation owns its own
network — correct by construction, since there is nothing inside the stack left to reach.

### 3. A deploy creates a new generation project, `{base}--g{n}`, and readiness is a declared healthcheck

Each blue-green deploy runs the routed services under a fresh compose project named
`{base}--g{n}`, where `n` is a monotonically increasing generation (`(active ?? 0) + 1`). Project
names matching `--g\d+$` become reserved — refused by stack project-name validation — so one
stack's generation can never collide with another stack's base name.

Generations, not alternating blue/green names, because cleanup then needs no bookkeeping: whatever
exists under the `{base}--g` prefix and is not the recorded active slot is garbage, deterministically
(§4). An alternating scheme leaves a crashed deploy's leftovers squatting on the *next* target name,
where the next deploy would adopt half-finished containers instead of creating a clean set. The
suffix also makes `docker ps` self-documenting: `myapp--g7-web-1` says which generation serves.

The pipeline, replacing the single `up` of a recreate deploy:

1. **Contract gate** (§6), on the already-produced `compose config --format json` — all violations
   reported at once, before anything is pulled or created; the running stack is untouched.
2. **Base converge**: `pull` + `up -d --no-deps` for the non-routed services on the base project.
   This is also what guarantees the shared network and volumes exist for the green `external`
   references.
3. **Green up**: `pull` + `up -d --no-deps <routed services>` on `{base}--g{n}`. `--no-deps` is
   essential — without it compose would start a second database inside the green project.
4. **Health wait**: poll the green containers' `State.Health` until every one reports `healthy`,
   bounded by a configurable timeout (§7). `unhealthy` and `exited` fail fast — Docker only reports
   `unhealthy` after the configured retries past `start_period`, so it is a verdict, not a blip.
   On any failure the green project is torn down, the deploy fails, and the old generation never
   stopped serving: a failed deploy is invisible to visitors.

Readiness is the service's **declared compose healthcheck, and declaring one is mandatory** for
routed services under `blue-green` (§6). A `HEALTHCHECK` baked into the image does not count: it is
invisible in `compose config` output, so the gate could not verify it without a daemon and an
already-pulled image — and the whole point of the gate is to be reviewable, version-controlled and
checkable before anything runs.

### 4. The active generation is a column on `stacks`; everything else is garbage a sweep collects

`Stack.ActiveDeploymentSlot` (`int?`, null = the base project serves, as before any blue-green
deploy) records which generation traffic points at — following ADR-0024, in the database, so any
instance can project the route table and a restart changes nothing. Nothing is persisted *before*
the swap: a deploy that dies after the green `up` leaves only unreferenced containers.

Cleanup is one rule applied in two places: **down every `{base}--g*` project whose generation is
not the active slot.** It runs inline at the start of every blue-green deploy (so a retry never
waits on a janitor) and periodically in a `DeploymentSlotReaper` background service (so a crashed
deploy's leftovers — possibly with `restart: always` — are bounded in lifetime). The reaper skips
any stack with a running or queued deploy event (the same predicate tenant teardown uses), which is
what keeps it safe when several Watchtower instances share a daemon, and it leaves slot projects
with no matching stack row alone — stack deletion leaves base containers behind today, and a reaper
should not invent a new kind of deletion.

While a slot is live, `ComposeProjectName` is immutable — a rename would orphan the running
generation beyond the sweep's prefix match.

### 5. The swap is the existing atomic route-table replace, and the alias must carry the generation

The proxy upstream alias becomes generation-scoped: `{base}--g{n}-{service}` on the same
per-stack ingress network, produced by the same `EdgeAlias` formula over the *serving* project name.
The swap sequence is: join the healthy green containers to the ingress network under their slot
alias, persist `ActiveDeploymentSlot = n`, then re-project — the `FrozenDictionary` snapshot
replace is atomic for in-flight requests, and the ADR-0024 change signal converges other instances
within its debounce. Because the slot is read by the provider-independent `ProxySiteProjection`,
all three providers (YARP, Caddy, Cloudflare — ADR-0015) follow from the one projection change.

The generation-scoped alias is load-bearing, not cosmetic. The YARP forwarder sends every upstream
request through one process-wide `SocketsHttpHandler` whose connection pool is keyed on
scheme+host+port, with an infinite pooled-connection lifetime. Re-pointing a *stable* alias at the
new containers would not move traffic: warm pooled sockets to the old container's IP keep being
reused indefinitely under load. A new hostname is a new pool key — fresh DNS resolution, fresh
connections to green — while blue's existing sockets stay valid on blue for exactly the drain
period. The alternative (flush or shorten the pool) would tax every stack's steady-state latency to
solve a per-deploy problem.

### 6. The deployment contract: what an application must promise to be deployed without downtime

Opting into `blue-green` binds the stack's compose file and application to four rules. The first
three are validated by the contract gate and refuse the deploy with an actionable message; the
fourth is documentation — it cannot be checked from the outside.

1. **Every routed service declares a healthcheck in the compose file** (not only in the image —
   §3). Without one there is no readiness signal and the swap would be a guess.
2. **Routed services publish no host ports and set no `container_name`.** Two copies cannot bind
   one port or hold one name. Violations are *refused, not stripped*: a compose override can only
   append to a list (removal needs the `!reset` tag and compose ≥ 2.24, noted as a future escape
   hatch), and silently deleting an operator's `ports:` under one strategy but not the other would
   be invisible behavior divergence. The proxy path never needed host ports (ADR-0022).
3. **Aliases must fit a DNS label**: `{base}--g{n}-{service}` ≤ 63 characters, checked with the
   real service names in hand.
4. **Version N and N−1 run concurrently against the shared data layer** for the health-wait plus
   drain window. That means expand-contract migrations only: a release may add columns and start
   writing them, but the release that removes or repurposes what the old code reads must come after
   the old code is gone. This also covers a subtler overlap: both generations sit on the base
   network and answer the same plain compose alias `<service>`, so intra-stack calls from a worker
   to `http://web` round-robin across versions for the length of the drain.

A pre-swap "quiesce" lifecycle hook — the application is told to enter read-only mode, finish
in-flight work, and release the old generation deliberately — was considered and deferred (see
Rejected alternatives); the contract is written so such a hook slots in later as a tightening,
not a redesign.

### 7. The drain is a bounded wait, and it does not serialize a rollout

After the swap, the deploy waits a configurable drain period before removing the previous serving
location — the previous generation's project, or on the first blue-green deploy the routed services
of the base project (`rm --stop --force`, volumes untouched). In-flight requests hold their
already-established connections to the old containers; the drain gives them time to complete.

The cross-stack deploy gate (`MaxConcurrentDeploys`) is released *before* the drain wait: the drain
does no Docker, registry or network work, and holding a permit through it would turn a 500-tenant
rollout with a 30-second drain into an hour of scheduled sleeping. The per-stack slot is held
through cleanup — a second deploy of the same stack must not start while two generations are up —
and the deploy event stays `running` until cleanup finishes, which keeps "is a deploy active"
guards (tenant teardown, the reaper) truthful. The UI's "already serving" signal comes from a new
`DeployEvent.Phase` (`pulling` → `converging` → `warming` → `swapping` → `draining`), not from the
event status.

Three knobs, following the existing options-clamp pattern: `DeployHealthTimeoutSeconds`
(default 300), `DeployDrainSeconds` (default 30, 0 legal), `DeploySlotReaperIntervalMinutes`
(default 30).

### 8. Strategy switches converge without ceremony

`recreate` → `blue-green` needs no migration step: the first blue-green deploy finds
`ActiveDeploymentSlot` null and removes the base project's routed containers after the swap and
drain, exactly as it would remove a previous generation. `blue-green` → `recreate` converges the
other way with near-zero downtime as a courtesy: the recreate deploy brings the routed services up
in the base project while the slot still serves, nulls the slot, re-projects, drains, and downs the
generation projects. There is deliberately no health gate on that path — `recreate` swaps as soon
as `up` returns, which is precisely recreate's existing contract.

## Consequences

- Every consumer of `ComposeProjectName` must learn that a stack can serve from a slot project:
  stop/start and the desired-state reconciler (ADR-0025) must stop base *and* generation projects
  and start only the active one; tenant teardown downs generations before the base; the App API
  (ADR-0008) must recognize a caller whose project label carries the suffix; drift detection
  inspects the active slot's containers; metrics must normalize the project name or every deploy
  restarts a stack's series; backup plans include the active slot's containers while volume
  discovery stays base-scoped. This enumeration is the bulk of the implementation surface.
- Routed services run twice for health-wait plus drain — on a dense tenant host with the default
  timeout that is up to ~5½ minutes of doubled memory per deploying stack.
- Container names carry the generation suffix in `docker ps` and any raw-Docker tooling; the UI
  normalizes for display and shows a generation badge instead.
- `DockerEngineClient` grows `State.Health` parsing and a health-wait helper — capability that
  backup quiesce or the UI can later reuse.
- Operators whose healthcheck lives only in the image must re-declare it in the compose file; the
  gate's error message shows exactly what to add. Expect this to be the most common first-deploy
  failure.
- A deploy event now stays `running` through the drain — a visible change for anyone scripting
  against event status.
- Rollback keeps its ADR-0026 shape — pin the previous release and redeploy — but under
  `blue-green` the rollback itself is also downtime-free.

## Rejected alternatives

- **Duplicating the whole stack, databases included.** Fresh project-scoped volumes start empty; a
  shared volume means two engines writing one data directory; a copied volume misses writes made
  after the copy. No variant is safe as a default (§2).
- **A quiesce/read-only cutover as the v1 foundation** — tell the app (and Postgres) to stop
  accepting writes, copy state, swap, resume. It trades a 502 window for a read-only window, must be
  implemented per application, and still loses the writes-in-flight race unless the app cooperates
  perfectly. Kept as future work: an optional pre-swap lifecycle hook layered on this design.
- **Alternating blue/green names.** Crash recovery stops being answerable from state alone, and a
  crashed deploy's leftovers occupy the next deploy's target name (§3).
- **Keeping the alias stable and moving it between containers.** Defeated by the forwarder's
  connection pool: the pool key never changes, so warm sockets keep serving the old containers
  (§5). Docker DNS with two containers under one alias also round-robins rather than swaps.
- **`compose up --wait` as the health gate.** Native, but a bare exit code with no per-service
  diagnosis, version-gated (`--wait-timeout` needs compose ≥ 2.17), and untestable through the
  existing compose seam on machines without the plugin. Reading `State.Health` directly yields
  "service `web` went unhealthy after 3 consecutive failures" instead of "exit 1".
- **Stripping published ports with `!reset` instead of refusing them.** Silently changes what the
  operator's file says, under one strategy only, and pins the feature to compose ≥ 2.24. Refusal is
  symmetric with the healthcheck rule; `!reset` remains a documented escape hatch for later.

## Future work

- The optional pre-swap quiesce/read-only lifecycle hook (§6).
- Per-stack health-timeout overrides.
- Injecting `WATCHTOWER_DEPLOYMENT_SLOT` into green containers so applications can log their
  generation.

## References

- ADR-0012 — the generated compose override the green project extends.
- ADR-0015 / ADR-0022 / ADR-0023 — the proxy provider seam, the in-process YARP proxy, and routes
  as the unit of "this domain fronts that service".
- ADR-0024 — state in the database; the change signal that converges the swap across instances.
- ADR-0025 — desired state and the reconciler that must become slot-aware.
- ADR-0026 — products, releases and the rollout fan-out this exists to make bearable.
