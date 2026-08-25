# Upgrading

## From SQLite to PostgreSQL

Watchtower used to keep everything in one SQLite file at `/data/watchtower.db`. Since
[ADR-0024](decisions/0024-postgresql-only-and-state-in-the-database.md) it uses PostgreSQL, and only
PostgreSQL — the file backend is gone rather than deprecated.

The one-shot importer that carried the SQLite file across — the automatic first-start import and the
`--import-sqlite <path>` command — was removed on 2026-08-25, once every known installation had
migrated. Current images cannot read the old file at all.

**Still on a SQLite-era install?** Upgrade in two steps: deploy the last image that still ships the
importer (any image built from `main` before 2026-08-25), follow that image's copy of this document
to complete the import, then move to the current image. The importer refused non-empty targets and
never deleted the source file, so the intermediate step is safe to retry.

### Key and certificate files

Separate from the (removed) SQLite row import, the first start after the upgrade carries the **key
and certificate files** into the database, once and automatically — this import still exists:

```
info: Imported legacy state into the database: 1 signing key(s), 2 data-protection key(s),
      1 ACME account(s), 19 certificate(s). The files under /data/auth-keys and /data/proxy-certs are
      no longer read and can be removed.
```

That is what keeps everyone signed in across the upgrade (the data-protection key ring), keeps the
ACME account and its rate-limit history, and stops every certificate being re-ordered on the day you
upgrade. It runs once — a marker records that it did — and never overwrites anything already in the
database, so a certificate issued since is safe. **Nothing is deleted**: the files stay exactly where
they were, which is what makes rolling back possible.

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
- **`Watchtower:DbPath` / `WATCHTOWER__DBPATH` no longer exist.** A leftover value is ignored (it was
  honoured one last time by the removed SQLite import).
- **`Proxy:Yarp:CertPath` and `Auth:KeyPath` no longer exist either.** Certificates, the ACME account,
  the identity-assertion signing key and the data-protection key ring are rows now. A leftover value
  for either is still read *once*, by the key/certificate file import above, so a deployment that
  moved those directories is imported from where its files actually are; after that it is ignored.
  The read-only "certificate directory" field is gone from Settings → Reverse proxy, because there is
  nothing for it to name.
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
`/data/proxy-certs` directories the imports left untouched — which is the reason the clean-up above
comes last. Anything you changed after the import will not be in them.
