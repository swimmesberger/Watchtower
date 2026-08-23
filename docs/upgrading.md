# Upgrading

## From SQLite to PostgreSQL

Watchtower used to keep everything in one SQLite file at `/data/watchtower.db`. Since
[ADR-0024](decisions/0024-postgresql-only-and-state-in-the-database.md) it uses PostgreSQL, and only
PostgreSQL — the file backend is gone rather than deprecated. Upgrading is a restart: add a database,
point Watchtower at it, start. The import runs itself.

You need: the new image, a PostgreSQL server, and the old `/data` volume still attached.

### 1. Add a database

The shipped compose example already has it. If yours predates it, copy the `postgres` service and the
`watchtower-pg` volume from [`deploy/docker/docker-compose.yml`](../deploy/docker/docker-compose.yml),
and set `POSTGRES_PASSWORD` to something of your own. Any PostgreSQL 14 or newer works — a managed one
is fine; nothing here needs superuser.

```bash
docker compose up -d postgres
```

### 2. Point Watchtower at it

Replace `WATCHTOWER__DBPATH` with a connection string. There is no default and no fallback to the file:
a Watchtower that cannot find this setting refuses to start, which is deliberate — the alternative is an
instance that silently comes up against an empty database.

```yaml
environment:
  WATCHTOWER__DATABASE__CONNECTIONSTRING: "Host=postgres;Database=watchtower;Username=watchtower;Password=..."
```

Leave the `watchtower-data` volume mounted. It holds the SQLite file about to be imported, and the key
and certificate files the same start carries into the database (see step 4).

### 3. Start

```bash
docker compose up -d
```

That is the whole upgrade. The first start finds an empty PostgreSQL database beside
`/data/watchtower.db`, applies the migrations, copies every table across and logs what it moved:

```
info: Empty PostgreSQL database and a legacy SQLite database at /data/watchtower.db: importing it
      (ADR-0024). The file is not deleted.
info:   sqlite-import| Imported:
info:   sqlite-import|   realms                                          2
info:   sqlite-import|   stacks                                         14
info:   sqlite-import|   routes                                         19
info:   sqlite-import|   users                                           3
info:   sqlite-import|   elarion_settings                               27
info:   sqlite-import|   deploy_events                                 412
info:   sqlite-import| Total: 496 row(s) across 31 table(s).
info: Imported /data/watchtower.db. Check your stacks, routes and accounts, then delete the old file —
      nothing reads it any more.
```

Three things have to be true for it to run, and all three are checked before a row is written: the
PostgreSQL database holds nothing but what the migrations seed, the file is there, and this database has
not been imported into before. A marker records the decision, so leaving the old file mounted for a while
is safe — it is read once and never again.

**If it fails, Watchtower still starts**, on the empty database, and says so. Nothing was written: the
whole import is one transaction. An unreadable file is not a reason to leave an instance restarting, and
the next start tries again once you have fixed it — or run it by hand (below) to see the failure in full.

Types are converted by the *target* column, not guessed from the source: SQLite's `0`/`1` become real
booleans, and its timestamp text becomes `timestamptz` normalized to UTC. Identity sequences are moved
past the imported rows, so the next stack you create does not collide with an imported one.

#### Importing by hand

The automatic import only looks at `/data/watchtower.db` (or whatever your old `WATCHTOWER__DBPATH`
named, which is still honoured for this one upgrade). For a database kept anywhere else, or to retry a
failed import and read the output directly, run it yourself against the still-empty database:

```bash
docker compose run --rm watchtower --import-sqlite /srv/wherever/watchtower.db
```

It refuses to run against a database that already holds rows, so a second run is a no-op with a clear
message rather than a duplicated estate.

**Columns the model no longer has** are reported as `warning: <table>.<column> exists in the source but
not in the model — not imported.` and skipped. There is one exception, because its value is
load-bearing: the importer converts the pre-2026-08-22 realm login-host column (`realms.auth_host`,
which [ADR-0023](decisions/0023-login-hosts-are-watchtower-self-routes.md) replaced with a Watchtower
route). Each realm that named a host gets that route back and keeps it as its login route, and the
summary says `converted N legacy realm login host(s)`. A hostname already served by one of your
application routes is left alone with a warning — pick another hostname for that realm afterwards.
Read the warnings once before you delete the old file.

### 4. Check, then clean up

The same start carries the **key and certificate files** into the database, once and automatically:

```
info: Imported legacy state into the database: 1 signing key(s), 2 data-protection key(s),
      1 ACME account(s), 19 certificate(s). The files under /data/auth-keys and /data/proxy-certs are
      no longer read and can be removed.
```

That is what keeps everyone signed in across the upgrade (the data-protection key ring), keeps the
ACME account and its rate-limit history, and stops every certificate being re-ordered on the day you
upgrade. It runs once — a marker records that it did — and never overwrites anything already in the
database, so a certificate issued since is safe. **Nothing is deleted**: the files stay exactly where
they were, which is what makes step "rolling back" possible.

Sign in and check that your stacks, routes, accounts and certificates are there. Once you are
satisfied, delete the old files — nothing reads them any more:

```bash
docker compose exec watchtower rm -rf /data/watchtower.db /data/auth-keys /data/proxy-certs
```

At that point the data volume holds nothing Watchtower needs, and you can drop the mount entirely at
your next convenient restart.

### What else changed

- **Backups of Watchtower's own state** are now a `pg_dump`, not a file copy. See
  [docs/backups.md](backups.md#backing-up-watchtower-itself).
- **The metrics backend `sqlite` is now called `database`.** Semantics are unchanged — history is
  persisted in Watchtower's own database. A stored or env-pinned `sqlite` is still accepted and reads
  as `database`, so nothing breaks if you miss it; the UI and new writes use the new name.
- **`Watchtower:DbPath` / `WATCHTOWER__DBPATH` no longer exist.** A leftover value is still read *once*,
  by the automatic import in step 3, so a deployment that moved its database file is found where the file
  actually is; after that it is ignored.
- **`Proxy:Yarp:CertPath` and `Auth:KeyPath` no longer exist either.** Certificates, the ACME account,
  the identity-assertion signing key and the data-protection key ring are rows now. A leftover value
  for either is still read *once*, by the import above, so a deployment that moved those directories
  is imported from where its files actually are; after that it is ignored. The read-only "certificate
  directory" field is gone from Settings → Reverse proxy, because there is nothing for it to name.
- **`Kestrel__Endpoints__ProxyHttp__Url` and `Kestrel__Endpoints__ProxyHttps__Url` are no longer the
  ingress ports.** They are gone from the shipped image and ignored where one is still set. The ports
  are `WATCHTOWER__PROXY__YARP__HTTPPORT` / `__HTTPSPORT`, or the yarp block of Settings → Reverse
  proxy, and the listeners exist only while the built-in provider is enabled — bound, unbound and moved
  without a restart. Published host ports are unchanged on the defaults (`80:8081`, `443:8443`), and
  `Kestrel__Endpoints__Http__Url` still owns the management port. See
  [docs/reverse-proxy/yarp.md](reverse-proxy/yarp.md#switching-at-runtime).
- **Private keys in the database can be encrypted at rest.** Set
  `WATCHTOWER__AUTH__KEYPROTECTIONSECRET` to a long random passphrase and keep it out of the database
  and out of your database backups. It covers the certificate keys, the ACME account key, the
  identity-assertion signing key and the data-protection key ring. Optional so the upgrade stays one
  decision; without it, all four are stored exactly as the files were and the host logs one warning at
  startup. You can set it later, and nothing needs migrating — but it is not retroactive in one go:
  the signing key and the ACME account key are encrypted on the next start, certificates as they
  renew, and the key ring only for keys generated from then on (earlier ring elements stay plaintext
  and keep loading). Losing it once set invalidates sessions and forces certificate reissuance.
- **More than one instance is now possible for the proxy/auth plane.** Every instance serves every
  routed host from the same tables; exactly one holds the `acme-issuer` lease and orders certificates;
  route, realm and certificate changes reach the others over PostgreSQL `LISTEN/NOTIFY`. Nothing about
  a single-instance deployment changes.

### Rolling back

There is no downgrade path in the tooling. If you need to go back, redeploy the previous image with the
old `WATCHTOWER__DBPATH`, the `/data/watchtower.db` you kept, and the `/data/auth-keys` and
`/data/proxy-certs` directories the import left untouched — which is the reason step 4 leaves deleting
all three until last. Anything you changed after the import will not be in them.
