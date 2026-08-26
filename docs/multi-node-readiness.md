# Multi-node readiness — from one box to a small cluster

Status: design note (2026-08-23). Records where Watchtower stands on the road from a single
Docker host to the multi-node Kubernetes target of [ADR-0010](decisions/0010-target-kubesolo-runtime.md),
what has to change, in which order, and why. Companion to
[scaling-beyond-one-node.md](scaling-beyond-one-node.md) (the cluster-shape survey) and to the
proxy ADRs [0022](decisions/0022-in-process-yarp-proxy.md) / [0023](decisions/0023-login-hosts-are-watchtower-self-routes.md),
which made the in-process proxy the default and gave it a first-class self-route model — the two
decisions that make a clean cluster story possible at all.

The sections are ordered the way the work should happen. The first two are being done now
(stacked on the proxy PR); the rest are recorded so the later ADRs start from an agreed shape.

## 1. Is Watchtower stateless? — No, and here is the inventory

"Stateless" means: any instance can serve any request, and an instance can be killed and replaced
without losing anything. Today the durable facts that are already server-side and keyed by id
(sessions, login codes, grants, realms, routes, audit, settings, backups, deploy history) live in
the database, which is the right half. The other half does not:

| State | Lives in | Why it blocks a second instance |
| --- | --- | --- |
| ~~The database itself~~ | **PostgreSQL** (`Watchtower:Database:ConnectionString`) — *done*, ADR-0024 | was a single-writer SQLite file on one disk, the hard blocker; see §2 |
| ~~ACME account key, issued certificates~~ | **`acme_accounts` / `proxy_certificates`** — *done*, ADR-0024; one instance orders, all serve | were PEM files under the removed `Proxy:Yarp:CertPath` → every instance would order its own certificates, hitting Let's Encrypt rate limits and answering SNI differently per node |
| ~~HTTP-01 challenge tokens~~ | **`acme_http_challenges`** — *done*, ADR-0024 | were in-memory; the CA's validation request lands on whichever node answers port 80, not necessarily the one that published the token |
| ~~Identity-assertion signing key, ASP.NET data-protection key ring~~ | **`signing_keys` / `data_protection_keys`** — *done*, ADR-0024 | were files under the removed `Auth:KeyPath` → a token or cookie minted on node A was unreadable on node B, OIDC correlation/nonce cookies included |
| ~~Route table (`ProxyRouteTable`), certificate SNI map~~ | in-memory, **derived** — and now re-projected on the cross-instance change signal (*done*, ADR-0024) | was refreshed only by an in-process `ApplyAsync`, so a change on node A was invisible to node B until its next reconcile |
| Login rate limiter, live metrics ring, SSE deploy-output broadcaster | in-memory | per-instance views; inconsistent, not dangerous |
| Background loops (`DeployQueueService`, `BackupQueueService`, `CertificateManager`, `AutoDeployBackgroundService`, `StackUpdateBackgroundService`, `ImagePruneBackgroundService`, `SelfUpdateBackgroundService`, `CiRunnerOrchestrator`, `MetricsSampler`, the proxy providers) | one `IHostedService` per process | each assumes it is the only instance: no leader election, no job claiming — two instances would run every deploy twice and race on every reconcile |
| Runtime access | the Docker socket (`DockerEngineClient`), compose project names, per-stack ingress networks | node-local by nature; replaced wholesale by the Kubernetes API in the ADR-0010 port |

Two conclusions. First, the proxy/auth plane is the part that is *closest* to multi-instance — its
authoritative state is a handful of files and one in-memory map. Second, nothing in the YARP or
self-route design has to be undone: the cluster shape is the same process with the file-backed
state moved into the database and a role flag (§4).

## 2. Database: PostgreSQL replaces SQLite (done)

SQLite cannot be shared, and running two database backends with different semantics (transaction
isolation, `ExecuteUpdate` behaviour, date/time storage, raw-SQL migrations) would double the
testing surface for no product benefit. Decided and shipped 2026-08-23,
[ADR-0024](decisions/0024-postgresql-only-and-state-in-the-database.md): **PostgreSQL is the single
supported database.**

What landed:

- Npgsql EF Core provider; the migration history regenerated as one `InitialPostgreSql` (the SQLite
  history was not portable — table rebuilds, `strftime`, `PRAGMA journal_mode=WAL`); snake_case
  naming kept. The seeded operator realm moved onto the model as `HasData`, so it is part of the
  schema every environment scaffolds rather than one migration's hand-written INSERT.
- Optimistic concurrency on editable rows via the `xmin` system column as the EF concurrency token
  (routes, stacks, realms, groups — the Elarion settings store already carries `expectedVersion`),
  surfaced as a `Conflict` result by one assembly-wide decorator. Users keep Identity's
  `ConcurrencyStamp`, which survives the detached read/write the user store is built on.
- A connection string (`Watchtower:Database:ConnectionString`, falling back to
  `ConnectionStrings:watchtower`) replaces `DbPath`; `docker-compose.yml` gains a `postgres` service
  and the Aspire AppHost a Postgres resource; both integration suites run against a real PostgreSQL
  — Testcontainers by default, or the server `WATCHTOWER_TEST_PG` names (CI's service container, or
  a locally started one) — with a database per test host cloned from a migrated template.
- Existing SQLite installations were carried across by a one-shot `--import-sqlite <path>` that
  copied every table into the configured PostgreSQL and refused a non-empty target
  ([upgrading.md](upgrading.md)); the importer was removed on 2026-08-25 once every installation
  had migrated, and with it the last SQLite code.
- The SQLite-era workarounds went with it: the raw-SQL expiry sweeps, the client-side sorts that
  existed because SQLite cannot `ORDER BY` a `DateTimeOffset`, the in-process lockout computation,
  and `PRAGMA journal_mode=WAL`. ADR-0013's `sqlite` metrics backend is now `database` (same
  semantics; the old value still reads).

Still to come here, with the work that needs them:

- Work that several instances may pick up — deploy and backup jobs, certificate orders per host —
  claimed with `SELECT … FOR UPDATE SKIP LOCKED` or a per-key advisory lock, never with "I am the
  only worker" (§4).
- Singleton loops holding a lease (§3) rather than assuming singleness.

## 3. Proxy/auth state moves into the database (done)

The rows that replace the files, all keyed so any instance can serve them, and the framework
primitives that carry the cross-instance parts (Elarion ≥ 0.2.6 — the pinned 0.2.3 preview
predates all of them; the bump is the first commit of the stacked PR):

- **Certificates**: leaf + chain PEM, private key (encrypted at rest through the data-protection
  system), `not_before`/`not_after`, issuer, thumbprint, per host — a plain EF entity with a
  `bytea`/`byte[]` payload. (Elarion's `IBlobStore` was considered and rejected here: it is a
  streaming store for large objects; a certificate is a few kilobytes and wants to be queryable.)
- **ACME account**: one row per directory URL (key PEM encrypted at rest, account URL).
- **HTTP-01 challenge tokens**: `token → key authorization` with an expiry; the challenge
  middleware on *any* node answers from the table.
- **Keys**: the ES256 identity-assertion signing key in the database (encrypted at rest), and the
  ASP.NET data-protection key ring via `PersistKeysToDbContext<WatchtowerDbContext>` — Elarion has
  no key-ring helper, the ASP.NET one composes fine — so tokens, cookies and OIDC correlation state
  are valid cluster-wide.
- **Single issuer**: certificate ordering runs only on the instance holding the Elarion role lease
  `acme-issuer` (`Elarion.Coordination.PostgreSql`, `IRoleLease.IsHeld` checked per renewal pass);
  every instance serves. The same primitive later carries the `control` role (§4).
- **Change signal**: the settings store's cross-instance change source
  (`Elarion.Settings.PostgreSql`, Postgres `LISTEN/NOTIFY`, commit-gated) carries a
  `Watchtower:Proxy:RoutesVersion` counter that every route/realm/certificate write bumps; each
  instance watches it (`ISettingsManager.Watch`) and re-projects its route table and SNI map.
  Chosen over Elarion client events (those are browser-facing, at-most-once hints) and over a
  hand-rolled `NpgsqlConnection.Notification` listener (one fewer moving part).
- **Scheduled jobs** claim occurrences through `Elarion.Scheduling.EntityFrameworkCore`
  (Postgres claims), so `[ScheduledJob]`s such as the backup schedule run once cluster-wide.

All of the above landed with ADR-0024's second commit. What is *not* done, and is deliberately left
to §4: the deploy and backup queues, the reconcilers, and every other background loop still assume
they are the only instance — so a second instance today is safe for the proxy/auth plane and not for
the rest. `RenewNowAsync` on a non-holder reports where the work happens rather than forwarding to it;
a role-holder proxy needs the advertised address on the lease row and an authenticated hop between
instances, which is §4's business.

## 4. Edge and control: split the *deployment*, not the code

The port split already shipped (management `Http` 8080 bound privately; ingress `ProxyHttp` 8081
and `ProxyHttps` 8443; unknown hosts on ingress get 404) is the single-node form of a pod split.
The proposal for the cluster is one image with a role flag:

| `WATCHTOWER__ROLE` | Runs | Deployment | Database privileges |
| --- | --- | --- | --- |
| `all` (default) | everything — today's single node | one container | full |
| `edge` | YARP host dispatch, ACME challenge answering, the IdP surface (login, callback, userinfo, JWKS), `AccessVerifier`, certificate SNI serving | `DaemonSet` with `hostPort` 80/443 (or a `Deployment` behind MetalLB/kube-vip) | read routes/realms/certificates/grants; write sessions, login codes, auth audit (session validation slides the window, so it is not read-only) |
| `control` | management API + UI, deploy/backup queues, reconcilers, certificate *issuance*, self-update, CI runners | `Deployment`, 1–2 replicas, leader-elected | full |

Why one codebase: `AccessVerifier`, the route projection (`ProxySiteProjection`), the self-route
model and the realm model are shared by construction; two repositories would drift exactly where
drift is a security bug. Why a pod boundary anyway: an edge pod compromise must not hold management
privileges, edge scales with the node count, control does not.

What does *not* change: the route table remains the single source of truth; `Local` sites (self-
routes) are served by whichever edge pod receives the request; the management UI is reached from
outside only through a self-route, exactly as today.

## 5. Kubernetes shape (ADR-0010 port)

- **Ingress is Watchtower itself.** No `Ingress` objects, no ingress-nginx: the edge role *is* the
  cluster's edge. YARP's Kubernetes ingress controller (`Yarp.Kubernetes.Controller`, preview) is not
  needed — it exists to turn `Ingress` resources into YARP config, and our source of truth is the
  route table. Upstreams are plain cluster DNS (`http://{service}.{namespace}.svc:{port}`); the
  per-stack ingress networks and the alias join disappear.
- **External identity providers** plug into the same process (the ASP.NET OpenID Connect handler;
  Entra ID, Keycloak, …) — no oauth2-proxy hop, because the IdP, the verifier and the forwarder are
  one pipeline. The only cluster-specific requirement is §3's shared data-protection key ring.
- **Leader election** for the control role and for singleton loops uses a Postgres-backed lease
  (runtime-agnostic, works on one box and on KubeSolo) rather than a Kubernetes `Lease`, so the
  same binary runs everywhere.
- **Storage**: PostgreSQL on local disk with its own replication (see
  [scaling-beyond-one-node.md §3](scaling-beyond-one-node.md#3-option-b--k3s-recommended)); nothing
  else in Watchtower needs a volume once §3 is done.

## 6. Sequencing

1. ~~PostgreSQL replaces SQLite (§2)~~ — prerequisite for everything else. **Done** (ADR-0024).
2. ~~Proxy/auth state into the database, locked issuance, change signal (§3).~~ **Done** (ADR-0024).
3. Job claiming and leases for the background loops; the role flag and the database-privilege
   split (§4) — their own ADR, before the runtime port so the port targets them.
4. The Kubernetes runtime port (ADR-0010): edge `DaemonSet`, control `Deployment`, Docker adapters
   replaced by the Kubernetes API.

## 7. Non-goals (for now)

Multi-region, active/active PostgreSQL, and anything that needs consensus beyond "one leader at a
time" — Watchtower's natural size is one box to a handful of nodes, and every step above is
designed to be correct on one node first.
