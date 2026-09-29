# ADR-0035: New domain routes are protected by default

- Status: Accepted
- Date: 2026-09-03
- Related: [ADR-0015](0015-proxy-provider-abstraction.md) (the provider seam, and the phase-3 rule about
  empty allow lists that decision 4 reverses),
  [ADR-0023](0023-login-hosts-are-watchtower-self-routes.md) (Watchtower's own routes, which stay Public
  and are held there by a check constraint),
  [ADR-0033](0033-port-routes-and-internal-ca.md) (port routes, likewise),
  [docs/central-auth/design.md](../central-auth/design.md) §7 (`proxy.setAccess` — the Admin gate this
  reuses rather than inventing a second one),
  [docs/reverse-proxy/cloudflare.md](../reverse-proxy/cloudflare.md) (the operator guide).

## Context

`Route.AccessMode` defaults to `Public` and `proxy.createRoute` has never accepted anything else. So the
sequence an operator actually performs — create the route, deploy, open it in a browser to check it works
— publishes the service to everyone first and offers a second, separate call (`proxy.setAccess`) to gate
it afterwards. The gap between the two steps lasts as long as it takes to remember there is a second one.

Under the built-in and Caddy providers that gap is at least honest: the route is Public, the page says
Public, and an unauthenticated request is served because that is what the row asks for. Under Cloudflare
it is not, because the projection only writes an Access application for a **non**-Public route. "No
application" and "an application that admits everyone" are the same thing at the edge and neither is
written down anywhere, so nothing in the Cloudflare dashboard records that a decision was taken.

The sharper half of issue #82 is the other end of the same asymmetry. A route that *is* `Authenticated`
or `Restricted`, but whose allow list comes out empty — no allowed emails, no email domains, no Access
group id, no reusable policy id, or grants that resolve to no address Cloudflare can match — was
**skipped** with a warning: no app created, any existing app left untouched. That was a deliberate rule,
written down as "a silent total lockout is the worse failure", and it is the wrong way round. A
locked-out application announces itself to the first person who tries to use it, in a message that names
what locked them out. An ungated one announces nothing to anybody, and the operator reading
`Authenticated` on the Routes page believes the opposite of what is true. The failure #82 reported was
the second kind, and the rule that produced it was the one meant to prevent the first.

There is a third thing the default cannot be flipped without. `Route.BypassPaths` already exists — the
in-process allow list that keeps a webhook or an OAuth callback anonymous on an otherwise protected route
— and it is enforced by `RouteAccessPolicy`, which does not run under Cloudflare at all. Protecting new
routes by default while leaving bypass paths unprojected would make "protected" mean "the webhooks are
broken" for every Cloudflare deployment.

## Decision

### 1. A new domain route is Authenticated, under every provider

An unspecified access mode on `proxy.createRoute` resolves from the operator setting
`Watchtower:Proxy:DefaultAccessMode`: `authenticated` (the default) or `public`, env-pinnable like every
other setting (ADR-0014) and editable under Settings → Reverse proxy as *Default access for new routes*.
Anything else in that field — a typo, a value from a newer version, `restricted` — resolves to
`Authenticated` rather than failing the call. This value is read on the write path of every route
creation, and the reading that costs an operator one click is better than the reading that costs them an
open service.

It applies to every provider rather than only to the one where the old behaviour was invisible. The
mechanism differs (forward-auth under `yarp` and `caddy`, an Access application under `cloudflare`) but
the question an operator is answering does not, and a default that changed with the provider would be a
fourth thing to remember when switching one.

The **entity** default stays `Public`, and the divergence from the handler default is deliberate rather
than an oversight: `Public` is what the two check constraints in decision 3 require of the rows nobody
creates through this form, so it is the right value for a row constructed without a handler. What a human
asking for a domain route gets is the handler's business.

### 2. The call can say, and saying so is an Admin action

`proxy.createRoute` accepts `accessMode` and `bypassPaths`, so the mode is chosen in the form that
creates the route rather than in a second visit to a second dialog. Supplying **either** puts the call
behind the same Admin role gate as `proxy.setAccess` (docs/central-auth/design.md §7); a caller without
it gets `Forbidden` and no row is written.

The gate fires on *any* explicit value, not on a value that differs from the default. "Non-default" is a
comparison against something that can move between the moment a form was rendered and the moment it is
submitted — another admin edits the setting, or a restart brings an environment pin with it — so two
callers sending byte-identical requests could be gated differently depending on when they loaded the
page. Bypass paths settle it on their own in any case: naming a path that skips the access check is an
access-control statement whatever the route's mode ends up being.

**`Restricted` cannot be chosen at creation.** A create carries no grants, so a Restricted route admits
nobody — it is decision 4's deny-all arrived at by accident, in the one place where an operator is least
expecting it. The path is to create the route `Authenticated` and then call `proxy.setAccess` with the
grants, which is the call that has somewhere to put them.

A caller who supplies neither field is not gated at all and creates a route at whatever the setting says.
Route creation is not an admin-only operation and this does not make it one.

### 3. Watchtower's own routes and port routes stay Public, structurally

Both are already held there by check constraints rather than by handler etiquette —
`ck_routes_target` requires `access_mode = 'Public'` for a `Watchtower` target, and `ck_routes_binding`
requires it for a `Port` binding — and both constraints exist for reasons this ADR does not disturb. A
login host that required a session to reach would be a login page nobody can sign in through
(ADR-0023). A port route has no hostname a forward-auth redirect could return a visitor to, which is
exactly what ADR-0033 decision 1 made structural so the dispatcher could skip the access check knowing
the database would never store the case.

So `createRoute` **refuses** explicit access fields on those two shapes rather than accepting and
ignoring them, in keeping with the house rule the two handlers already follow for every other field that
does not apply to them. The default in decision 1 does not reach them either.

### 4. Under Cloudflare, an empty allow list is refused at creation and denied at the edge

This reverses ADR-0015's phase-3 rule, in two halves that cover different moments.

**At creation.** With the proxy enabled, the Cloudflare provider selected, and no allow source configured
anywhere — no allowed emails, no email domains, no Access group ids, no reusable policy ids — a route
that would not be Public is refused, naming Settings → Reverse proxy and the Public alternative. This is
the half that reaches a human while they are still looking at the thing they are creating.

**At reconcile.** An `Authenticated` or `Restricted` route whose allow list resolves to empty is no
longer skipped. The provider publishes an explicit deny-all Access application — a `deny` decision over
Everyone, under the same `watchtower: ` name prefix as every other app it owns — and sets the route to
`Error` with the reason. This half covers what a create-time check cannot: an allow source removed later,
grants deleted, a group emptied, a route imported from the dashboard, a token that stops resolving
identities.

Deny-all is the correct failure because of the asymmetry in the Context. A lockout is loud, immediate,
attributable and reversible by one settings change. An unprotected route is silent, and it stays silent
for exactly as long as nobody malicious finds it. `Error` on the row is the second half of the same
argument: the operator finds out from Watchtower rather than from a colleague who cannot log in.

### 5. Bypass paths are projected as a second Access application

For every non-Public route with bypass paths, the provider emits a second application named
`watchtower: {host} (public paths)` carrying `{host}{path}` for each configured path, with a single
`bypass` policy for Everyone. Cloudflare resolves overlapping applications most-specific-first, so the
bypass app wins on the paths it names and the route's own app keeps everything else.

It is emitted for a deny-all route too. The lockout in decision 4 is about people who would otherwise
sign in; a webhook delivery has no identity to present and never did, and breaking an integration is not
part of what that failure is trying to say.

**The two matchers are not the same matcher, and the edge's is the wider one.** Cloudflare matches by
path segment, so an application on `host/hooks` covers `/hooks` and everything under it and stops at a
segment boundary. `RouteAccessPolicy.IsExemptPath` matches a raw prefix, which is wider at the boundary
(`/hooks` also matches `/hooksecret`) and considerably narrower everywhere else: it refuses the exemption
outright for any path carrying a percent-encoded byte or a `.`/`..` segment, because it decides on the
raw forwarded path while the upstream acts on whatever it makes of that path after decoding. The edge
applies no such guard. So a request that Watchtower would have pushed through the full access check can
be bypassed at the edge, and bypass prefixes should be narrow, specific and pointed at endpoints that
authenticate their own callers — which is what a webhook signature is for.

### 6. The route list shows every route's access mode

`RouteDto` carries `accessMode`, and the Routes page renders it per route in the table and on the cards.
A default nobody can see is a belief rather than a fact, and the whole argument above rests on an
operator being able to look at a list and read which of their services are open.

## Consequences

- **On the first reconcile after this upgrade, an existing Cloudflare route that is `Authenticated` or
  `Restricted` with no allow source becomes deny-all and goes to `Error`.** Nobody reaches it until an
  allow source is configured or the route is set Public deliberately. That is the intended effect and it
  is not softened by a migration: flipping those rows to Public on upgrade would silently confirm the
  exposure this ADR exists to end, on the deployments most affected by it, and flipping them to anything
  else changes an access decision on an operator's behalf. `docs/upgrading.md` says what to do before
  upgrading; the deny-all is the reason it is worth reading first.
- **Existing routes are otherwise untouched.** Nothing re-evaluates the mode of a row that already
  exists. A Public route stays Public, gets no Access application, and is now that way because somebody
  chose it.
- **`Access: Apps and Policies: Edit` is effectively required on the Cloudflare token**, where before it
  was needed only by operators who used protection. A token without it now fails to publish the app for a
  route that defaults to protected, so `docs/reverse-proxy/cloudflare.md` lists it as part of the setup
  rather than as a conditional extra.
- **A protected route with bypass paths owns two Access applications**, which the reconcile's sweep has
  to account for: it keys on the desired applications' domains **and** their names, or the second app is
  created and deleted on alternate passes. Flipping a route to Public deletes both; removing one bypass
  path rewrites the bypass app; removing the last one deletes it.
- **`TenantProvisioningService` and `AdoptStack` still create Public routes.** Both mint routes without
  going through this handler, and neither is changed here. What access a tenant's subdomain should have
  is a question about the tenancy model rather than about route creation — it depends on whether tenants
  share a realm, whether the template's users are Watchtower accounts at all, and who is allowed to
  answer for a tenant — and that model is [ADR-0026](0026-products-are-the-deployable-unit.md), which is
  still Proposed. Deciding it as a side effect of a default would fix it in the wrong ADR. Recorded as a
  follow-up, and named in the code at both sites.
- **The setting is one value for the instance.** There is no per-realm, per-stack or per-tenant default,
  and the two callers in the previous point are the reason one would eventually be wanted. One global
  value is the version that can be explained in a sentence on the Settings page.
- **A non-admin can still create routes**, and gets the default without a choice about it. The Routes
  page hides the access controls for them rather than showing a disabled field, since the interesting
  case is a deployment where route creation is delegated and the access policy is not.
