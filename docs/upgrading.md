# Upgrading

## From SQLite to PostgreSQL

Watchtower used to keep everything in one SQLite file at `/data/watchtower.db`. Since
[ADR-0024](decisions/0024-postgresql-only-and-state-in-the-database.md) it uses PostgreSQL, and only
PostgreSQL — the file backend is gone rather than deprecated. Upgrading is one command, run once.

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

Leave the `watchtower-data` volume mounted. It holds the SQLite file you are about to import, and the
key and certificate files the same upgrade carries into the database (see step 4).

### 3. Import the old database

```bash
docker compose run --rm watchtower --import-sqlite /data/watchtower.db
```

This applies the migrations to the empty PostgreSQL database, then copies every table across and prints
what it moved:

```
Importing /data/watchtower.db into PostgreSQL.
Target migrated.
Imported:
  realms                                          2
  stacks                                         14
  routes                                         19
  users                                           3
  elarion_settings                               27
  deploy_events                                 412
  ...
Total: 496 row(s) across 31 table(s).
```

It refuses to run against a database that already holds rows, so a second run is a no-op with a clear
message rather than a duplicated estate. If it fails, nothing was written: the whole import is one
transaction. Fix the cause and run it again.

Types are converted by the *target* column, not guessed from the source: SQLite's `0`/`1` become real
booleans, and its timestamp text becomes `timestamptz` normalized to UTC. Identity sequences are moved
past the imported rows, so the next stack you create does not collide with an imported one.

**Columns the model no longer has** are reported as `warning: <table>.<column> exists in the source but
not in the model — not imported.` and skipped. There is one exception, because its value is
load-bearing: the importer converts the pre-2026-08-22 realm login-host column (`realms.auth_host`,
which [ADR-0023](decisions/0023-login-hosts-are-watchtower-self-routes.md) replaced with a Watchtower
route). Each realm that named a host gets that route back and keeps it as its login route, and the
summary says `converted N legacy realm login host(s)`. A hostname already served by one of your
application routes is left alone with a warning — pick another hostname for that realm afterwards.
Read the warnings once before you delete the old file.

### 4. Start normally, then clean up

```bash
docker compose up -d
```

The first normal start also carries the **key and certificate files** into the database, once and
automatically — nothing to run:

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
- **`Watchtower:DbPath` / `WATCHTOWER__DBPATH` no longer exist.** A leftover value is ignored.
- **`Proxy:Yarp:CertPath` and `Auth:KeyPath` no longer exist either.** Certificates, the ACME account,
  the identity-assertion signing key and the data-protection key ring are rows now. A leftover value
  for either is still read *once*, by the import above, so a deployment that moved those directories
  is imported from where its files actually are; after that it is ignored. The read-only "certificate
  directory" field is gone from Settings → Reverse proxy, because there is nothing for it to name.
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
