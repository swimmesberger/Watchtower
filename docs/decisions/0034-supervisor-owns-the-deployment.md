# ADR-0034: A supervisor owns Watchtower's deployment — Watchtower declares, the supervisor reconciles

- Status: Proposed
- Date: 2026-09-02
- Related: [ADR-0005](0005-aspire-dev-orchestration.md) (the dev-time analogue of exactly this pattern),
  [ADR-0014](0014-env-wins-runtime-settings.md) (what "the operator pinned it" means, and why
  `config.yaml` is an env layer rather than a new one),
  [ADR-0022](0022-in-process-yarp-proxy.md) / [ADR-0033](0033-port-routes-and-internal-ca.md) and
  PRs #77 / #78 (the self-manipulation this retires, and the "compose drift" consequences it answers),
  [ADR-0024](0024-postgresql-only-and-state-in-the-database.md) (why the data volume is empty today,
  and therefore free to carry a contract),
  [ADR-0027](0027-full-instance-backup-and-restore.md) (the restore coordinator, which moves),
  [ADR-0010](0010-target-kubesolo-runtime.md) (the Kubernetes controller this is a rehearsal for),
  [docs/multi-node-readiness.md](../multi-node-readiness.md) (§4's role split, which this does not
  touch).

## Context

Compose reconciles a static definition. Watchtower's own container definition is not static: it is
derived from Watchtower's state, and three parts of it move at runtime.

The **image** moves on a self-update. The **published host ports** move when an operator adds or removes
a port route (ADR-0033 decision 7). The **ingress-network membership** moves on every deploy and every
route change, because Watchtower *is* the proxy under the `yarp` provider and has to sit on each stack's
`watchtower-ingress-{stackId}` network to reach its upstreams. `YarpProxyProvider.cs:183-193` hands its
own `HOSTNAME` to `ProxyIngressNetworks.ConnectAllRoutedContainersAsync`
(`ProxyIngressNetworks.cs:94-128`), the sweep over every routed stack, which joins them one at a time
through `EnsureStackNetworkAsync` (`:39-44`).

A `docker compose up -d` after any edit to the compose file rebuilds the container from the file and
drops all three. It is worth being precise about which of them actually hurts, because two of them heal
themselves:

- **Network joins heal.** `YarpProxyProvider` is an `IHostedService` whose `StartAsync`
  (`YarpProxyProvider.cs:93`) queues a full reconcile (`:108`), and the reconcile's first act is to
  re-join every ingress network. A recreate costs a few seconds of 502s on routed hosts and nothing else.
- **The image tag heals, mostly.** The compose file names a tag, the self-update pulled that tag, so the
  recreated container runs the same image the coordinator would have given it. ADR-0033 already recorded
  the exception in the other direction: a ports-only recreate carries the container's *configured* image
  reference, so a tag that moved locally brings the newer image along.
- **The ports are the real loss.** Nothing re-derives them. `SelfPortPublishService.StartAsync`
  (`:145`) prunes the managed-port claim down to what the container actually publishes, so after a
  compose recreate the Routes page correctly says "host port 9001 is not published" and offers the
  button again. Honest, and a restart of the management plane the operator did not ask for.

That is the visible half. The structural half is worse, and everything awkward about the current design
follows from one fact: **nothing outlives Watchtower's process.**

A recreate cannot be performed by the process being recreated, so Watchtower spawns a throwaway sibling
container from its own image, running the same binary under `--self-update`
(`CoordinatorMode.cs:28`, dispatched at `Program.cs:25-26`). That coordinator gets the Docker socket,
`NetworkMode = "none"` and nothing else (`CoordinatorContainers.cs:113-132`). It then sleeps three
seconds so the HTTP response that triggered it can flush (`CoordinatorMode.cs:56-58` — "Allow the
triggering container to finish returning its response before it is stopped"), stops the container,
renames it aside as a rollback target, creates the replacement from a cloned config
(`ContainerCloneSpec.FromInspect`, `ContainerCloneSpec.cs:62`), reconnects the networks the create body
could not carry, starts it, and rolls back if the start throws (`CoordinatorMode.cs:64-91`). The try
block opens at `:72`, so the stop (`:64`) and the rename-aside (`:69`) are both outside it: a failure in
either leaves a stopped container under a name nothing will look for, with no rollback path.

Every peculiarity in that flow is the missing "after" showing through:

- **Two paths spawn the same shape of coordinator over the same container id**, self-update and port
  publish, so they need a cross-guard that neither of them owns. `CoordinatorContainers.OtherRecreateInFlightAsync`
  (`:51`) makes each read the *other's* settings record — `self.runtime` (`SelfUpdateService.cs:35`) and
  `proxy.ports.runtime` (`SelfPortPublishService.cs:47`). Reading it once is a time-of-check race, so
  each path reads it, claims its own stage, and reads again (`SelfUpdateService.cs:320` and `:352`);
  both standing down in a true tie is the documented correct outcome.
- **The managed-port claim is written before the spawn**, not after
  (`SelfPortPublishService.cs:384-390`), with a comment that says why in one line: "the coordinator ends
  this process and there is no 'after'". The startup prune exists to repair the resulting lie.
- **A wedged coordinator blocks both paths indefinitely, deliberately** (`CoordinatorContainers.cs:74-92`),
  because clearing the stage on a timeout would let a second coordinator start while the first may still
  be mid-recreate. The best available fix was a better error message.
- **The success of an apply cannot be recorded by the process that started it.** Both services record the
  *start* as the audit event and leave the outcome to the next process instance's startup reconcile.

None of this is bad code. It is what the constraint permits. The constraint is that the only thing on the
host with authority over Watchtower's container is Watchtower.

**Development already solved this.** ADR-0005's Aspire AppHost provisions PostgreSQL with a data volume,
publishes the connection string as `ConnectionStrings:watchtower` (the fallback key
`WatchtowerConnectionString` reads, so the API needs no Aspire-specific configuration), and starts the
API behind `WaitFor(database)` (`src/Watchtower.AppHost/AppHost.cs`). A long-lived process outside the
app owns the app's deployment. In production there is no such process.

**Kubernetes is the same pattern at the other end.** ADR-0010's target is an API where an operator writes
values into a ConfigMap and a controller reconciles the resources it owns, patching its own Deployment
when its desired state changes. Compose sits in between: it has the declarative file and none of the
controller. That is the gap this ADR fills, in the shape Kubernetes will later fill it.

## Decision

### 1. One image, one new long-lived mode

`swimmes/watchtower` gains `supervise` alongside the modes it already dispatches in `Program.cs`:
`--self-update` (`:25`), `--restore-self` (`:32`) and `--export-schema` (`:39`). Unlike all three, it
does not exit.

The operator runs it once, by hand:

```bash
docker run -d --name watchtower-supervisor --restart always \
  --group-add "$(stat -c %g /var/run/docker.sock)" \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v /srv/watchtower/data:/data \
  -v /srv/watchtower/watchtower.yaml:/config.yaml:ro \
  swimmes/watchtower supervise
```

That command line is the only static artifact in the deployment. Everything else is either the
operator's `config.yaml` or Watchtower's own declaration.

**The `--group-add` is not optional and replaces compose's `group_add: ["999"]`.** The image runs as an
unprivileged user (`USER watchtower`, `Dockerfile:81`), so a mounted socket it has no group for is a
permission error on the first daemon call. `stat -c %g` is the Linux answer, and is the number the
shipped compose file asked operators to work out by hand. Elsewhere the socket is not at that path or
not group-owned: read the gid off whatever `DOCKER_HOST` resolves to (`stat -f %g` under macOS' own
`stat`), and a rootless podman socket owned by the invoking user needs no supplementary group at all.

The supervisor derives the gid for the containers *it* creates exactly as Watchtower does today, from
its own `/proc/self/status` `Groups:` line (`HostSupplementaryGroups.cs:20-30`, handed to every spawned
coordinator at `CoordinatorContainers.cs:127`). Supplied once on the one-liner, it propagates.

**Upgrading the supervisor is manual**, and that is a decision rather than an omission. The supervisor
recreates Watchtower; nothing recreates the supervisor, so a self-recreating supervisor would need the
whole throwaway-sibling dance this ADR exists to delete, in the one component that must never be
mid-recreate when something goes wrong. Re-running the one-liner with a new tag is the upgrade. The
mode is versioned with the image, so a supervisor older than the Watchtower it supervises is the normal
state between upgrades, and the file contract in decision 3 carries a version for it.

### 2. The supervisor owns every dynamic self-manipulation

Four things move out of Watchtower:

1. **Create, recreate and rollback of Watchtower's container** — image, host ports, environment.
   `ContainerCloneSpec`'s transformation moves with it, and moves cleanly because it is already pure
   JSON with no daemon in it.
2. **Ingress-network membership of Watchtower's own container.** This one needs no recreate at all:
   `network connect` and `network disconnect` work on a running container, and Watchtower has been doing
   exactly that to *itself* at every reconcile. The supervisor does it instead, from the networks
   Watchtower declares. It also creates and holds one network of its own, `watchtower-supervisor`, for
   the reason decision 6 gives.
3. **Postgres provisioning and the health wait** — compose's `depends_on: service_healthy`, performed by
   a process rather than a file.
4. **Secret generation.** `POSTGRES_PASSWORD` and `WATCHTOWER__AUTH__KEYPROTECTIONSECRET` are generated
   on first run if absent, persisted under `/data/self/` and injected as environment variables.
   ADR-0024 made the key-protection secret "[o]ptional rather than required so an upgrade stays one
   command"; a supervisor generates it as part of the one command, which turns encryption at rest from
   an opt-in into the default without adding a step.

After this, **Watchtower never touches its own container again.** `SelfUpdateService`'s coordinator
spawn, `SelfPortPublishService`'s recreate, the self network joins in `YarpProxyProvider.ReconcileAsync`
and ADR-0027's restore coordinator all leave. Watchtower keeps the Docker socket — it manages stacks,
and that is unchanged. This takes away one privilege it exercises against itself, not the socket.

### 3. The contract is two files on the data volume, and there is no RPC

Watchtower writes `/data/self/desired.json`: the amendments it declares to its own container.

```
{ "contractVersion": 1,
  "generation": 7,
  "image": "swimmes/watchtower:1.9.0",
  "listen": { "management": 8080, "http": 8081, "https": 8443 },
  "publishPorts": [9001, 9002],
  "networks": ["watchtower-ingress-3", "watchtower-ingress-8"] }
```

`listen` is the container side of the three fixed ports, and it is here rather than in `config.yaml`
because Watchtower owns those numbers: the management port is `Kestrel__Endpoints__Http__Url`
(`Dockerfile:77`) and the ingress pair are the runtime settings `WATCHTOWER__PROXY__YARP__HTTPPORT` /
`__HTTPSPORT` (ADR-0022's addendum). Decision 4 explains what the split buys.

The supervisor writes `/data/self/status.json`: what it has done about that.

```
{ "contractVersion": 1,
  "appliedGeneration": 6,
  "phase": "reconciling",
  "lastError": "…the daemon's own words…",
  "rollbackAvailable": true,
  "supervisorVersion": "1.9.0",
  "heartbeatAt": "2026-09-02T10:14:03Z" }
```

The supervisor composes the container spec from three inputs: the operator's `config.yaml` (decision 4),
the secrets it generated, and Watchtower's amendments. It reconciles the running container to that spec
and reports back.

Both files are written temp-plus-rename, so a reader never sees half a document and a crash mid-write
loses the update rather than the file.

**Both sides poll, at one second; neither uses inotify.** Inotify is fine over a bind mount on a local
filesystem, which is why it looks like the obvious choice. Two cases decide against it: it does not fire
for changes made on network-backed storage (NFS, SMB, a NAS volume driver), and Docker Desktop's file
sharing does not propagate the events either. A supervisor built on it would work on the maintainer's
box and sit still on exactly the hardware ADR-0033 was written for.

Polling reads the file when mtime or size has moved **and compares the generation after reading, every
time**. Stat alone is not enough: `"generation": 7` becoming `"generation": 8` inside one coarse mtime
tick moves neither field. The generation comparison is the convergence test; the stat only saves reading
a small file once a second.

**Watchtower's UI reads `status.json`**, and that replaces both runtime records: `self.runtime`
(`SelfUpdateService.cs:35`) and `proxy.ports.runtime` (`SelfPortPublishService.cs:47`).
`proxy.getPortBindings` stops being a Docker inspect and becomes a read of the applied generation, which
also retires ADR-0033's "Watchtower cannot see its own container" state — the supervisor can always see
it, and if the supervisor is gone the heartbeat says so instead.

Why files and not an API between the two processes: there is no token to mint, rotate or leak, no port
to bind, no network to put them both on, and no second wire format to version. It works while Postgres is
down, which matters because "Postgres is down" is a state the supervisor may be the one fixing. And it is
`kubectl apply` in miniature — a declared spec, an applied generation, an observed status — which is what
makes decision 8 a translation rather than a rewrite.

### 4. `config.yaml` is the operator's pinned surface, in ADR-0014's exact sense

ADR-0014 settled the precedence question: `appsettings < boot snapshot < live settings store < env vars
< command line`, and a value supplied through the environment is shown in the UI as pinned, disabled,
and named. `config.yaml` is not a fourth layer. Its `env` block becomes environment variables on
Watchtower's container, so `EnvironmentSettingPins` renders them exactly as it renders a compose file's
today. The UI says "pinned in config.yaml" instead of naming a variable, and that is the whole change.

The schema is deliberately small, and versioned so the supervisor can refuse a file it does not
understand rather than guess:

```yaml
version: 1
dataDir: /srv/watchtower/data     # or: volume: watchtower-data
ports:                            # HOST side only
  management: 127.0.0.1:8080
  http: 80
  https: 443
postgres: bundled                 # or a connection string
env:
  WATCHTOWER__PROXY__ENABLED: "true"
  WATCHTOWER__PROXY__ADMINEMAIL: you@example.com
```

**`ports:` is the host side of the mapping and nothing else.** The container side comes from
`desired.json`'s `listen` block, and the supervisor composes each `host:container` pair from the two.
That fixes a documented trap: the container-side ports are runtime settings, so an operator who moves an
ingress port under Settings → Reverse proxy has to remember to edit the right-hand side of a compose
mapping to match. The shipped compose file warns about it twice (`docker-compose.yml:27-30` and
`:64-68`, "Keep the right-hand side of the mappings above in step with them"), which is how you can tell
it was not reliably remembered. Split this way, neither side can be edited into disagreement with the
other.

**Port-route ports are in neither half.** They are derived from route rows and change when an operator
clicks a button in the Routes page; `desired.json`'s `publishPorts` carries them, host port equal to
container port as ADR-0033 requires. `config.yaml` carries the three ports whose numbers are an
operator's decision about their host.

**Editing the file is applying it.** The supervisor watches it on the same one-second poll. There is no
apply command, because a command the operator can forget to run is a config file that lies.

The dividing line is now sayable in one sentence: what is in `config.yaml` is the operator's, and
everything else is Watchtower's — Settings plus the database.

### 5. Postgres is a sibling the supervisor provisions, and an external one is supported from day one

`postgres: bundled` means the supervisor creates and health-waits a Postgres container, with the major
version pinned per Watchtower release rather than floating on a tag. Anything else in that field is a
connection string, and the supervisor provisions nothing.

`SelfPostgresLocator` keeps working unchanged. It parses Watchtower's own connection string and matches a
running Postgres-imaged container by that `Host`, over candidates drawn from three sources in order: the
explicit `Watchtower:Backup:SelfPostgresContainer` setting, else the Postgres-imaged containers of
Watchtower's own compose project, else every running Postgres-imaged container on the daemon
(`SelfPostgresLocator.cs:40-43`, implemented at `:149-153`). A supervised install has no compose project
label — decision 7 strips it — so it takes the third source, which is the one that already exists for
`docker run` installs and which logs that it is doing so. Naming the bundled container after the
connection string's host is therefore the entire integration.

**Major-version upgrades are a supervisor operation, in a later slice.** Dump, new container, restore, on
ADR-0027's existing `pg_dumpall` machinery. It is the right owner — the supervisor is the only process
that can stop Watchtower, replace the database under it and start it again without a sibling — and it is
not needed to ship this. *(Recorded as the decision-maker's position; confirm before implementation.)*

### 6. Readiness is `/health` over one private network, and rollback is the previous container

`/health` is an anonymous liveness endpoint, `app.MapGet("/health", () => Results.Ok("healthy"))`
(`WatchtowerHttpEndpoints.cs:56`), open by design. Reaching it needs a route, and both obvious ones are
closed. `docker exec` is out: the image purges `curl` after using it to add Docker's apt key
(`Dockerfile:38`) and carries no `HEALTHCHECK`. And the coordinator's `NetworkMode = "none"`
(`CoordinatorContainers.cs:126`) reaches nothing at all — right for a process that only spoke to the
socket, impossible for one that has to ask a question over HTTP.

**So the supervisor creates one user-defined bridge, `watchtower-supervisor`, joined by itself and
Watchtower's container and nothing else.** Readiness is `GET http://watchtower:8080/health` over it, on
Docker's own DNS. One network, two members, traffic in one direction.

It then keeps the previous container, stopped and renamed aside, until the new one answers. That is the
coordinator's rename-aside trick (`CoordinatorMode.cs:68-69`) with a real readiness check instead of
"the start call returned", which is the one thing the coordinator could never afford: it had to exit.

A failed start rolls back automatically and records it in `status.json` with the daemon's own error text,
not a paraphrase.

Manual rollback is an explicit `"rollback": { "toGeneration": 6 }` in `desired.json`, written under a
*new*, higher generation — not a decrement of the counter. Decrementing looks simpler and breaks the one
question the counter exists to answer: `appliedGeneration == generation` means converged, and a counter
that can move backwards makes that comparison ambiguous for as long as the two files disagree. A
rollback is a new thing to want, so it gets a new generation.

### 7. Migration is manual, and its guide is part of this decision

The new Watchtower version **requires** the supervisor. With no readable `status.json`, self-update and
port publishing are refused with a message naming the migration guide. Refused, not degraded: an
instance that quietly stops being able to update itself is one that is silently out of date on the day a
security fix ships.

**Adoption strips the compose labels, and that is the load-bearing part of the migration.**
`ContainerCloneSpec.FromInspect` clones the whole `Config` block onto the new image
(`ContainerCloneSpec.cs:69-71`) and has no label handling at all, so a naive adoption recreate would
carry `com.docker.compose.project` and friends forward. On the ownership recreate the supervisor
therefore **removes every `com.docker.compose.*` label** and adds `com.watchtower.supervised=true`.
Postgres is adopted the same way: a labels-only recreate on its existing volume, health-waited, with no
data touched — the volume is the database and the container is a wrapper around it.

Two things depend on it: migration step 5 is safe because `docker compose down` then finds no containers
belonging to the project, and `SelfPostgresLocator` falls to its third source (decision 5) because
Watchtower is now "not running under a Compose project", the branch that already works for `docker run`
installs.

The guide is written out below and becomes a section of [docs/upgrading.md](../upgrading.md) when this
is implemented.

### 8. The Kubernetes mapping is the point of the file contract

Under ADR-0010 the pieces translate rather than move:

| Here | There |
| --- | --- |
| `config.yaml` | `values.yaml` / a ConfigMap |
| `desired.json` | the controller's patch of its own Deployment and Service |
| `status.json` | the Deployment's status and the controller's conditions |
| the supervisor | the controller |
| published port routes | `hostPort` on the pod |
| bundled Postgres | an external server or a StatefulSet |

`hostPort` is named as the intended analogue rather than decided here. A port route is a LAN address on
*this node's* IP with a certificate naming that address (ADR-0033 decision 6), which is what `hostPort`
is; `NodePort` allocates from a cluster-wide range and adds a hop, and choosing between them is the
migration ADR's business.

The reusable part is the reconcile loop, and this is deliberately the second time Watchtower writes one:
ADR-0025's desired-state reconciler for stacks, and now one for itself. The Docker client is an adapter
underneath it.

### 9. Non-goals

No UI in the supervisor. No stack management. No Docker-socket proxying. No multi-node coordination —
one supervisor per node, and the cross-node role split stays
[multi-node-readiness.md](../multi-node-readiness.md) §4's problem. No automatic supervisor self-update
(decision 1). And no compose kept as a second supported install path: `deploy/docker/docker-compose.yml`
is **deleted** rather than deprecated. Two install paths means every future decision is taken twice, and
the compose path is the one that cannot express the deployment. Git history keeps the file, and the
migration guide references it by tag.

## Worked examples

**An operator adds a port route.** They create the route; `YarpProxyProvider.ApplyAsync` binds the
listener and issues the internal-CA leaf as it does today — through the port-route plane that PR #78
moves this out into, as an addendum to ADR-0033 decisions 2 and 6. Then, instead of
a confirmation dialog that spawns a coordinator, Watchtower writes `desired.json` with `9001` added to
`publishPorts` and generation 8. The supervisor notices within a second, composes the spec, and recreates
the container with `-p 9001:9001`. Watchtower comes back, reads `status.json`, sees
`appliedGeneration: 8`, and the Routes page shows the port as published. The restart is still a restart
of the management plane and still lives behind a confirmation, because a few seconds of downtime is a
few seconds of downtime whoever causes it. What is gone: the pre-spawn claim, the startup prune that
repairs it, the cross-guard against the self-update path, and the three-second sleep that existed so the
answer could beat the stop.

**An operator edits `config.yaml`** to add `WATCHTOWER__PROXY__ADMINEMAIL`. They save the file. The
supervisor's next poll sees a new mtime, re-composes the spec, finds the environment differs, and
recreates. Watchtower comes back with the variable set, and Settings → Reverse proxy shows the admin
email pinned and read-only, exactly as it shows a compose-set variable today (ADR-0014 decision 3).
There is no apply step and no `docker compose up -d`.

## Migration guide (draft of the `docs/upgrading.md` section)

Read the whole thing before starting. The point of no return is step 5.

**1. Pull the new image.** `docker pull swimmes/watchtower:<new>`.

**2. Adopt.** Run the supervisor once in adopt mode, against the running compose deployment:

```bash
docker run --rm --group-add "$(stat -c %g /var/run/docker.sock)" \
  -v /var/run/docker.sock:/var/run/docker.sock \
  swimmes/watchtower:<new> supervise adopt --print-config
```

It inspects the existing `watchtower` and `watchtower-postgres` containers, derives a `config.yaml` from
their ports, mounts, environment and connection string, and prints it. Nothing is changed. Read it,
correct it if it guessed wrong, and save it as `/srv/watchtower/watchtower.yaml`. (The `--group-add`
is how an unprivileged container gets at the socket; see decision 1 for the non-Linux forms.)

**3. Take ownership.** Run the supervisor for real, with the one-liner from decision 1. On its first
pass it recreates both containers once under its own ownership, and three things change about them:

- every `com.docker.compose.*` label is **removed** and `com.watchtower.supervised=true` added, which is
  what makes step 5 safe;
- both join the private `watchtower-supervisor` network, and the compose project's own network is left
  behind — nothing that runs is on it any more;
- the secrets the supervisor can derive from the running configuration move into `/data/self/`.

Postgres is recreated too, on its existing volume, with no data touched. That pair of recreates is the
migration: afterwards the containers are the supervisor's.

**4. Verify, before removing anything.** The UI's Settings page shows the supervisor's version and a
recent heartbeat. Your stacks, routes and accounts are all in Postgres and untouched. Check that a routed
host still serves, and that any port routes still report their ports as published.

**5. Remove what is left of the compose project.** Run this against **your own** compose file, wherever
you keep it — the file this repository used to ship is deleted (decision 9), and the path below is a
placeholder for yours:

```bash
docker compose -f /srv/watchtower/docker-compose.yml down
```

After step 3 the project has no containers left in it, because the labels that made them its containers
are gone, so this removes the leftover network and nothing that runs. **No `--remove-orphans`**: with
the labels stripped there are no orphans to find, and that flag is precisely the one that would have
removed the live containers had step 3 not run first. **Run it from where you ran `up`**, too — without
a top-level `name:` compose derives the project name from the directory, so `down` from anywhere else
addresses a project that does not exist and reports success having done nothing.

**Do not run `docker compose down -v`.** The `-v` deletes named volumes, and `watchtower-pg` is the
database. There is no recovery from that except a restore from a backup bundle (ADR-0027), and the
bundle you have is from before this migration.

**Backing out**, at any point before step 5: **stop the supervisor first**, remove the adopted
containers, then bring the compose project back up with the old image pinned.

```bash
docker stop watchtower-supervisor && docker rm watchtower-supervisor
docker rm -f watchtower watchtower-postgres
docker compose -f /srv/watchtower/docker-compose.yml up -d
```

The middle line looks alarming and is not: **the data is in the volumes**, `watchtower-pg` and
`watchtower-data`, and removing a container removes neither. It is required rather than tidy — the
adopted containers keep their `container_name`, so a `compose up` without it fails on a name conflict
and leaves you with neither deployment running. **Pin the pre-migration tag** in that compose file
(`image: swimmes/watchtower:<old>`) before bringing it up: `latest` may already be the version that
requires a supervisor, which would come back up and refuse self-update and port publishing (decision 7)
on a deployment that no longer has one.

The order is not a formality. A running supervisor and a running `compose up` are two reconcilers with
opposite opinions about one container: compose recreates it from the file, the supervisor recreates it
back, and the two take turns until someone notices. Stop one before starting the other.

After step 5 there is no compose project to go back to. Rebuilding one by hand from the file in git at
the pre-migration tag is possible and is not a supported path.

## Consequences

- **Most of this is deleted rather than moved.** `CoordinatorMode`, `CoordinatorContainers`, both
  cross-guards and their claim-then-verify protocol, the managed-port claim and its startup prune,
  `SelfPortPublishService`'s recreate half, `SelfUpdateService`'s spawn-and-watch half, and the self
  network joins. Only `ContainerCloneSpec`'s transformation moves.
- **ADR-0033's "compose drift", "image-tag drift on a ports-only recreate" and "a wedged coordinator
  blocks both recreate paths" consequences are retired**, along with PR #77's host-port contract
  consequences. Compose drift has no compose file. Image-tag drift was a symptom of a coordinator that
  had to be handed a tag it could not resolve safely; a supervisor holds the resolved id and the
  configured reference separately. A wedged coordinator has no coordinator.
- **The recreate authority leaves a process that sits on tenant ingress networks, and that is a genuine
  security improvement.** ADR-0022's first consequence is that Watchtower's own container joins every
  tenant's ingress network, so a compromised tenant reaches the process holding the Docker socket, the
  database and every credential. That process also held the authority to recreate itself. The supervisor
  is on **no tenant network**, and on exactly one network of its own — the private
  `watchtower-supervisor` bridge it shares with Watchtower's container and nothing else. On that bridge
  it listens for nothing: it is the client of `/health`, never a server, which non-goal 9 keeps true by
  giving it no HTTP surface to expose. So nothing reachable from a tenant network can reach it, and the
  two privileges now sit on different sides of a network boundary. The supervisor holds the Docker
  socket, which is not a new exposure: Watchtower already holds it, and anyone who owns the socket owns
  the host.
- **The restore coordinator does not simply move, and this needs care.** ADR-0027 §5's coordinator
  deliberately stops and starts Watchtower rather than recreating it: "That is deliberate: the
  container's filesystem survives, and with it the marker file the restarted Watchtower reads to find
  out what happened here" (`RestoreCoordinatorMode.cs:19-21`). A supervisor whose whole job is recreating
  would destroy that marker. The restore therefore becomes a supervisor operation with an explicit
  contract — the nonce moves into `/data/self/`, which survives a recreate because it is on the volume —
  rather than a lift-and-shift of the existing mode. *(Flagged: a real design consequence, not a
  detail.)*
- **Moving an ingress container port now costs a recreate of a few seconds.** Today
  `WATCHTOWER__PROXY__YARP__HTTPSPORT` rebinds Kestrel with no restart (ADR-0022's addendum) and the
  operator is expected to edit the compose mapping to match — which ADR-0022 already records as a trap:
  the Settings page reports the new port, traffic keeps arriving on the old one, and the instance
  crash-loops at the next restart. Declaring the container side trades the no-restart rebind for a
  mapping that cannot drift, and the UI says the restart is coming before the change applies. Enabling
  or disabling the proxy still rebinds with no restart, because that moves no port.
- **The data volume stops being optional.** Since ADR-0024 `/data` holds nothing Watchtower needs; the
  Dockerfile says so and the compose file tells a fresh install to leave the volume out. Two files and
  the generated secrets put it back. That is a reversal worth naming, and it is a small one — the volume
  now holds the deployment's contract and its secrets, both of which are small, and neither of which is
  Watchtower's state.
- **`/data/self/` becomes backup material, and losing it is a restore, not a repair.** It holds the
  generated secrets, and `WATCHTOWER__AUTH__KEYPROTECTIONSECRET` is the sharp one: sessions are
  invalidated, ACME certificates reissue themselves, and the **internal CA does not recover** — ADR-0033
  treats an unreadable CA key as fatal to issuance rather than silently replacing it, so every port route
  sits at `Error` until a human restores the secret or deletes the `internal_cas` row and re-imports the
  root on every device that trusted it. Generating the secret automatically makes that failure reachable
  by operators who never chose to have one, so `/data/self/` is named as backup material in the same
  three places the secret already is (yarp.md, upgrading.md, backups.md).
  The supervisor does refuse to generate a *replacement* secret when `status.json` says it generated one
  before, and that guard should be described honestly: it lives in the directory whose loss it guards
  against, so it catches a partial loss (one file deleted, a bad merge of the directory) and nothing
  else. **Losing `/data/self/` outright is a restore-from-backup event, full stop.**
- **Failure modes are asymmetric, in the right direction.** Supervisor down: Watchtower keeps serving
  every request, every stack keeps running, and only lifecycle changes wait — the UI says so from the
  heartbeat rather than from silence. Watchtower down: a restart policy already handles a container that
  *exits*, under compose as much as here; what the supervisor adds is health-gated recovery for one that
  stays up while broken, which is the case a restart policy cannot see. The count goes from two
  containers to three (supervisor, Watchtower, Postgres); the supervisor's `--restart always` is
  deliberate, and Watchtower's own policy becomes the supervisor's to set rather than the compose file's
  `unless-stopped`.
- **The supervisor must stay small, and that is a requirement rather than an aspiration.** It is the
  component with no supervisor of its own. Non-goal 9 is the enforcement: no UI, no HTTP surface, no
  stack knowledge, no database client. Read two files, talk to the daemon, write one file.
- **Development is untouched.** ADR-0005's Aspire AppHost stays the dev analogue; neither the supervisor
  nor `config.yaml` is used there.
- **The line citations here are against `main` at the time of writing, and PRs #77 and #78 move some of
  them** — the port-publish and port-route code cited from `SelfPortPublishService` and
  `YarpProxyProvider` moves into #78's port-route plane, where `proxy.ports.runtime` is renamed and
  `Proxy:Yarp:*` becomes `Proxy:PortRoutes:*`. The argument is unaffected, since what those files do is
  what is being deleted; re-resolve the numbers against the merged tree before implementing.
- **Docs to change when implemented:** `deploy/docker/` removed, the README install section rewritten
  around the one-liner, `docs/upgrading.md` gains the migration section drafted above, and ADR-0033's
  compose-drift and wedged-coordinator consequences get rewrite notes pointing here.

## Rejected alternatives

- **A `docker-compose.override.yml` that Watchtower writes and the operator includes.** It keeps compose
  and moves the burden onto the operator: every port publish becomes "we wrote a file, now run
  `docker compose up -d`", which is the manual step this exists to remove.
- **Watchtower authoring its own compose file and running `docker compose up -d` on it** — the Coolify
  model. It replaces the coordinator with a compose invocation and keeps the actual problem: `up -d` on
  the project containing Watchtower stops Watchtower mid-`up`, so the reconciler is still one Watchtower
  cannot run against itself.
- **A one-shot `bootstrap` mode that creates the deployment and exits, leaving the coordinator in place
  for changes.** A nicer install, and nothing else: after the bootstrap exits there is again nothing on
  the host with authority over Watchtower's container, so every consequence in the Context section
  survives intact.
- **An RPC between Watchtower and the supervisor.** A second API to authenticate, authorize, version and
  keep working while the database is down, in exchange for lower latency on an operation that already
  takes seconds. The file contract needs no auth story: whoever can write the volume already owns the
  deployment.
- **A separate, minimal supervisor image.** Genuinely attractive — less attack surface on the process
  holding the socket. Rejected for now because one image means one build, one tag, and no version-skew
  matrix between two artifacts sharing `ContainerCloneSpec`. Revisit if size or start time ever makes an
  operator hesitate.
- **`docker run` with no supervisor at all**, documented as the install. It is what a supervised
  deployment looks like from outside, and it loses the only thing that matters: nothing can recreate
  Watchtower from outside, so self-update and port publishing go back to the coordinator or disappear.
