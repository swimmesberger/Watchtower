# ADR-0011: A stack may ask which sibling tenants the proven visiting user can reach

- Status: Accepted
- Date: 2026-08-10
- Related: [ADR-0008](0008-public-app-api.md) (the invariant this amends),
  [ADR-0009](0009-public-management-api.md) (the grant model this sits beside), and the
  [central-auth design](../central-auth/design.md) (the assertion this trusts).

## Context

With central authorization enabled, Watchtower is the identity provider for the apps it proxies: it
owns the users, decides per route who may enter, and forwards a short-lived ES256 assertion
(`X-Watchtower-Jwt`, `aud` = the app's domain) to the upstream on every allowed request. A product
deployed as a template with one tenant stack per customer therefore has a proven user identity in
hand on every request it serves — and no way to act on it beyond the current tenant.

The missing feature is mundane and universally expected: a **tenant switcher**. A consultant who
works for three of the vendor's customers, or an employee whose company runs a staging tenant
alongside production, lands on one tenant and has no idea the others exist, let alone a menu to
switch to them. The vendor's own management UI wants the same list, filtered to the person looking
at it, so a support agent sees the customers they may actually open.

Nothing on the existing surfaces answers it. The App API ([ADR-0008](0008-public-app-api.md)) is
self-only by construction — *a stack can only ever see itself*. The Management API
([ADR-0009](0009-public-management-api.md)) can list a template's tenants, but its authority is an
operator-created grant, and ADR-0009 explicitly **forbids granting a template to one of its own
tenants**: that is the one thing that would let a customer enumerate its neighbours. The product's
tenant stack is exactly that forbidden caller, and it is also the only place the switcher can live —
the switcher belongs in the product's own UI, on the customer's own domain, not in Watchtower's.

The information itself is not a secret from the *user*. Somebody who may enter `customer4` and
`customer7` can discover both by visiting them; the access decision is unchanged either way. What is
missing is a way for the product to ask **on that user's behalf** without the question degenerating
into an enumeration oracle — an endpoint that answers "may user 42 reach these tenants?" for an
arbitrary `42` would hand any token-holding stack a probe against every user on the host.

## Decision

**Add a user-scoped tenant-discovery read to both public surfaces —
`GET /api/app/tenants/accessible` and `GET /api/mgmt/templates/{templateId}/tenants/accessible` —
answered only for a user the caller can *prove* is currently visiting it, by forwarding that user's
Watchtower-signed identity JWT.** The listing segment is carved out of the tenant-slug namespace to
make that second path unambiguous: `accessible` becomes a reserved slug, so no tenant can ever be
provisioned that `…/tenants/{slug}` would resolve on top of it.

This is a deliberate, scoped amendment of ADR-0008's invariant. A stack still cannot see its
siblings on its own account; it may learn **what the authenticated user standing in front of it may
reach** among them.

- **The proof is the assertion; no endpoint ever accepts a user id.** The caller forwards the
  `X-Watchtower-Jwt` verbatim from the request it is currently serving, alongside its own
  `Authorization: Bearer wtapp_…`. There is no `?userId=` parameter, no email lookup, no subject in
  the body — the only way to name a user is to hold a live, valid assertion about them.
- **The audience binding is the anti-enumeration control.** Validation is the existing ES256 chain
  (algorithm pinned, signature, issuer, expiry) **plus** `aud` matching one of the *calling stack's
  own route domains*. The assertion Watchtower mints for `customer4.example.com` is accepted only by
  the stack serving `customer4.example.com`. So an app can only ask about users **actively visiting
  it** — an assertion captured elsewhere, by a curious tenant or a compromised one, carries the
  wrong audience and is refused. On the Management API the same rule applies against the
  *management* stack's route domains.
- **The user row is rechecked, not taken from the token.** After signature and audience pass, the
  user is reloaded and a missing or `Disabled` account is refused. An assertion minted moments
  before an operator disabled the account does not keep working for the remainder of its five-minute
  life.
- **Access is evaluated with the same route policy the proxy enforces**, in one pass over the
  template's tenants rather than per-tenant probing: `Public` and `Authenticated` routes are
  reachable, `Restricted` needs a grant for that user, and an unrecognised mode fails closed.
- **Every assertion failure is one generic `401` with one message.** Missing, malformed, expired,
  wrong audience, wrong signature, disabled user — indistinguishable to the caller. A per-check
  message would turn the endpoint back into the oracle the audience binding exists to prevent.
- **The response is the minimum a switcher needs**: slug, domain, and (App API only) which entry is
  the caller itself, sorted by slug. No stack ids, no deploy status, no timestamps — unlike every
  other response on these surfaces, this one is rendered to end users.
- **Each surface keeps its own authorization semantics ahead of the assertion.** On the App API the
  caller must be a tenant of a template (otherwise `404` — it is telling the caller about itself).
  On the Management API the ADR-0009 grant chain runs **first**, so an ungranted template is still a
  uniform `404` and never reveals that an assertion would have been checked.
- **With `Auth:Enabled` off, both endpoints are `404`.** No assertion can exist, so there is no
  honest answer to give; this matches how the verify and UserInfo endpoints already disappear.

## Consequences

- **The switcher is only meaningful where central auth fronts the tenant routes.** A product whose
  routes are `Public` — it does its own login — never receives an assertion, so it gets `404` with
  auth off and `401` with auth on, and must keep its own user-to-tenant mapping product-side, as it
  does today. Adopting the endpoint is therefore a reason to move a product's routes onto
  `Authenticated`/`Restricted`, not something that works everywhere by default.
- **ADR-0008's invariant now has exactly one exception, and it must be quoted with it.** "A stack
  can only ever see itself" remains the rule for everything the stack asks on its own behalf; the
  one carve-out is this user-scoped read. Reviewers who rely on the one-sentence contract need the
  amended sentence, so the public API docs state it inline rather than only here.
- **A tenant learns that siblings exist — the ones its own visitor can reach.** Accepted: the user
  could establish the same list by visiting those domains, so the endpoint discloses to the tenant
  only what the person already sitting in its UI could hand it anyway. What it does not do is reveal
  tenants the user *cannot* reach, or anything about users who are not there. The residual exposure
  is a tenant harvesting sibling slugs from its own visitors over time; that is bounded by who
  visits it, and it is why the response carries no operational data about those siblings.
- **Debuggability is deliberately poor.** An integrator whose calls `401` cannot tell a clock-skewed
  expiry from a misconfigured audience from a disabled account. The operator-side audit trail and
  logs are the diagnostic path; the API surface stays silent on purpose.
- **Grant changes take effect immediately, token changes do not.** Access is evaluated per request
  against the live grant rows, so revoking a user's access to a sibling removes it from the next
  response. The five-minute assertion window is closed for account disablement by the user recheck;
  nothing else about the user is read from the token.
- **No caching, no pagination.** A handful of indexed lookups per call, none of them per-tenant, over
  the tenant count of a single template. If a template ever grows large enough for this to matter,
  the shape can take a page token without changing the security model.

### Rejected alternatives

- **Accept a bare user id (or email) parameter.** The obvious API, and the reason this ADR exists.
  Any stack holding a valid `wtapp_` token could then ask "which of these tenants may user 42
  reach?" for every id on the host — an enumeration oracle over the user table *and* over sibling
  access policy, driven by a credential that is deliberately low-value and lives in a container's
  environment. Requiring an unforgeable assertion makes the answer's scope structural instead of
  policed.
- **Reuse the existing no-audience assertion validation** used by verify and UserInfo, where any
  Watchtower-signed JWT is accepted. It would make one line of code do both jobs, and it would
  destroy the property that matters: a tenant could replay an assertion it received on *its* domain
  against another tenant's endpoint, or a hostile app could farm assertions from visitors and use
  them to map the estate. The audience is what binds the question to "a user who is here, now". The
  no-audience overload keeps its current behavior for the flows that legitimately have no single
  audience.
- **Put the accessible-tenant list in the JWT claims.** Zero new endpoints, zero new auth — but the
  assertion is minted per request for one route and lives five minutes, so the list would be stale
  the moment a grant changed, and every proxied request to every protected app would carry a claim
  almost none of them read. It also leaks the list to apps that never asked, through a header that
  crosses into upstreams the user did not choose.
- **Expose it only on the Management API.** Safe and already grant-governed, but it puts the
  switcher in the wrong place: the vendor's management UI is not where a customer's user switches
  tenants. The product's own UI, on the tenant's own domain, is — and ADR-0009 rightly refuses to
  grant that stack management of its own template. The management variant ships too, for the
  support-agent view, but it cannot be the only one.
