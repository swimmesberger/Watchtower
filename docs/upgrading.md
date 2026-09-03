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
  and out of your database backups. It covers the certificate keys, the ACME account key, the internal
  CA's signing key, the identity-assertion signing key and the data-protection key ring. Optional so the
  upgrade stays one decision; without it, all five are stored exactly as the files were and the host logs
  one warning at startup. You can set it later, and nothing needs migrating — but it is not retroactive
  in one go: the signing key, the ACME account key and the internal CA are encrypted on the next start,
  certificates as they renew, and the key ring only for keys generated from then on (earlier ring
  elements stay plaintext and keep loading). Losing it once set invalidates sessions and forces
  certificate reissuance — automatic for ACME, but **not** for the internal CA if you use port routes:
  that key is never silently replaced, so recovery means deleting the `internal_cas` row and re-importing
  the new root on every device that trusted the old one
  ([ADR-0033](decisions/0033-port-routes-and-internal-ca.md)).
- **More than one instance is now possible for the proxy/auth plane.** Every instance serves every
  routed host from the same tables; exactly one holds the `acme-issuer` lease and orders certificates;
  route, realm and certificate changes reach the others over PostgreSQL `LISTEN/NOTIFY`. Nothing about
  a single-instance deployment changes.

### Rolling back

There is no downgrade path in the tooling. If you need to go back, redeploy the previous image with the
old `WATCHTOWER__DBPATH`, the `/data/watchtower.db` you kept, and the `/data/auth-keys` and
`/data/proxy-certs` directories the imports left untouched — which is the reason the clean-up above
comes last. Anything you changed after the import will not be in them.

**Delete every port route before you redeploy an image older than
[ADR-0033](decisions/0033-port-routes-and-internal-ca.md).** A port route is a route row with no domain,
which is a shape the older code has never heard of: its projection reads `Domain` off every row and hands
the empty result on as a site like any other. Under the built-in proxy that is a route the old build
cannot serve; under **Caddy** it is worse, because the site is rendered into the generated Caddyfile as a
block with no address, and a Caddyfile Caddy refuses is refused whole — no route change reaches the proxy
after that, and a Caddy container started fresh on that file does not come up. One route that was never
Caddy's to serve takes every domain that was working down with it. (Since the ADR-0033 addendum a Caddy or Cloudflare deployment can be
*serving* port routes rather than merely holding unserved ones, so this now applies to those deployments
in earnest.) Delete them from the Routes page first, then roll back.

## Port routes work with every provider (ADR-0033 addendum)

Port routes — a stack service on a dedicated TLS port with a certificate from Watchtower's own CA — used
to be the built-in provider's alone; under Caddy or Cloudflare such a route was marked `Error` as
unsupported. It never should have been gated that way: the listener is on Watchtower's own container and
has nothing to do with which backend terminates your public domains. From this image on, a port route is
served whenever the reverse proxy is enabled, under all three providers. See
[docs/reverse-proxy/README.md → Port routes](reverse-proxy/README.md#port-routes-https-on-a-lan-with-any-provider).

Two things to know before upgrading.

**Watchtower joins the ingress network of every stack it port-routes.** That has always been true under
the built-in provider; a Caddy or Cloudflare deployment now gets it too, for exactly the stacks that have
a port route and no others. Nothing changes for a deployment with no port routes.

**Three settings are renamed.** They carried `Yarp` in their names, which is precisely the conflation
being removed:

| Before | Now |
| --- | --- |
| `Watchtower:Proxy:Yarp:LanNames` (`WATCHTOWER__PROXY__YARP__LANNAMES`) | `Watchtower:Proxy:PortRoutes:LanNames` (`WATCHTOWER__PROXY__PORTROUTES__LANNAMES`) |
| `Watchtower:Proxy:Yarp:PortRoutePorts` (internal) | `Watchtower:Proxy:PortRoutes:Ports` |
| `Watchtower:Proxy:Yarp:ManagedHostPorts` (internal) | `Watchtower:Proxy:PortRoutes:ManagedHostPorts` |

**A value you saved in the UI is carried across for you**, once, on the first start after the upgrade —
copied to the new name and logged, with a row in the audit trail under `proxy` / `config.migrate`. The
old rows are left in place so a rollback still finds them, and a value already stored under the new name
is never overwritten.

**A value you pinned with an environment variable is not, and cannot be.** Environment values never enter
the settings store, so there is nothing for the copy to read — `WATCHTOWER__PROXY__YARP__LANNAMES` simply
stops having any effect. Watchtower says so on every start until you act on it:

```
warn: WATCHTOWER__PROXY__YARP__LANNAMES is set but no longer has any effect: the setting is now
      Watchtower:Proxy:PortRoutes:LanNames (WATCHTOWER__PROXY__PORTROUTES__LANNAMES). Environment values
      are invisible to the settings store, so nothing copied it across — set the new variable (or remove
      the old one and use Settings → Reverse proxy).
```

Rename the variable in your compose file (or drop it and set the LAN names under **Settings → Reverse
proxy → LAN port routes**). Until you do, the internal CA has no names to issue for and every port route
reports `Error`.

## New routes are protected by default (ADR-0035, ADR-0036)

**Read this before upgrading if you use the Cloudflare provider.** One change in this release alters the
behaviour of routes you already have, and it is the kind that locks people out rather than the kind that
lets them in.

**A protected route with no allow source is now denied at the edge.** Until now, a route stored as
`Authenticated` or `Restricted` whose allow-list came out empty — no allowed emails, no email domains,
no Access group ids, no reusable policy ids, or grants that resolve to no address Cloudflare can match —
was *skipped* by the reconcile: no Access application was published and any existing one was left alone.
The Routes page said the route was protected; the edge served it to everyone. From this image on, the
reconcile publishes an explicit **deny-all** Access application for such a route and sets the row to
`Error`. Nobody reaches it until you act.

That happens on the **first reconcile after the upgrade**, which is at startup. There is no migration
that softens it, deliberately: flipping those routes to Public would silently confirm the exposure, and
flipping them anywhere else would be Watchtower deciding your access policy for you
([ADR-0035](decisions/0035-new-routes-are-protected-by-default.md)).

**Before you upgrade**, go through **Routes** and, for every route showing *Authenticated* or
*Restricted* under the Cloudflare provider, either:

- configure an allow source under **Settings → Reverse proxy** — allowed emails, email domains, an
  Access group id, or a reusable Access policy id — which is almost certainly what you meant the route
  to have; or
- set the route **Public** if it was in fact meant to be open. It is open today; this makes the row say
  so.

**Routes already stored as Public are untouched**, under every provider. Nothing re-evaluates the access
mode of an existing route.

**New routes created from now on are Authenticated by default**, under every provider, and creating a
protected route is refused while Cloudflare has no allow source configured. The default is a setting —
**Settings → Reverse proxy → Default access for new routes** (`authenticated` or `public`,
env-pinnable as `WATCHTOWER__PROXY__DEFAULTACCESSMODE`) — so a deployment that genuinely wants open
routes sets it once. Watchtower's own routes and LAN port routes stay Public and are unaffected.

**Add `Zone: Read` to your Cloudflare API token.** It is not required — an install with a zone id set
keeps working exactly as it does today — but with it Watchtower discovers the zones your token can see,
the zone id becomes optional, and routes can live under more than one domain in the same account
([ADR-0036](decisions/0036-routes-live-under-primary-domains.md)). See
[docs/reverse-proxy/cloudflare.md → Zone discovery](reverse-proxy/cloudflare.md#zone-discovery).
