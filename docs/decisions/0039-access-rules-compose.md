# ADR-0039: Access rules are named, composable clause lists — provider-neutral, with portability declared

- Status: Accepted — decision 5 amended 2026-09-23 (routes are created with their whole access policy)
- Date: 2026-09-21
- Related: [ADR-0015](0015-proxy-provider-abstraction.md) (the provider seam this composes across),
  [ADR-0035](0035-new-routes-are-protected-by-default.md) (the lockout rule this makes route-aware),
  [ADR-0040](0040-the-edge-projection-is-authoritative.md) (the projection half — what the edge is
  allowed to keep, and the realm invariant it must apply),
  [docs/central-auth/design.md](../central-auth/design.md) §3 (the subject model), §13.5 (the realm
  invariant, written once in `RouteAccessPolicy`),
  [docs/reverse-proxy/cloudflare.md](../reverse-proxy/cloudflare.md) (the operator guide).

## Context

`Route.AccessMode` — `Public | Authenticated | Restricted` — is the entire access vocabulary, and only
one of its three values carries per-route detail:

- `Public`: no access control.
- `Authenticated`: admits **four instance-wide settings** — `AccessAllowedEmails`,
  `AccessAllowedEmailDomains`, `AccessGroupIds`, `AccessReusablePolicyIds`. Every `Authenticated` route in
  the instance therefore gets a byte-identical allow list.
- `Restricted`: admits exactly the route's `RouteAccessGrant` rows, and **deliberately drops** the
  instance-wide sources so that "only these subjects" cannot be widened behind the operator's back.

So the only per-route lever *replaces* rather than *adds*, and the common request cannot be expressed at
all: *"this hostname admits the people every hostname admits, plus these extra people."* An operator whose
family is already an allow list and whose friends are a second one has two working options, both bad — put
the friends policy in the instance-wide setting and admit friends to every protected hostname, or
re-enumerate the family inline on the one route and maintain that list twice.

The Cloudflare wire already composes. `CloudflareAccessAppRequest.Policies` is `string[]` and *"a
non-empty array replaces them"*, so attaching two reusable policies to one application is implemented and
tested. What is missing is not a projection capability — it is a **per-route selection** of which policies
to attach. That is a modelling gap on Watchtower's side, and building it as a Cloudflare feature would put
provider-specific identifiers on the route table for a decision every provider has to make.

There is a second, sharper reason not to model this per route. An operator who is Cloudflare-only today and
turns Watchtower's own identity plane on later would have to migrate every route holding a Cloudflare
policy UUID, with no name to search for. The same operator with a *named* rule edits one clause.

Two existing behaviours show what happens when the model and the enforcement points drift apart, and both
are silent:

- `Restricted` discards `AccessReusablePolicyIds` with no warning anywhere.
- The realm invariant — *"a protected route is only ever reachable by an account of its own realm, whatever
  its grants say"* — is enforced in `RouteAccessPolicy` and **not** in the Cloudflare projection, so the
  same grant table admits differently at the two enforcement points (ADR-0040 decision 3).

A vocabulary shared by two enforcement points is worth little without an honest account of which point can
honour which part of it.

## Decision

### 1. An access rule is a named, ordered list of clauses; a route composes an ordered list of rules

Three entities, none of which mentions a provider:

```csharp
public sealed class AccessRule {          // "family", "friends", "staff"
    int Id; int RealmId;
    string Name; string NormalizedName;   // unique per realm, like Group
    string? Description;
    DateTimeOffset CreatedAt;
}

public sealed class AccessRuleClause {    // one "who" predicate
    int Id; int AccessRuleId;
    AccessClauseKind Kind;
    int? UserId; int? GroupId;            // Kind = User / Group
    string? Value;                        // Kind = Email / EmailDomain / ExternalPolicy
    int Order;
}

public sealed class RouteAccessRule {     // ordered = precedence at the edge
    int Id; int RouteId; int AccessRuleId; int Order;
}
```

The indirection is the point. A rule is referenced by name from any number of routes, so adding a third
friend is one edit rather than one edit per hostname — and swapping a clause (an external policy for a
Watchtower group) migrates every route that names the rule without touching a single route row.

### 2. Attached rules replace the instance-wide default for that route; no rules means today's behaviour

A protected route with **no** attached rules resolves exactly as it does now: `Authenticated` admits the
four instance-wide settings, `Restricted` admits its grants. A route **with** attached rules admits the
union of their clauses and nothing else.

"Replace" rather than "extend" because the alternative needs a magic `default` pseudo-rule to be
expressible, and because an operator who attaches a rule has stated what admits people. The instance-wide
settings remain the fallback for every route that never opts in, which is what makes this a no-op for
existing deployments: the schema change adds tables, and no route behaves differently until a rule is
attached.

The composition an operator actually wanted is then two named rules and two attachments:

```
AccessRule "family"   [ ExternalPolicy(cloudflare, <family-uuid>) ]
AccessRule "friends"  [ ExternalPolicy(cloudflare, <friends-uuid>) ]

internal.example.com  → [ family ]
shared.example.com    → [ family, friends ]
```

`Restricted` keeps its meaning: it is the mode whose allow list is the route's own grants, so attaching
rules to it is **refused** rather than silently merged. A route that wants named rules is `Authenticated`;
a route that wants a hand-picked subject list is `Restricted`. Two modes, one axis each.

### 3. Six clause kinds, and only the two opaque ones are provider-bound

| Kind | In-process (`yarp`) | Cloudflare Access |
| --- | --- | --- |
| `Group(id)` | native, membership evaluated per request | member emails, flattened at reconcile |
| `User(id)` | native | the account's email; an account without one contributes nothing |
| `Email(addr)` | **not supported** | native `email` include |
| `EmailDomain(d)` | **not supported** | native `email_domain` include |
| `ExternalGroup(id)` | **not supported** — opaque | native Access-group include |
| `ExternalPolicy(id)` | **not supported** — opaque | native, attached by reference, never written |

`Email` and `EmailDomain` are edge-only **on purpose, not for lack of code**. Cloudflare's identity
provider verifies an address before asserting it; Watchtower does not — `User.Email` is nullable and
carries no unique index — so matching a session against an email in process would make an unverified,
non-unique column an authorization key. Supporting them in process is a decision about email verification,
and it is not this one.

`ExternalGroup` and `ExternalPolicy` are opaque by nature: Watchtower holds an identifier for a subject set
or an allow list the provider owns and can neither read nor evaluate. They are the only clause kinds that
name a provider, and their whole projection is one include and one array entry respectively. `ExternalGroup`
exists because `AccessGroupIds` already does — without it, an operator whose allow list lives in a
Cloudflare Access group could not express per-route composition at all without first converting it to a
reusable policy, which is work this feature would have created rather than removed.

### 4. Portability is declared per clause and refused at save time, never dropped at reconcile time

`AccessClauseSupport` maps each kind to the `AccessEnforcementPoint`s that can honour it (`InProcess`,
`CloudflareAccess`). A rule's portability is the intersection over its clauses; a route's is the
intersection over its rules.

Writing a route's access is **refused** when a clause the active provider cannot honour would be needed,
naming the clause and the provider. A rule may still be *created* with clauses the active provider cannot
honour — that is how an operator prepares a migration — but it cannot be attached to a route the provider
would then mis-serve.

This is the rule that makes a shared vocabulary worth having. The two silent divergences described above
are what the alternative looks like in practice: the model says one thing, the edge does another, and
nothing tells the operator which.

### 5. The lockout check becomes route-aware

ADR-0035 decided that a protected route whose allow list is empty gets an explicit deny-everyone
application and an `Error` row, because a lockout announces itself and an ungated hostname does not. That
stands. What changes is how "empty" is computed: a route with attached rules is judged on **its** clauses,
not on the instance-wide settings, so a Cloudflare-only operator with rules and empty global settings is
not denied.

`WatchtowerOptions.HasAccessAllowSource()` keeps its meaning — "the instance-wide fallback admits
somebody" — and is checked only where that fallback *is* the route's allow-list: an `Authenticated` route
attaching no rules.

> **Amended 2026-09-23.** This decision originally kept `HasAccessAllowSource()` as `proxy.createRoute`'s
> check "because a create carries no rules yet, for the same reason it cannot start `Restricted`". That
> reasoning was circular: a create carried no rules and no grants only because its request had no field for
> them, and nothing in the model required it — the route's realm is known from the stack the create names,
> and the whole policy fits in the transaction that writes the route. The consequence was not cosmetic. It
> forced a create-then-`setAccess` sequence that published every route under the instance-wide allow-list
> before narrowing it — the side-effect publishing ADR-0035 exists to prevent — and it left a Cloudflare
> deployment built entirely from access rules unable to create a protected route at all.
>
> `proxy.createRoute` now takes the whole policy — mode, bypass paths, identity forwarding, grants and
> access rules — validated by the same code as `proxy.setAccess` (`RouteAccessValidation`), and writes it in
> the same `SaveChanges` as the route, audited like a `setAccess` would be. The create-time guard is
> route-aware exactly as the reconcile is: rules decide an `Authenticated` route that attaches them, grants
> decide a `Restricted` one. The one refusal that remains is a genuine one — a *new* `Restricted` route
> naming nobody, which would be published admitting no one.
>
> `proxy.updateRoute` takes the same optional policy, with the same validation and the same reconcile
> (`RouteAccessWrite`) `proxy.setAccess` uses, applied in the one `SaveChanges` that writes the route's other
> fields: omitting the mode leaves access alone, naming it replaces the policy. That is what lets the Routes
> page have one route form for creating and editing — access included — instead of an edit form beside a
> separate access dialog, where saving the route and then its access in two calls could apply the first
> and fail the second. `proxy.setAccess` stays, unchanged, for clients that change access alone.

## Consequences

- Two new tables plus one join table; `Route` gains no column. Nothing behaves differently until a rule is
  attached, so the migration needs no data backfill.
- `proxy.getAccess` and `proxy.setAccess` gain `AccessRuleIds`, added last with a default so an older
  client keeps working (the `GrantedGroupIds` precedent).
- Rule CRUD lives in the **Proxy** module (`proxy.listAccessRules`, `proxy.setAccessRule`,
  `proxy.deleteAccessRule`) rather than a new module: the surface it serves is routes, and a separate
  `Modules:Access:Enabled` gate would let an operator disable management of state the projection still
  reads.
- `proxy.listExternalAccessPolicies` is a new read against the Cloudflare account so the picker shows the
  operator's own policy names instead of UUIDs. It needs an account-level listing the API client did not
  have; `ListAccessPoliciesAsync` is app-scoped.
- Rules are realm-scoped like groups, and a clause naming a user or group of another realm is refused — the
  same invariant `proxy.setAccess` already applies to grants.
- In-process evaluation of `Group`/`User` clauses is **not** in this change. Until it lands, a rule is
  enforced only by the Cloudflare projection, which is why decision 4 refuses attaching one under `yarp`
  rather than accepting it and quietly ignoring it.
- The clause vocabulary is deliberately short of two kinds that were considered and left out:
  `AnyAuthenticated` (native in process, realm-scoped, with **no** faithful Cloudflare equivalent — it is
  what `AccessMode.Authenticated` already means, and promoting it to a clause would give one concept two
  spellings) and `IdpGroup` (an external identity provider's group claim, which needs per-realm federation
  to mean anything in process).
