# ADR-0038: The product declares the configuration contract; environment variables resolve through a ladder

## Status

Proposed (2026-09-20). Full design:
[docs/products/design.md §Configuration](../products/design.md#configuration).

## Context

[ADR-0026](0026-products-are-the-deployable-unit.md) removed copy-at-provision for the git source:
`Stack` and `StackTemplate` hold a `ProductId` and `ProductSourceResolver` answers "what does this
actually clone?" from live rows. Environment variables were left behind. They are now the *only* axis
still copied — `TenantProvisioningService` materializes `StackTemplate.BaseEnvVars` into
`StackEnvVar` rows at provision time (`TenancyMapping.MergeEnv`), and `templates.adoptStack` does the
same for the keys an adopted stack does not already define. A template edit reaches no existing
tenant, which is exactly the defect ADR-0026 was written to remove.

That is the known half. The half that prompted this ADR is smaller and more telling: **an operator
with a single-stack product had nowhere to put the product's configuration at all.** The only shared
env surface is `BaseEnvVars`, which lives on `StackTemplate` — so "these are the variables this
application takes" required standing up a tenancy setup, with its domain pattern, realm, slug and
managed route, none of which were wanted. The reasonable fallback is what they did: put everything on
the stack. Nothing about that is wrong for one instance, and that is the point — the value here is
not *propagation*, which has nothing to propagate to at N=1. What is missing is a place to say which
of those values are not instance-specific.

Env vars are the one axis where the definition/runtime split ADR-0026 drew everywhere else was never
drawn. The deploy already has the shape for it: `DeployQueueService.BuildEnvFileContent` resolves
`repo .env < stack operator vars < reserved WATCHTOWER_*` by **omission** — a key a higher layer
defines is simply not written by a lower one, so the three sets are pairwise disjoint and physical
file order carries no precedence meaning. That ladder has room for more rungs.

The house precedent for an inheritance ladder with visible provenance is
[ADR-0020](0020-backup-service-settings-labels-win-ui-fills-gaps.md): resolve per key, render the
effective value with its source, and never accept an edit that would silently not take effect.

## Decision

### 1. `ProductEnvVar` — the product declares what the application takes

A product owns a set of `(Key, Value?)` rows: the keys its compose file expects, with a default where
one sensible default exists. **`Value` is nullable, and null means "required, supplied per
instance"** — a declared key with no value contributes nothing to the deploy but makes the
requirement visible in the UI and in the deploy output. This is what makes the feature pay off at one
stack: the product page documents the application's configuration, and a new deployment starts
pre-filled instead of empty.

### 2. One ladder, resolved at deploy time

```
repo .env  <  product  <  template  <  stack  <  reserved WATCHTOWER_*
```

`StackEnvResolver` mirrors `ProductSourceResolver` — a static pure function over rows the caller has
already loaded, throwing on a missing include rather than reading absence as "no override". Its
output is the operator set `BuildEnvFileContent` already consumes, so the existing
precedence-by-omission against the repository's `.env` and the reserved variables is unchanged, and
[ADR-0012](0012-direct-env-injection.md)'s generated override is untouched: this ADR changes what the
operator set *contains*, not how anything reaches a container.

### 3. `StackTemplate.BaseEnvVars` becomes a live rung, not a copy

Provisioning stops writing the merged result into `StackEnvVar` rows; adoption's env merge disappears
entirely, because precedence gives the keep-contract ("the running stack's values win") for free.

This is not a separable change. A live product rung above a copied template rung would mean editing
the product changes every tenant while editing the template changes nothing — one ladder with two
different semantics, which no operator can hold in their head. Adding the product rung *is*
converting the template rung.

### 4. An unset required key warns; it never fails the deploy and never guesses

A declared key with a null value that no lower rung supplies emits one warning line into the deploy
output, beside the existing injection line. Failing closed would break the back-compat contract for
every migrated product that happens not to set such a key, and "warn, never guess" is the discipline
ADR-0012 already established for ambiguous injection input.

### 5. Provenance is in the contract, not just the UI

`stacks.getEnv` returns, per key, the effective value plus its source (`Product | Template | Stack`)
and the values it shadows. The UI needs no new pattern: `EnvVarEditor` already renders a locked
read-only band above the editable rows for the reserved variables Watchtower injects, and its stated
reason is this ladder's — *"which variables does my container actually get" is one question, and
answering it in two places invites the reading that the editable list is the whole answer.* The
inherited rungs join that same list under the same treatment, labelled **Set by: product *acme/web***
or **Set by: fleet *acme-prod***, each offering **Override here**. An inherited value that a stack row
shadows is kept and shown, not deleted — ADR-0020's rule for a label-shadowed override.

### 6. Blast radius is stated before the save, and the roll-out is explicit

Saving product-level configuration enqueues nothing. The save-confirm enumerates the affected
deployments in the shape `products.update` already uses ("Saving changes the configuration for 3
deployments. They keep running until redeployed."), the change is audited as a field diff, and a
separate roll-out action fans out deploys. Env values are runtime behaviour, so a change that lands
silently at each stack's next unrelated deploy is the hazard this rung introduces; the answer is
making the deploy the operator's act, plus surfacing stacks whose last deploy predates the change
beside the existing `Behind` rollup.

### 7. The migration clears provably-redundant tenant rows

A tenant's copied `StackEnvVar` rows would otherwise shadow the template rung forever, leaving every
existing fleet exactly as un-propagating as it is today. The migration deletes a tenant's row when its
key **and value both exactly equal** the template's current base row — provably the copy, and provably
a no-op for what the next deploy applies. Rows that differ are genuine per-tenant overrides and are
kept. The only divergence is future template edits reaching those keys, which is the fix.

### 8. Values stay plaintext; no secret flag in v1

`StackEnvVar.Value` is plaintext today and `ProductEnvVar` is no different. A product-level secret has
fleet-wide blast radius and appears in every instance's env view, which is a real gap — but it is the
*existing* gap at a new rung, and masking is a change to every env surface at once. Deferred
deliberately, and the product rung is the thing that finally makes it worth doing.

## Consequences

- The copy-at-provision defect is gone from the last axis that still had it. `TenancyMapping.MergeEnv`
  and adoption's "env keys added" reporting both disappear; the adoption audit detail loses a field.
- `stack_env_vars` stops being the whole truth about a stack's environment. Anything that reads those
  rows directly to answer "what will this deploy apply" is wrong and must go through
  `StackEnvResolver` — the same rule `ProductSourceResolver` carries, and worth an invariant.
- `stacks.getEnv` grows fields (additive; existing readers keep working). `stacks.setEnv` continues to
  replace the stack's *own* rows only, which is what it always did — its meaning narrows without its
  contract changing.
- A hobby single-stack product gains a place to write its configuration down without touching
  tenancy. Tenancy stops being a prerequisite for anything except tenancy.
- Existing fleets begin propagating at the migration, for keys whose tenant rows matched. An operator
  who had deliberately set a tenant value identical to the fleet default loses that pin — stated in
  the release note; the fix is to set it again, and it then shows as an override.
- Stack env changes are unaudited today (the same gap as registry CRUD, noted in the design). Adding
  the product rung makes that worth fixing in passing rather than later.

## Rejected alternatives

- **Add the product rung, leave `BaseEnvVars` copied.** Two rungs with opposite semantics in one
  ladder; see §3.
- **Tell the operator to create a template.** The friction that produced this ADR. Tenancy carries a
  domain pattern, realm, slug and a managed route, and none of them are implied by "my app needs these
  variables".
- **Copy product env vars onto the stack at creation.** Re-creates the exact bug ADR-0026 removed, one
  layer up.
- **Fail the deploy when a required key is unset.** Breaks back-compat for migrated products and
  contradicts ADR-0012's warn-never-guess rule.
- **`{tenant}` interpolation in product values.** Right idea, wrong rung — per-tenant parameterization
  is what the template rung is for, and putting it on the product would give one key two meanings
  depending on which rung set it.
- **Leaving every copied tenant row in place at migration.** Safest-looking and worst: a 40-tenant
  fleet would inherit nothing until someone cleared 40 stacks by hand, so the propagation fix would
  ship switched off.
- **An `IsSecret` column in v1.** See §8.
- **Resolving the ladder at write time into a materialized effective set.** That is the copy again,
  with extra steps and a cache to invalidate.

## References

- ADR-0026 (the product as the deployable unit; `ProductSourceResolver`, the resolver this mirrors).
- ADR-0012 (the generated compose override — unchanged by this ADR; the warn-never-guess discipline).
- ADR-0014 (infrastructure-as-code wins over runtime edits; the pinned-field UI pattern).
- ADR-0020 (per-knob resolution with a visible source, and keeping a shadowed override rather than
  refusing it).
- `EnvVarEditor`'s `injected` band (`src/watchtower-web/src/components/env-var-editor.tsx`) — the
  read-only-row treatment the inherited rungs reuse, and the "one list, one question" reasoning this
  ladder inherits. ADR-0037 adds to the reserved rung it renders; neither changes the rung's position.
- `docs/products/design.md` §Configuration — the full design: schema, resolver, UI, migration.
