# ADR-0024: PostgreSQL is the only database, and the proxy/auth plane keeps its state in it

- Status: Accepted (decisions 1–3 and 7 landed with the PostgreSQL switch; 4–6 with the state move)
- Date: 2026-08-23
- Related: [ADR-0010](0010-target-kubesolo-runtime.md) (the multi-node direction),
  [ADR-0013](0013-sqlite-metrics-history.md) (the SQLite-persisted metrics tier, which moves with it),
  [ADR-0022](0022-in-process-yarp-proxy.md) / [ADR-0023](0023-login-hosts-are-watchtower-self-routes.md)
  (the in-process proxy whose state this ADR relocates),
  [docs/multi-node-readiness.md](../multi-node-readiness.md) (the inventory and sequencing this ADR
  executes the first two steps of).

> **Note (2026-08-25).** The one-shot importer of decision 2 — `--import-sqlite <path>` and the
> automatic first-start import — has been removed, every known installation having migrated to
> PostgreSQL. The decision itself stands as recorded; this note retires the tool, not the choice.
> A pre-ADR-0024 installation now upgrades through an intermediate image built before 2026-08-25
> (see [docs/upgrading.md](../upgrading.md)).

## Context

Watchtower stored everything in one SQLite file. That was the right call for a single box — no
service to run, one volume to back up — and it is the wrong call the moment a second instance
exists, because SQLite is one writer on one disk. The multi-node direction (ADR-0010) was recorded
without a schedule; the in-process proxy (ADR-0022) made the question concrete, because the proxy
plane is the part of Watchtower that most obviously wants to run on every node.

Two facts decided the timing:

1. **Two database backends cost more than one migration.** Supporting SQLite *and* PostgreSQL
   means two sets of migrations (the SQLite history is not portable: table rebuilds, `strftime`,
   `PRAGMA journal_mode=WAL`), two transaction-isolation models, two `DateTimeOffset` storage
   semantics, and a test matrix nobody would run. Watchtower has one deployment shape per size;
   it does not need a database abstraction layer.
2. **Every cross-instance primitive the framework offers is PostgreSQL-only**, except the outbox:
   role leases for leader election (`Elarion.Coordination.PostgreSql`), `LISTEN/NOTIFY` change
   propagation for settings (`Elarion.Settings.PostgreSql`) and client events, scheduler
   occurrence claims (`Elarion.Scheduling.EntityFrameworkCore` uses `pg_advisory_xact_lock`),
   actor snapshots and homes, and the blob store. Staying on SQLite would mean hand-rolling a
   lease, a claim coordinator and an invalidation channel — three things the framework already
   ships, tested, for PostgreSQL.

Meanwhile the proxy/auth plane kept authoritative state **outside** the database: ACME account key
and certificates as PEM files, HTTP-01 challenge tokens in memory, the identity-assertion signing
key and the data-protection key ring as files. Each of those is a reason a second instance cannot
exist (see the inventory in docs/multi-node-readiness.md §1).

## Decision

1. **PostgreSQL (via Npgsql) is Watchtower's only database.** SQLite support is removed rather than
   kept alongside. The connection string comes from `Watchtower:Database:ConnectionString`
   (`WATCHTOWER__DATABASE__CONNECTIONSTRING`); `Watchtower:DbPath` is gone. The EF migration history
   is regenerated for PostgreSQL from the current model; the SQLite migrations are deleted. Naming
   stays snake_case.
2. **A one-shot importer carries existing installations across.** `--import-sqlite <path>` copies
   every table in dependency order from an existing SQLite file into the configured PostgreSQL and
   refuses to run against a non-empty target. It is the only SQLite code that remains, and it is a
   command, not a runtime mode.
3. **Optimistic concurrency where several writers can meet.** Editable rows use the PostgreSQL
   `xmin` system column as the EF concurrency token; the Elarion settings store already carries
   `expectedVersion`. Work that several instances may pick up is *claimed*, never assumed: scheduled
   occurrences through the Elarion scheduler claims table, and (next ADR) deploy/backup jobs through
   `SELECT … FOR UPDATE SKIP LOCKED`.
4. **The proxy/auth plane's state lives in the database.** Certificates (leaf + chain PEM, private
   key encrypted at rest, validity, issuer, thumbprint) and the ACME account (per directory URL) are
   EF entities with binary payloads; HTTP-01 challenge tokens are rows with an expiry so *any*
   instance answers `/.well-known/acme-challenge/*`; the ES256 signing key is a row, encrypted at
   rest; the data-protection key ring is persisted with `PersistKeysToDbContext<WatchtowerDbContext>`.
   `Proxy:Yarp:CertPath` and `Auth:KeyPath` disappear. The in-memory SNI map and route table remain
   caches of these tables. Elarion's blob store was deliberately not used: certificates are small,
   queryable rows, not streamed objects.
5. **Exactly one instance orders certificates.** `CertificateManager` runs its issuance/renewal pass
   only while this instance holds the Elarion role lease `acme-issuer`; every instance serves from
   the table. The lease, not a file lock, because the same primitive later carries the `control`
   role.
6. **Instances learn about changes through the settings store's change channel.** Every route,
   realm or certificate write bumps `Watchtower:Proxy:RoutesVersion`; every instance watches it
   (`ISettingsManager.Watch`) through `Elarion.Settings.PostgreSql`'s commit-gated `LISTEN/NOTIFY`
   and re-projects its route table and SNI map. Chosen over client events (browser-facing,
   at-most-once hints) and over a hand-rolled `NpgsqlConnection.Notification` listener (one more
   thing to supervise). The value written is a fresh random string rather than a counter: nothing
   reads it, only notices it changed, so an unconditional write makes concurrent bumps a non-event
   instead of a compare-and-set loop. The bump sits in `ProxyProviderRouter.ApplyAsync` — the one
   call every route and realm write handler already ends with — rather than repeated per handler,
   which is the kind of rule a new handler can be written without. Watchers debounce (250 ms) and
   re-read the database rather than replaying a payload, so a burst of writes costs one re-projection
   and a lost notification costs nothing beyond the next one.
7. **Elarion is bumped to 0.2.6** (from the 0.2.3 preview) as the first commit, because the
   primitives above do not exist in the pinned version.

## Consequences

- **Operators run PostgreSQL.** The compose example ships a `postgres` service with a named volume;
  the Aspire AppHost adds a Postgres resource for development; the integration tests run against a
  real PostgreSQL (Testcontainers locally, the CI service container on GitHub Actions) instead of an
  in-memory SQLite connection. "One container, one volume" is no longer the deployment shape — that
  is the price of a second instance ever being possible.
- **Upgrading is a restart.** Add the `postgres` service, set the connection string, start. The first
  start that finds an empty PostgreSQL database beside a `/data/watchtower.db` imports it itself, then
  carries the key and certificate files across in the same start; the operator's only remaining job is
  deleting the old files once they are satisfied. The import runs before the settings snapshot is taken
  from the database — otherwise the first post-upgrade process would run on an empty database's defaults
  while the imported rows said otherwise, and `Auth:Enabled` is among them. `--import-sqlite <path>`
  remains for a file kept anywhere but the default, and for a deliberate retry after a failure: the
  automatic path never stops the host, because an unreadable upgrade artefact is a bad reason to
  restart-loop a deployment. Documented in docs/upgrading.md (new) and the compose comments.
- **Backups of Watchtower's own state** become a PostgreSQL concern (`pg_dump` or the stack-backup
  machinery against the Watchtower database), not a file copy; ADR-0013's "sqlite" metrics backend
  is renamed `database` and keeps its semantics.
- **The settings key is internal**: `Watchtower:Proxy:RoutesVersion` is never offered in the UI and is
  not part of the proxy card's paths. Pinning it through the environment would freeze the one write
  that tells the other instances anything changed, leaving their route tables to drift with nothing
  reporting it. Same shape as `Watchtower:Proxy:ProviderMigrated` and
  `Watchtower:Auth:LoginHostsConverted`, and now `Watchtower:Auth:FileStateImported` and
  `Watchtower:Database:SqliteImported` — the markers the two one-shot imports write.
- **The upgrade carries the files across itself.** On the first start after the upgrade, an existing
  `/data/auth-keys` and `/data/proxy-certs` (or whatever the removed `Auth:KeyPath` /
  `Proxy:Yarp:CertPath` named) are imported into the new tables: the signing key, the data-protection
  key ring, the ACME account and every certificate. The key ring is the reason it is automatic rather
  than a documented step — without it the upgrade invalidates every session and every in-flight OIDC
  login, which reads as an outage. Nothing is deleted; removing the files is a step the operator takes
  when they are satisfied.
- **Keys in the database** raise the stakes of a database dump. Private keys (certificates, ACME
  account, identity-assertion signing key) are encrypted at rest with AES-GCM under a key derived
  from `Watchtower:Auth:KeyProtectionSecret` when that setting is present (env-pinnable, never
  stored in the database itself); when it is absent they are stored as the files were — unencrypted
  — and the host logs a warning at startup. Optional rather than required so an upgrade stays one
  command; the data-protection key ring is encrypted under the same secret when one is set, and stored
  as ASP.NET's default plaintext elements when it is not — the same exposure the key directory on the
  data volume had. Losing the
  secret invalidates sessions and forces certificate reissuance — the blast radius the old key
  files already had.
- The proxy becomes runnable on N nodes from the same table; the remaining single-instance
  assumptions (deploy/backup queues, reconcilers, the role split) are the next ADR and are listed in
  docs/multi-node-readiness.md §4–§6.
- SQLite-specific code paths (`PRAGMA journal_mode=WAL`, the pre-DI settings snapshot reader, raw
  `strftime` in migrations) are deleted; anything that relied on SQLite's case-insensitive `NOCASE`
  comparisons is made explicit (`ToLower()` on both sides or `citext`-free normalized columns).
