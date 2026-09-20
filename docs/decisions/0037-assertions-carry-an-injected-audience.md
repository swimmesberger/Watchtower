# ADR-0037: Deploys inject the audience an app must check, not just the JWKS to check it against

- Status: Accepted
- Date: 2026-09-20
- Related: [ADR-0008](0008-public-app-api.md) (the reserved variables this adds to),
  [ADR-0012](0012-direct-env-injection.md) (how they reach a container),
  [ADR-0015](0015-proxy-provider-abstraction.md) (the provider seam, and the Cloudflare Access
  applications this reads the audience off),
  [ADR-0035](0035-new-routes-are-protected-by-default.md) (which made a protected route the default,
  and gave a route with bypass paths a second Access application),
  [docs/central-auth/design.md](../central-auth/design.md) §2.3 (the assertion and its claims),
  [docs/public-app-api.md](../public-app-api.md) (the operator-facing table).

## Context

An app behind a protected route is handed a signed identity assertion — `Cf-Access-Jwt-Assertion`
under the Cloudflare provider, `X-Watchtower-Jwt` under integrated auth — and is expected to verify it
rather than trust a plaintext header. Watchtower already injects `WATCHTOWER_AUTH_JWKS_URL` so the app
can fetch the right public keys without hard-coding an issuer, which is what makes switching edges a
redeploy rather than an app change.

Verifying the signature is only half of verifying the assertion. A signature proves the token came
from that edge; it says nothing about **which application it was minted for**. Both edges answer that
in the `aud` claim, and both were already filling it in:

- `AuthTokenSigner.Mint` sets `Audience = routeDomain`, and the class remarks say why in as many
  words — checking `aud` "binds the assertion to *its own* domain — a token minted for another app is
  not accepted, so a hostile upstream cannot replay one it received".
- Cloudflare Access stamps the Access application's **Application Audience (AUD) tag**, a per-app
  identifier the edge assigns at creation and never changes.

Nothing told the app what to compare against. Under integrated auth it could guess — the audience is
its own hostname, which it may or may not know reliably from behind a proxy. Under Cloudflare it could
not guess at all: the AUD tag is an opaque value that exists only at the edge. `CloudflareAccessApp`
did not even deserialize the field, so Watchtower discarded the answer on every reconcile.

The practical consequence is the replay the signer's own documentation warns about. Every application
in one Cloudflare account is signed by one team key and published under one JWKS. An app that checks
the signature and skips `aud` therefore accepts an assertion minted for *any other application in
that account* — including one whose Access policy admits a much wider population, and including one
the attacker is themselves authorised for. The edge would not deliver such a token to this upstream,
so reaching the container by another path is a precondition; but "the network is the only thing
stopping this" is precisely the assumption a signed assertion exists to remove.

## Decision

### 1. A fifth reserved variable, `WATCHTOWER_AUTH_AUDIENCE`

Deploys inject the `aud` value(s) an assertion reaching this stack will carry, alongside the JWKS URL
and under the same rules: written into the generated compose override and into the temporary `.env`,
given to every service (it identifies, it does not authenticate), reserved so an operator variable
cannot shadow it, and omitted entirely when there is nothing to say.

An app's verification becomes the whole check, from two variables it never has to interpret:

```
verify(assertion, jwks = WATCHTOWER_AUTH_JWKS_URL, audience ∈ split(WATCHTOWER_AUTH_AUDIENCE, ","))
```

### 2. The name is edge-neutral, because the concept is

`CF_ACCESS_AUD` and `CF_ACCESS_TEAM_DOMAIN` are the names a Cloudflare-only deployment reaches for,
and they are the wrong ones here for the same reason `WATCHTOWER_AUTH_JWKS_URL` is not called
`CF_ACCESS_CERTS_URL`. Both edges mint assertions, both fill `aud`, and the entire point of resolving
these values at deploy time is that one image runs behind either. A Cloudflare-shaped name would be
wrong the moment an operator switched providers — which is the failure the JWKS variable was
introduced to prevent, reintroduced one variable later.

So the value is resolved per edge and the variable never says which one produced it:

- **Cloudflare provider active:** the Access applications' AUD tags.
- **Otherwise, with integrated auth enabled:** the routes' own hostnames, which is what
  `AuthTokenSigner` stamps.
- **Neither:** nothing is injected.

### 3. The AUD tag is stored on the route, written back by the reconcile

`routes.access_aud` is filled by `CloudflareTunnelProvider` each time it reconciles a route's Access
application, from the `aud` field of the create/update response, and cleared for every route that no
longer has an application of its own.

It is a cache of a remote fact, and deliberately not fetched at deploy time. A deploy that had to ask
Cloudflare for the audience would fail whenever the API was unreachable or the token had been rotated
— converting an unrelated outage into an inability to deploy anything, including the change that
fixes it. Reading a column cannot fail that way.

The writeback is best-effort, like the route status beside it: a bookkeeping failure is logged and
never fails a reconcile that already succeeded at the edge. It is logged at **warning** rather than
debug, because unlike a status this one outlives the pass — until the next reconcile, deploys keep
injecting the previously recorded value and a strict app refuses the assertions it is handed. That is
the fail-closed direction and it heals on the next reconcile, but it should not be silent while it
lasts. Only rows whose value actually changed are written, so a steady-state reconcile costs one
`SELECT` and no `UPDATE` — the same reasoning as skipping an unchanged tunnel configuration `PUT`.

Only the route's **own** application contributes. ADR-0035's bypass application admits everyone
without signing them in, so it mints no assertion at all; recording its tag would hand the upstream a
second acceptable audience that nothing can ever present.

### 4. A stack with several protected routes gets all of their audiences, comma-separated

A stack can be reached on more than one protected hostname, and each is a separate application at the
edge with an `aud` of its own. The variable therefore carries a list, ordered by hostname so that two
deploys which agree produce byte-identical overrides, and every JWT library accepts a set of valid
audiences.

This is not a weakening of the check. Each value names an application **of this stack**, so the
comparison still refuses every assertion minted for anything else behind the same edge, which is the
entire property being bought. What it does not do is distinguish between this stack's own hostnames:
an assertion minted for one is accepted on another. The edge will not deliver one that way — each
hostname's own policy is enforced before anything is forwarded — so the residual case is again an
attacker who already has another path to the container.

An app that wants the stricter check can still have it: the claim is in the token and the hostname is
in the request. But it is not what this variable is for, and making the variable single-valued would
have left a two-hostname stack silently unverifiable instead.

### 5. A protected route with no recorded audience contributes nothing

A route the provider has not reconciled yet — newly created, or one whose last reconcile failed — has
no AUD tag, and is skipped rather than contributing an empty string. If it is the stack's only
protected route the variable is not injected at all, so an app that requires it fails to start rather
than starting with a check that passes everything. Both are the fail-closed answer, and the route
reads `Pending` or `Error` on the Routes page while it lasts.

## Consequences

- **`routes` gains a nullable `access_aud` column** (`AddRouteAccessAud`). Nothing reads it except the
  audience resolver, and a row that never meets the Cloudflare provider keeps it null forever.
- **Apps that already verify `aud` against a hard-coded value keep working**, since the value they
  were given is the one now injected. Apps that do not verify it are not broken either — the variable
  is additional, and nothing rejects a container for ignoring it.
- **`stacks.getAppApi`/`setAppApi` now query the stack's routes** to build `injectedVarNames`, because
  the list depends on the stack and no longer only on the settings. The wire shape is unchanged.
- **The first reconcile after the upgrade fills the column for every protected Cloudflare route**, and
  the audience reaches a container only on that stack's next deploy. A stack deployed in the window
  between those two moments gets no audience variable, and one deployed after a provider switch gets
  the new edge's answer — the same "the next deploy re-injects it" contract the JWKS URL already has.
- **Every Cloudflare response type had the same latent problem, and was corrected alongside this.**
  The JSON source generator treats a record of `init` properties as having a parameterized constructor
  and assigns every one of them from the parsed arguments, so a field absent from the response arrives
  as null and the `= ""` initializers never run — making the non-null annotations promises the
  deserializer does not keep, and silencing the compiler at the use sites. `Aud` is declared `string?`
  because the audience is genuinely optional. The rest are now split explicitly: the ids that address
  a URL or a DNS target (`CloudflareTunnel.Id`, `CloudflareDnsRecord.Id`, `CloudflareZone.Id`/`.Name`,
  `CloudflareAccessApp.Id`, `CloudflareAccessPolicy.Id`) are `required`, so an absent one throws a
  `JsonException` naming the member rather than flowing on; everything only compared or logged is
  nullable. `CloudflareWireContractTests` pins both halves. The one defect this surfaced was in
  `StaleApps`, where `app.Name.StartsWith(...)` would have thrown on a nameless application and taken
  the whole Access reconcile with it — it now reads a missing name as "not one of ours", which is the
  side of that question a deletion sweep has to fail towards.
- **Watchtower still does not inject the team domain.** `Proxy:Cloudflare:TeamDomain` remains an
  operator setting that the JWKS URL is derived from, and no `CF_ACCESS_TEAM_DOMAIN` is written: an
  app holding the JWKS URL and the audience has everything it needs to verify, and the team name is an
  edge detail it should not learn to depend on.
