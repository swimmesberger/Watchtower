# ADR-0036: Routes live under primary domains, and Cloudflare zones are discovered

- Status: Accepted
- Date: 2026-09-03
- Related: [ADR-0015](0015-proxy-provider-abstraction.md) (the Cloudflare provider, and the single-zone
  assumption decision 3 removes),
  [ADR-0033](0033-port-routes-and-internal-ca.md) (port routes, which have no domain and are their own
  group in the list),
  [ADR-0035](0035-new-routes-are-protected-by-default.md) (the other half of this change to the
  new-route form),
  [docs/reverse-proxy/cloudflare.md](../reverse-proxy/cloudflare.md) and
  [docs/reverse-proxy/README.md](../reverse-proxy/README.md) (the operator guides).

## Context

An operator with routes usually has one base domain, or a few. Every route they create is a subdomain of
one of them, and Watchtower asks them to type the whole hostname every time — which is a chance to
misspell the part that never changes, on the field whose value is also the DNS record, the certificate
subject and the route's identity.

The Cloudflare provider makes the same assumption in the other direction and enforces it. One zone id,
pasted by hand from the dashboard, and ADR-0015 recorded the consequence as a limitation: "all route
domains must live under the configured `ZoneId`; a domain outside it fails its DNS upsert (logged,
best-effort) while the rest proceed". So an account with two domains in it — the ordinary shape for
anyone hosting more than one thing — can serve only one of them through Watchtower, and the second one
fails in a log line.

Both are the same missing fact: nothing in Watchtower knows what an operator's domains are. The
information exists in two places already. The operator can say so, and under Cloudflare the API token
Watchtower is holding can be asked, because listing zones is what a Cloudflare token is for.

The third symptom is the route list, which is flat and ordered by nothing an operator thinks in. Twenty
routes across two domains and a handful of LAN ports read as twenty rows.

## Decision

### 1. A primary domain is derived, and is never an entity

A **primary domain** is a base domain that routes are published under. It has no table, no row, no id and
no foreign key on `Route`. It is a string an operator configured or a zone name Cloudflare reported, and
every question anyone asks about it is answered by a longest-suffix match against the hostname the route
row already stores.

Persisting it would buy nothing and cost a lifecycle. A per-route foreign key needs a migration, a
backfill that guesses, a cascade rule for the day an operator stops publishing under a domain, and an
answer for a route whose hostname and whose recorded parent disagree — a state that can only exist
because the parent was written down. Derived, that state is unreachable: change the setting and every
route re-groups on the next render, because the grouping was never anything but a function of the
hostname and the list.

### 2. Two sources, merged on the server, one answer

`proxy.listPrimaryDomains` returns one list, and the client does not know how it was assembled.

The first source is the operator's, under every provider: `Watchtower:Proxy:PrimaryDomains`, comma- or
newline-separated, each entry held to the same hostname rules as a route's own domain
(`DesiredHosts.TryNormalize`), env-pinnable like every other setting (ADR-0014). The second is
Cloudflare's, under the Cloudflare provider only: the zones the configured API token can see, which needs
`Zone:Read` on the token. On a duplicate the configured entry wins, so an operator who typed a domain
that is also a zone gets one entry, and the answer does not change shape when a token permission is
granted later.

Merging on the server rather than in the browser keeps one set of rules in one place. The frontend would
otherwise need to know which providers have a second source, how the two collide, and how to behave when
one of them fails — all of which is decision 4, and none of which is a rendering concern.

### 3. The Cloudflare provider becomes multi-zone, and the zone id becomes optional

Each route's CNAME is written into **the zone whose name is the longest suffix of that route's domain**,
falling back to the configured zone id when no discovered zone covers it. Longest suffix rather than
first match, because an account holding both `example.com` and `apps.example.com` has two zones that
could take `web.apps.example.com` and exactly one of them is right.

The zone id therefore becomes optional. Leaving it blank is accepted at save time only if the token can
list at least one zone, so a deployment cannot save a configuration in which no route could ever get a
DNS record. When a zone id **is** set, the save path does not consult the zone listing at all — the
credentials being saved may be the ones that were broken, and validating them against a call the old
configuration never made would refuse a fix.

**An install with a zone id and no `Zone:Read` keeps working exactly as it did.** Discovery fails, the
catalog falls back to a single entry for the configured id, and the zone's *name* is learned from the
`zone_name` field of any DNS record in it — which the provider can read with the `DNS: Edit` permission
it already has. So the single configured zone participates in longest-suffix matching like a discovered
one, and an operator who never grants the new permission never notices this ADR on the DNS path.

A domain no zone covers, with no configured zone id to fall back to, sets its route to `Error` naming
both remedies — grant `Zone:Read` on that zone, or set the zone id under Settings → Reverse proxy. That
replaces ADR-0015's best-effort log line: a route whose DNS record was never written is not a route in
any useful sense, and the row is where an operator looks.

### 4. Discovery is fail-open and cached

The zone listing is cached for about five minutes, keyed by the credentials it was made with (account id,
zone id, API token), so a token change is picked up without a restart and an answer fetched with one set
of credentials is never served for another.

On an API error the catalog returns fewer domains — the configured zone id alone, or nothing — and never
an error. Every consumer of this list is a convenience over a hostname the operator can always type in
full, and none of them may be able to fail: `proxy.listPrimaryDomains` answering with an empty list
renders a form, while `proxy.listPrimaryDomains` throwing renders a broken page on the way to creating a
route that has nothing to do with Cloudflare. The same argument covers a bad *stored* value in the
configured list: it yields fewer domains rather than an error, because a setting that can only be fixed
on a page it prevents from loading is a trap. Cancellation still propagates; a caller that went away is
not a fail-open case.

### 5. The composed field is an affordance; the server's gate is unchanged

The new-route form composes a hostname from `subdomain` and a chosen primary domain — an empty subdomain
means the apex — and always keeps a custom-hostname escape hatch, in both directions, carrying whatever
was typed across the switch. A deployment with no primary domains configured and no discoverable zones
sees the field exactly as it is today.

`DesiredHosts.TryNormalize` in `proxy.createRoute` remains the **only** gate on what a route's domain may
be. The composed control makes the common case shorter to type; it does not make the result trusted, and
a hostname assembled from a select is validated on arrival like one that was pasted in. Nothing about
the composition is stored — what reaches the database is the hostname.

### 6. The list groups by primary domain, then by what is left

The Routes page renders a section per primary domain (longest-suffix match again, apex first, then by
subdomain), then **Other domains** for hostnames no primary domain covers, then **LAN ports** for port
routes, which have no domain at all (ADR-0033). Empty groups are omitted, and a deployment with no
primary domains, or one where everything lands in a single group, gets today's single flat list rather
than a section header with everything under it.

`Route.Kind` does gain a meaning here — a route created against a hostname no primary domain covers is
recorded as `custom` rather than `managed` — but **grouping is by suffix, not by kind**. Every route that
exists today is `Managed`, so grouping by kind would file every pre-existing route under the wrong
heading forever, and a backfill would be a migration guessing at a value derived from a setting the
operator can change five minutes later. The suffix match needs no backfill because it reads the hostname.

## Consequences

- **`Zone: Read` joins the recommended Cloudflare token permissions**, and the setup instructions say
  what it buys: discovery, an optional zone id, and routes across more than one domain. A token without
  it keeps working as long as a zone id is set, which is every existing install.
- **Beyond 50 zones, set the zone id explicitly.** The listing asks for `per_page=50` and does not
  paginate. An account with more zones than that gets an arbitrary 50 of them, so a route under one of
  the others would fall back to the configured id — which is why the id remains supported rather than
  deprecated. Pagination is a small change and is not this ADR.
- **Nothing changes for a deployment that configures no primary domains.** No section headers, no
  composed field, one more optional setting on the Settings page. Under `yarp` and `caddy` the feature is
  entirely the configured list, since there is no second source to merge.
- **Grouping is presentational, so there is no migration.** The `routes` table is unchanged by this ADR;
  `Route.Kind` is an existing column that starts being written with a value other than its default.
- **A configured primary domain is checked for syntax and nothing else.** `DesiredHosts.TryNormalize`
  rejects what cannot be a hostname; it cannot know whether the operator owns the domain, whether it
  resolves, or whether any provider can serve it. A typo becomes an option in a select and, eventually, a
  route that never comes up — the same failure as typing that hostname by hand, arrived at once instead
  of repeatedly.
- **Comparisons stay ASCII.** `DesiredHosts` rejects non-ASCII hostnames (the image runs with invariant
  globalization) and Cloudflare reports zone names punycoded, so suffix matching never has to reason
  about two spellings of one name.
- **The zone a route's DNS record lives in can change without the route changing**, if an operator adds a
  more specific zone to the account. The next reconcile writes the record into the new zone and the old
  record is left where it is, since nothing tells Watchtower the domain moved. Removing the stale record
  is a dashboard job, and it is the same class of leftover as the DNS records the provider already keeps
  when the proxy is disabled.
