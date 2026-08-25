# Products & Releases — implementation status

Living companion to [design.md](design.md) and
[ADR-0026](../decisions/0026-products-are-the-deployable-unit.md). The design doc says what to
build; this file says how far it got, what is owed, and what a fresh session needs to know before
touching stage 3. **Update it at the end of every stage.**

Branch: `wt/watchtower-multi-tenant-design-bad75e` (pushed). Last updated 2026-08-25 after the
stage-2 hardening round.

## Where things stand

| Commit | Stage | What landed |
| --- | --- | --- |
| `814347e` | — | The design doc and ADR-0026. |
| `ca6202e` | **0 — refactors** | `ImageRef` (one image-reference parser; `GetRemoteDigestAsync` refactored onto it), `GitCloneService.CloneAtCommitAsync` (shallow fetch of one commit + full-clone fallback), `ComposeOverrideFile.ParseServices` also returns each service's image and `watchtower.release-image` label (unconsumed), and the instance-wide deploy gate `Watchtower:MaxConcurrentDeploys` (default 4). |
| `d9ed0f0` | **1a — product, backend** | `Product` entity owning the source; `Stack`/`StackTemplate` reference it (required, Restrict) with a nullable `BranchOverride`; the four copied source columns dropped; one backfill migration (`ProductBackfillSql`); `ProductSourceResolver` as the single answer to "what does this stack clone"; `ProductCatalog` find-or-create; the `Products` module (`products.list/get/create/update/delete`); DTO back-compat. |
| `899be78` | **1b — product, frontend** | Products catalogue/create/detail pages, the `productDetailTabs` extension point, the source picker on stack and template creation, the read-only "From product X" row in stack settings, the Product column. |
| `a978a5e` | **2 — CI link** | `Product.CiRepoId` + `CiRepoResolver` replacing URL string matching, `ci.getProductCi`/`ci.enableForProduct` (the stack-keyed handlers remain as forwards), toolchain recording through the product, CI tab moved from the stack page to the product page. |
| `c96207d` | **2 — hardening** | The stage-2 review's owed items: `CiRepoResolver` ignores a link whose `owner/name` no longer matches the parsed URL (both `ResolveAsync` and `FindForWriteAsync`) with four tests, correction left to the read path (reasoning in the resolver remarks and design.md), the `CiToolchainRecorder` `Attach` comment naming its dependency on `CiRepo` having no `xmin`, the widened change-tracker remarks, and the "ADR-0026 product backfill" wording. No contract change. |

**Next: stage 3 (releases, read-only)** — `Release`/`ReleaseImage`, the release webhook endpoint
with digest resolution and fingerprint idempotency, `products.listReleases`, manual
`products.createRelease`, the Releases tab. No deploy behaviour changes in that stage. Then 4
(release-aware deploys — the behaviour-changing one), 5 (secret sync), 6 (tenant release policy),
7 (tenant-aware backups). The roadmap table in [design.md](design.md#staged-roadmap) is the
authority for scope per stage.

## Invariants — do not break these

1. **Back-compat contract.** A product with no releases deploys byte-for-byte as it did before
   ADR-0026: shallow clone of the branch head, no image overrides. This is the acceptance test for
   every stage, and every migrated product starts in `Git` mode.
2. **Implicit products (hobby guarantee).** Creating a stack stays one form; `stacks.create` keeps
   its inline repo fields and find-or-creates the product. There is never a "create the product
   first" step.
3. **Convergent fan-out.** A release-triggered deploy carries no release id; `ExecuteDeployAsync`
   resolves `PinnedReleaseId ?? newest` at execution time. Capturing the id at enqueue reintroduces
   a downgrade race — see the reasoning in design.md §Convergent fan-out. "Exactly this release" is
   the *pin* intent.
4. **One update mechanism visible.** `Product.ReleaseMode` selects it: the Updates panel in `Git`
   mode, the Version panel in `Releases` mode, never both.
5. **Tenants inherit, they do not copy.** `ProductSourceResolver.InheritedBranch` exists because a
   settings save must not pin the branch a tenant merely inherits from its template. Any new write
   path that computes an override must use it.
6. **Deploy shows what it will apply.** Once versions exist (stage 4), no surface may render a
   Deploy button without the version it would deploy visible next to it.

## Owed work and accepted debt

The four stage-2 review items that were queued behind the stage landed in the hardening commit
above. What is left is accepted debt, not owed work:

- **`ProductCatalog`'s savepoint retry branch is untested** — forcing the interleave needs an
  injection seam inside `FindOrCreateAsync`, which is more test scaffolding than the branch is
  worth. Accepted: the savepoint create/release path is covered by every implicit-create test, and
  the branch itself only rolls back to the savepoint, detaches the speculative entity and loops back
  into the same re-read. Revisit if it ever grows a decision.

## Environment and process notes

- **PostgreSQL only.** Every instance was migrated by 2026-08-25, so the legacy SQLite import path
  is dead: its tests were deleted on this branch, the products migration deliberately breaks a
  legacy import, and `Services/SqliteImport/` is queued for removal in a separate cleanup. Never
  write compatibility shims for it.
- **Verification, from the repo root** (all four are expected green before a stage is committed):
  ```
  dotnet build Watchtower.slnx --configuration Release
  dotnet test Watchtower.slnx --configuration Release --no-build
  dotnet run --project src/Watchtower.Api --configuration Release --no-build -- --export-schema "$PWD/rpc-schema.json"
  cd src/watchtower-web && npm run build
  ```
  The schema export needs the absolute `$PWD` path — `dotnet run --project` sets the CWD to the
  project directory and would otherwise write the file there. `git diff rpc-schema.json` should show
  only intended additions; every stage so far has been additive-only.
- **Windows hosts carry known, unrelated test failures** (confirmed at baseline `c4f0c59`, not
  introduced by this work): a CRLF-sensitive snippet assertion in `BackupPlanOverrideTests`, an
  audit-ordering case in `AuthEndpointTests`, `ProxyIngressEndpointReloadTests`, and flaky
  ACME/certificate tests failing in the Windows X509 chain builder ("unknown chain building
  error"). On Windows, judge a stage by the suites its diff touches plus build/schema; the
  full-suite green bar applies to Linux/macOS and CI.
- **Elarion moves faster than model training data.** Read the `elarion` skill before adding
  handlers, modules, entities, or host wiring; copy conventions from `Modules/Tenancy` and
  `Modules/Ci` rather than writing them from memory.
- **Live verification is feasible** and was used for the frontend stages: a Postgres container plus
  the API and Vite dev server, driven through the browser tools, then torn down. Worth doing for UI
  work; typecheck alone missed nothing structural but did miss the stale-form and stranded-form
  defects that a walkthrough surfaced.
- **The language server in this worktree reports phantom errors** (missing `Microsoft.*`/`Xunit`
  namespaces, `WatchtowerDbContext` "has no member"). `dotnet build` is the authority; it has been
  clean at every commit.
- **How these stages were built:** an implementation agent per stage, then a fresh-context reviewer
  that only reports findings, looping until the reviewer has none, then a final architectural pass
  before commit. Two defects that mattered were caught only because the reviewer mutation-tested the
  new tests (they passed with the fix reverted) — worth keeping that habit for stages 4 and 6.
