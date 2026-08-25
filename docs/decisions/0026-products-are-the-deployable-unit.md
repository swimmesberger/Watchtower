# ADR-0026: A Product is the deployable unit; stacks reference it, and releases pin its images

## Status

Proposed (2026-08-25). Full design: [docs/products/design.md](../products/design.md).

## Context

The git source (`RepositoryUrl`, `Branch`, `ComposeFilePath`, `CredentialId`) is denormalized onto
every `Stack` *and* every `StackTemplate`. Templates copy it onto each tenant at provision time and
never propagate changes afterwards. `CiRepo` — the runner orchestration and Actions-secret sync —
is linked to stacks only by parsing `Stack.RepositoryUrl`. And there is no first-class notion of
"the thing that was built": a deploy is "clone the branch head and hope the registry tag moved",
which is exactly the coupling ADR-0012's generated override had to work around, makes per-tenant
version pinning impossible, and leaves the CI chain broken between "image pushed" and "stack
redeployed" (hand-wired webhooks or digest polling are the only bridges).

Two use cases must feel equally native: a hobby single-stack deployment ("point Watchtower at my
repo, build and run it"), and a multi-tenant fleet of the same application on subdomains with
controlled version rollout (most tenants on the newest build, some pinned).

## Decision

1. **`Product` is the deployable unit.** The source (`RepositoryUrl`, `ComposeFilePath`,
   `DefaultBranch`, `CredentialId`) moves onto it; every `Stack` and every `StackTemplate` carries
   a required `ProductId` (`Restrict` — deleting a product is refused while anything references
   it, like realm deletion). Templates thereby narrow to tenancy policy; source changes propagate
   to tenants by construction, because nothing is copied any more.
2. **Branch is product-level with nullable per-stack and per-template overrides**; compose path and
   credential are product-only — a different compose file in the same repo is a different product.
3. **A `Release` is one build**: an optional commit SHA plus a set of `(image repository, manifest
   index digest)` pairs, keyed unique per `(product, version)` and idempotent on a fingerprint of
   commit + digests. Releases arrive through a product-level webhook called from the CI workflow
   (bearer token, branch-validated, tag→digest resolution done server-side), with digest polling as
   fallback. Latest = highest `Id`.
4. **A stack tracks latest or pins a release** — `PinnedReleaseId` nullable, null meaning "track
   latest"; no mode enum, no channels in v1. A pin opts the stack out of *all* automation and is
   protected by `Restrict` (deleting a pinned release is refused rather than silently flipping the
   stack to latest). `AutoDeployMode` is reinterpreted, not duplicated: in release mode, `Off` /
   `OnChange` / `Scheduled` keep their intents with the trigger swapped from polling to the release
   event.
5. **A product with no releases deploys exactly as today** — shallow clone of the branch head, no
   image overrides, today's update polling and UI. This is the back-compat contract: every migrated
   product starts in `Git` mode, and the whole release machinery stays dormant until a product's
   own CI publishes its first release (which durably flips `Product.ReleaseMode`, audited and
   revertible).
6. **Image pinning reuses the ADR-0012 generated override.** A pinned or release-tracked deploy
   clones at the release's commit and rewrites `image:` for every compose service whose normalized
   image repository matches a release image (so `postgres:16` is never touched);
   `watchtower.release-image` forces or exempts, following the `watchtower.inject-token` label
   discipline. Deploys resolve the target release at execution time, so the existing per-stack
   coalescing stays correct under fan-out.
7. **`CiRepo` stays separate GitHub infrastructure.** `Product.CiRepoId` (SET NULL) replaces URL
   string matching; the Actions-secret sync generalizes to also push `WATCHTOWER_URL`,
   `WATCHTOWER_PRODUCT_ID` and `WATCHTOWER_RELEASE_TOKEN`, with at most one product per repo owning
   the release sync (filtered unique index).
8. **Existing rows are backfilled inside the EF migration**: one product per normalized
   `(repository URL, compose path)` across stacks and templates, branch differences becoming
   overrides; `stacks.create` keeps its inline repo fields and find-or-creates the product by the
   same rule, so creating a stack remains a single form and API clients keep working.

## Consequences

- Template propagation is fixed by construction; a detached tenant keeps working because it holds
  its own `ProductId` — the same outcome as today's copied fields, by a better mechanism.
- Four columns leave `stacks` and `stack_templates`; `StackDto` keeps them as read-only effective
  projections, and `stacks.update` refuses repo-field *changes* (pointing at `products.update`;
  `branch` maps to the override) — a contract change worth a release note.
- Rollback becomes "pin an older release": the checkout and the digests both go back. Watchtower
  still cannot roll back an application's database — the documentation points at backups
  (ADR-0016) and expand/contract migrations, and the roll-out dialog offers a pre-deploy fleet
  backup.
- In release mode the per-tenant registry digest polling disappears (drift becomes a local
  `docker inspect` comparison), removing the registry HEAD storm.
- Release fan-out makes unbounded parallel deploys routine, so a global concurrency gate
  (`Watchtower:MaxConcurrentDeploys`) becomes a prerequisite — it also fixes the latent
  `templates.deployAll` thundering herd.
- The SQLite import path needs a conversion step for the NOT NULL `product_id` (the importer copies
  column-by-name and would fail before any fixup); this must be built and tested deliberately.
- A product's first release visibly switches its latest-tracking stacks from branch-HEAD to
  release deploys — announced, audited, and possibly to an older commit than the branch head; the
  UI warns when latest's commit is not the head.
- The UI reorganizes around the definition/runtime split: a Products page joins the sidebar,
  Templates folds into the product's Instances tab (redirects preserved), CI moves from the stack
  tab to the product, repo fields leave stack Settings for a read-only link, and exactly one update
  mechanism is ever rendered per stack (Updates panel in `Git` mode, Version panel in `Releases`
  mode).

## Rejected alternatives

- **Merging `CiRepo` into `Product`** — GitHub-specific fields on every product, and the
  cardinality is wrong (several products can share one repo).
- **Naming the entity "App"/"Application"** — collides with the App API (`WATCHTOWER_APP_TOKEN`,
  `/api/app/*`) and the realm-user "Applications" portal, i.e. the opposite end of the pipeline.
- **One product per `(repo, branch)`** — forks the product list into per-branch duplicates that
  then diverge, re-creating the denormalization this ADR removes.
- **A `TrackingMode` enum column** — redundant with the nullable pin; it would add an invalid-state
  axis to the schema for a distinction the DTO can derive.
- **A third "tenant follows template's release" mode** — release policy is inherently per-tenant;
  the template contributes a default at provisioning plus an explicit bulk action.
- **Channels (stable/beta) in v1** — latest + pin expresses every rollout both personas need; a
  third axis triples the mental model. `Release` keeps room to add channels later.
- **Commit-keyed webhook idempotency** — would wrongly swallow a genuine rebuild of the same commit
  with new base-image digests; the fingerprint keys on what actually changes.
- **Per-stack release webhooks** — one CI job produces one artifact for N stacks; N tokens in one
  repo is the wrong shape.

## References

- ADR-0009 (management API manages a template's tenants), ADR-0012 (generated compose override —
  the seam image pinning extends), ADR-0015/0022 (proxy provides the per-tenant subdomain routes),
  ADR-0024 (state in PostgreSQL; `xmin` concurrency), ADR-0025 (desired state — stopped stacks are
  skipped by fan-out).
- `docs/ci-runners/design.md` — the runner orchestration and the Actions-secret sync mechanism the
  release-token sync generalizes.
- `docs/products/design.md` — the full design this ADR summarizes, including the release webhook
  contract, UX architecture, tenant-aware backups, migration plan and staged roadmap.
