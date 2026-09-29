# ADR-0040: The edge projection is authoritative — Watchtower owns its apps' policy attachments, and the realm invariant travels with the grants

- Status: Accepted
- Date: 2026-09-21
- Related: [ADR-0039](0039-access-rules-compose.md) (the model this projects),
  [ADR-0015](0015-proxy-provider-abstraction.md) (the provider seam),
  [ADR-0035](0035-new-routes-are-protected-by-default.md) (the lockout an empty allow list produces),
  [docs/central-auth/design.md](../central-auth/design.md) §13.5 (the realm invariant this decision
  carries across the seam),
  [docs/reverse-proxy/cloudflare.md](../reverse-proxy/cloudflare.md) (the operator guide).

## Context

The Cloudflare projection reconciles an Access application per protected route. Three of its rules were
written when the route table was the only input, and ADR-0039 makes all three load-bearing in a way they
were not before.

**The `policies` array is written only when Watchtower has something to put in it.** `Policies` is sent as
`null` when no reusable policy ids are configured, and the comment on the field records why: *"Null omits
the field entirely so an update leaves existing attachments untouched."* That was a kindness — an operator
who attached a policy by hand kept it across reconciles. It also means the array is **shared, unversioned
state** with no owner: Watchtower replaces the whole list the moment the instance-wide setting becomes
non-empty, silently discarding hand-made attachments, and an operator cannot tell from Watchtower which
policies are actually attached. With per-route rules there is now a real answer to project, and two writers
to one array is the wrong shape for it.

**The Watchtower-owned app-scoped policy is matched by the exact name `watchtower`.** Any other app-scoped
policy on the same application survives every reconcile. That is the mechanism an operator can use today to
hand-add a second policy, and it is the reason the workaround works — but it is also invisible state, and
once rules exist the same hostname has two places that decide who gets in.

**The grant flattening does not apply the realm invariant.** `LoadGrantedEmailsAsync` filters grants on
`Disabled` and a non-empty `Email` and nothing else. In process, `RouteAccessPolicy.RealmAdmits` guarantees
that *"a protected route is only ever reachable by an account of its own realm, whatever its grants say"*,
and says so explicitly about the case that matters: *"a stale grant left behind by a realm change grants
nothing rather than crossing the boundary."* At the Cloudflare edge that same stale grant **does** admit,
because the projection resolves the grant to an email and the email is asserted by Cloudflare's identity
provider with no realm anywhere in the chain.

One grant table, two enforcement points, opposite answers — and nothing anywhere records the difference.
It is not reachable by a single API call (a grant is refused across realms when written), so it takes a
realm change on an existing stack to produce; that makes it a latent divergence rather than an open door,
which is exactly the class of bug a shared model is supposed to make impossible.

## Decision

### 1. Watchtower owns the `policies` array of a route that attached access rules, and only then

Ownership follows the model, not the app. For a route that attached rules the projection sends the full
array on every reconcile — the external-policy clauses in attachment order, **empty array included**, so
deleting the last such clause detaches the policy at the edge. For a route that attached none, `null` keeps
being sent when there is nothing to attach, exactly as today.

The asymmetry is the point. Watchtower is the authority precisely where it has something to say; where the
route has stated nothing, there is no second writer to conflict with and no reason to touch the field. Two
things follow, both good:

- **Existing deployments do not change.** An operator who never creates a rule sees identical requests, so
  this decision inherits ADR-0039's no-op migration instead of adding a breaking one.
- **The breaking half is confined and opt-in.** Hand-attached reusable policies stop surviving reconciles
  *on the routes an operator attached rules to* — a route they were editing anyway, with the replacement
  (an `ExternalPolicy` clause) in front of them at the time. The upgrade note says so, and says it about
  one screen rather than about the whole instance.

Applications Watchtower did not create are untouched either way. Only the prefix-owned ones are managed,
and that rule is unchanged — this decision narrows what Watchtower *preserves inside* its own apps, never
what it *reaches*.

One property makes the array safe to write without knowing how Cloudflare treats `policies` with respect to
app-scoped policies: the Watchtower-owned app-scoped policy is reconciled from a listing taken **after** the
app write, so whether or not the array disturbs it, the end state of every reconcile is the same. The cost
of the pessimistic case is a redundant create, which the audit trail records.

### 2. The app-scoped policy keeps its reserved name, and that name is now documented as reserved

Matching on the literal name `watchtower` stays: it is what lets a reconcile update its own policy without
enumerating what else an operator has done. What changes is its status — a reserved name rather than an
implementation detail, so an operator adding an app-scoped policy by hand knows which name not to use and
knows that any other name is left alone.

Combined with decision 1, the division is: **app-scoped policies** are the escape hatch that survives
(anything not named `watchtower`), **attachments** are Watchtower's. An operator who wants a hand-made
allow list to outlive reconciles writes it as an app-scoped policy, not as an attachment.

### 3. The realm invariant travels with the grants

`LoadGrantedEmailsAsync` filters on the route's realm, the same realm `RouteAccessPolicy` computes, so a
grant that admits nobody in process contributes no email at the edge. A grant that survives a realm change
now grants nothing at both enforcement points.

This is a bug fix, and it is deliberately **not** conditional on ADR-0039: it lands on its own, ahead of the
model, because it is a divergence between two existing code paths and nothing about it needs rules to exist.

Under `Restricted` the realm is the route's; the filter is applied where the emails are resolved rather than
where they are projected, so any future caller of the same loader inherits it.

### 4. A route whose access cannot be faithfully projected is an error on the row, not a best-effort push

ADR-0035 established the shape: a protected route the projection cannot satisfy gets an explicit
deny-everyone app and `RouteStatus.Error`, because a lockout announces itself. ADR-0039 decision 4 adds the
save-time refusal that keeps most of these out of the database in the first place.

Between the two there is still a window — a rule can become unprojectable after it is attached (its last
clause deleted, or the provider switched). The projection treats that as the ADR-0035 case: deny, warn,
`Error` on the row, with the rule named in the message. It does not fall back to the instance-wide
settings, because "the operator attached a rule" and "the operator wants the default" are different
statements and guessing between them is how a hostname ends up admitting people nobody chose.

## Consequences

- **Breaking for one workflow, on opt-in only**, called out in the upgrade notes: reusable policies attached
  by hand to a `watchtower: ` application are dropped on the next reconcile *of a route that attached access
  rules*. The migration is one `ExternalPolicy` clause per attached policy, which the Routes page then
  shows. A deployment that creates no rules is unaffected.
- The realm fix changes who can reach a `Restricted` route under Cloudflare in exactly one situation — a
  grant left behind by a realm change — and it removes access rather than granting it, so it fails closed.
- `proxy.listExternalAccessPolicies` gives the operator the names needed to do that migration without
  leaving Watchtower.
- The audit detail line gains the attachment count alongside the inline rule count, so the trail records
  which of the two mechanisms decided a hostname.
- The reserved `watchtower` policy name is now part of the operator-facing contract and cannot be renamed
  without a migration that finds the old one first.
