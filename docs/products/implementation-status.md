# Products & Releases — implementation status

Living companion to [design.md](design.md) and
[ADR-0026](../decisions/0026-products-are-the-deployable-unit.md). The design doc says what to
build; this file says how far it got, what is owed, and what a fresh session needs to know before
touching stage 3. **Update it at the end of every stage.**

Branch: `wt/watchtower-multi-tenant-design-bad75e` (pushed). Last updated 2026-08-26 after stage 4a
(release-aware deploys, backend).

## Where things stand

| Commit | Stage | What landed |
| --- | --- | --- |
| `814347e` | — | The design doc and ADR-0026. |
| `ca6202e` | **0 — refactors** | `ImageRef` (one image-reference parser; `GetRemoteDigestAsync` refactored onto it), `GitCloneService.CloneAtCommitAsync` (shallow fetch of one commit + full-clone fallback), `ComposeOverrideFile.ParseServices` also returns each service's image and `watchtower.release-image` label (unconsumed), and the instance-wide deploy gate `Watchtower:MaxConcurrentDeploys` (default 4). |
| `d9ed0f0` | **1a — product, backend** | `Product` entity owning the source; `Stack`/`StackTemplate` reference it (required, Restrict) with a nullable `BranchOverride`; the four copied source columns dropped; one backfill migration (`ProductBackfillSql`); `ProductSourceResolver` as the single answer to "what does this stack clone"; `ProductCatalog` find-or-create; the `Products` module (`products.list/get/create/update/delete`); DTO back-compat. |
| `899be78` | **1b — product, frontend** | Products catalogue/create/detail pages, the `productDetailTabs` extension point, the source picker on stack and template creation, the read-only "From product X" row in stack settings, the Product column. |
| `a978a5e` | **2 — CI link** | `Product.CiRepoId` + `CiRepoResolver` replacing URL string matching, `ci.getProductCi`/`ci.enableForProduct` (the stack-keyed handlers remain as forwards), toolchain recording through the product, CI tab moved from the stack page to the product page. |
| `18090e1` | **2 — hardening** | The stage-2 review's owed items: `CiRepoResolver` ignores a link whose `owner/name` no longer matches the parsed URL (both `ResolveAsync` and `FindForWriteAsync`) with four tests, correction left to the read path (reasoning in the resolver remarks and design.md), the `CiToolchainRecorder` `Attach` comment naming its dependency on `CiRepo` having no `xmin`, the widened change-tracker remarks, and the "ADR-0026 product backfill" wording. No contract change. |
| `0140c61` | **3 — releases, read-only** | `Release`/`ReleaseImage` + one additive migration (`AddReleases`) and the product's `ReleaseWebhookToken`/`ReleaseWebhookEnabled`; `ReleaseIntakeService` as the one intake pipeline (branch gate, registry gate, tag→digest resolution behind `IReleaseDigestResolver`, `ReleaseFingerprint`, idempotency) shared by `POST /api/webhooks/products/{id}/release` and `products.createRelease`; the six new `products.*` release methods; `BearerTokens` extracted from `AppApiTokens` for the `wtrel_` token; the Releases tab with the pre-filled CI snippet card, the Overview "Recent releases" section and the catalogue's Latest release column. **No deploy behaviour changes: `stacksEnqueued` is hard-coded 0.** One behaviour change outside releases, from the promised stack-webhook retrofit: `POST /api/webhooks/stacks/{id}/deploy` now verifies its bearer with `BearerTokens.Verify`, so an **enabled webhook with an empty token refuses every call** instead of accepting unauthenticated deploys. Needs a release-note line; the stack Settings copy that advertised the old behaviour was updated with it. |
| `_pending_` | **4a — release-aware deploys, backend** | `Product.ReleaseMode` (default `Git`), `Stack.PinnedReleaseId` (Restrict) / `LastDeployedReleaseId` (SetNull), `DeployEvent.ReleaseId` (SetNull) and the update check's `AvailableReleaseId`/`AvailableReleaseVersion`/`DriftedContainers`, in one additive migration (`AddReleaseAwareDeploys`). `ReleaseResolver` (`PinnedReleaseId ?? newest`, resolved at execution time), `ImagePinPlan` + `ComposeOverrideFile.Render(envPlan, imagePlan)`, `ReleaseRolloutService`, `ReleaseImageValidator` (the pin pre-flight), `DeployTriggers`. `ReleaseIntakeService.PublishAsync` flips `Git → Releases` in the same `SaveChanges` as the insert and takes an optional post-commit roll-out hook; the webhook's `stacksEnqueued` is real. `stacks.setRelease`, `products.deployRelease`, the owed `products.deleteRelease` pinned guard, `releaseMode` on `products.update`. `AutoDeployBackgroundService` and `StackUpdateService` gained a mode branch, and `Enqueue`'s coalesce branch gained the trigger-promotion rule that keeps an operator's deploy from merging into a skippable one. **Git mode is byte-for-byte unchanged and that is a golden test.** |

**Next: stage 4b (release-aware deploys, frontend)** — the Version panel and the roll-out/pin dialog,
the Updates-vs-Version switch driven by `StackDto.releaseMode` (invariant 4), the tracking chip and
"N behind" badge, the mode-revert control on the product page, and the "latest ≠ branch head"
warning. Everything it needs already exists on the wire — see **Stage 4b handoff** below. Then 5
(secret sync), 6 (tenant release policy), 7 (tenant-aware backups). The roadmap table in
[design.md](design.md#staged-roadmap) is the authority for scope per stage.

## Invariants — do not break these

1. **Back-compat contract.** A product in `Git` mode deploys byte-for-byte as it did before
   ADR-0026: shallow clone of the branch head, no image overrides. This is the acceptance test for
   every stage, and every migrated product starts in `Git` mode. Since stage 4a it is *structural*:
   `ReleaseResolver.UsesReleases` is the one predicate every mode branch asks — the resolver itself,
   the deploy call site (which uses it to avoid opening a scope at all), `IsEligible` and
   `StackUpdateService` — so there is a single place the answer can be wrong and a single mutation
   point that proves it right. The deploy pipeline's release branches are then all
   `release is not null`. Two tests hold it —
   `ReleaseDeployTests.Deploy_OfAGitModeProduct_ClonesTheBranchHeadAndPinsNothing` (exact git
   invocation and exact override body, with releases deliberately present) and
   `ReleaseUpdateCheckTests.Check_OfAGitModeProduct_StillPollsTheRegistry`. Both were mutation-checked
   by making `UsesReleases` return true. **Note that the switch is the mode, not the absence of releases**: a
   product can hold releases and stay in `Git` mode, and does until its next release flips it.
2. **Implicit products (hobby guarantee).** Creating a stack stays one form; `stacks.create` keeps
   its inline repo fields and find-or-creates the product. There is never a "create the product
   first" step.
3. **Convergent fan-out.** A release-triggered deploy carries no release id; `ExecuteDeployAsync`
   resolves `PinnedReleaseId ?? newest` at execution time. Capturing the id at enqueue reintroduces
   a downgrade race — see the reasoning in design.md §Convergent fan-out. "Exactly this release" is
   the *pin* intent. Since stage 4a the resolution happens **after** the concurrency gate's re-read of
   the stack, so a pin or a mode change written while a deploy was parked is the one that applies —
   the same staleness rule the stopped-stack re-check follows. Moving the resolution above the gate
   fails `Deploy_ResolvesTheReleaseThatLandedWhileItWaitedAtTheGate` and
   `Deploy_HonoursAPinWrittenWhileItWaitedAtTheGate`.
   The one deploy that may skip work is a `"release"`-triggered one whose resolved release already
   equals `LastDeployedReleaseId` — and only that trigger, and only for a plain deploy: a coalesced
   volume recreate is never swallowed (the `removeVolumes` clause in `ExecuteDeployAsync`, beside `DeployTriggers.MayShortCircuit`).
4. **One update mechanism visible.** `Product.ReleaseMode` selects it: the Updates panel in `Git`
   mode, the Version panel in `Releases` mode, never both.
5. **Tenants inherit, they do not copy.** `ProductSourceResolver.InheritedBranch` exists because a
   settings save must not pin the branch a tenant merely inherits from its template. Any new write
   path that computes an override must use it.
6. **Deploy shows what it will apply.** Once versions exist (stage 4), no surface may render a
   Deploy button without the version it would deploy visible next to it. Stage 4a is why every
   `StackDto` producer now `Include`s `PinnedRelease` and `LastDeployedRelease` — a new query that
   feeds `StackMapping.ToDto` and forgets them renders a chip-less panel rather than failing, so add
   the two includes with the two the product and template already need.
7. **Newest is the highest `Release.Id`.** `CreatedAt` and `PublishedAt` are display values and
   nothing orders on them — two instances writing releases a second apart must not be able to invert
   the order by disagreeing about the time. Every list, every "latest" and stage 4's
   `PinnedReleaseId ?? newest` resolution reads the id.
8. **Release identity is the fingerprint, and the unique indexes are the enforcement.**
   `sha256(commit + "\n" + sorted "repository@digest" lines)`, so a retried call is a replay and a
   rebuild of the same commit onto new layers is a new release. The pre-checks in
   `ReleaseIntakeService` exist for the error message; `(product_id, fingerprint)` and
   `(product_id, version)` are what make two simultaneous reports produce one release. Any new write
   path must go through `ReleaseIntakeService.PublishAsync` rather than inserting a release itself.
9. **The mode flip and the release insert are one write.** `ReleaseIntakeService` tracks the product
   and lets the flip ride in the same `SaveChanges` as the release. There must never be a state in
   which a release exists and the product still deploys branch heads, and no ordering of two separate
   statements gives that. The corollary: the roll-out is *not* in that write — it runs after the
   commit, through the optional `onCreated` hook. **A caller that holds an open transaction must not
   supply that hook**: the hook fires after `SaveChanges`, which commits only when nothing else owns a
   transaction on the context, so inside one the release is still uncommitted and the deploys it
   enqueues — other threads, other connections — would each resolve the previous release or none. The
   webhook is safe because it is a minimal-API endpoint that opens no transaction of its own; several
   handlers do open one, which is why `products.createRelease` records and stops and
   `products.deployRelease` is the separate, explicit way to roll a release out.
10. **Only `"release"` may short-circuit, and coalescing may never demote a trigger into it.** Every
    other trigger runs the full pipeline even when the resolved release has not changed, because a
    deploy also converges the compose file, the environment, the generated override and the proxy
    wiring. Two ways to break it: widening `DeployTriggers.MayShortCircuit`, and — the subtler one —
    letting `Enqueue`'s coalesce branch keep a pending `"release"` when a `"manual"` merges onto it,
    which would make the operator's Deploy button report success having done nothing. The merge rule
    is that a trigger which may *not* short-circuit supersedes one that may, with `volume-recreate`
    outranking everything; it mirrors the volume union rule, and for the same reason — a coalesced
    request must never come out weaker than either request that went in.
11. **A short-circuited deploy writes to the event and nothing else.** `Stack.LastDeployedAt` and
    `Stack.LastDeployStatus` describe the last deploy that actually ran; if a no-op touched them,
    every tenant of a product would show a fresh "deployed just now" after every release. It is
    decided before `MarkRunning`, so the event never passes through `running` either.

## Owed work and accepted debt

The four stage-2 review items that were queued behind the stage landed in the hardening commit
above, and stage 4a paid off the pinned-release delete guard it owed. What is left is accepted debt,
not owed work:

- **`docker.io` is always an admitted registry** for a release image, because an unqualified image
  lives there and a default install has no Hub credential to recognize it by. A leaked release token
  can therefore pin any public Hub digest; the compose-service match, the pin pre-validation and
  rollout visibility are the mitigations (design.md §Release intake spells the three accepted
  properties out, including the 401-vs-404 disclosure for *enabled* products).
- **`ReleaseIntakeService.KnownRegistryHosts()` does synchronous database and file I/O** on the
  anonymous webhook path — it goes through `RegistryAuthBuilder`, whose whole shape is synchronous
  (`ListResolvedRegistries` reads `db.Registries` and the host `docker/config.json`). Accepted:
  async-ifying that service reaches well beyond releases, the per-client rate limit bounds how often
  an unauthenticated caller can reach it, and the call happens after the token check for everyone
  else. Revisit if the endpoint ever sees real load.
- **The mode flip carries `Product`'s `xmin`.** A product edit landing in the microseconds between
  the read and the save makes that one call fail with a concurrency exception and write nothing.
  Accepted: it is retryable and self-correcting (the retry finds the mode already flipped and skips
  it), it can only happen on the single call in a product's life that flips the mode, and the
  alternative — an unconditional second statement — trades it for a window in which a release exists
  while the product still deploys branch heads (invariant 9).
- **`StackUpdateCheck.AvailableReleaseId` is deliberately not a foreign key.** It is a cache row; a
  value that stops matching a live release is corrected by the next check rather than by a schema
  rule that would make deleting a release harder. `AvailableReleaseVersion` is denormalized beside it
  so a stack list renders "v… available" without a join per row.
- **`AutoDeployBackgroundService` has no timing tests.** It had none before stage 4a either; what
  stage 4a added is `IsEligible` (the two release-mode rules), which is `internal` and directly
  tested. The interval and window machinery around it is still only exercised in production.
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
  full-suite green bar applies to Linux/macOS and CI. One more member of the certificate family,
  identified at stage 4a: `FileStateImportTests.Certificates_AreImported_AndServed` fails with a
  `NullReferenceException` rather than the chain-builder message, because it dereferences
  `SelectCertificate(host)` without a null check — same root cause, different symptom. Stage 4a's
  full run on Windows was 39 failures across exactly these families: 17 `CertificateStoreTests`,
  11 ACME across four classes (`AcmeOrderFlowTests` 6, `AcmeFailurePathTests` 2,
  `AcmeIssuerLeaseTests` 2, `AcmeHttpClientTrustTests` 1), 4 `CertificateManagerTests`,
  2 `CertificateManagerProjectionTests`, and one each of `AuthEndpointTests`,
  `BackupPlanOverrideTests`, `ProxyIngressEndpointReloadTests`, `ProxyChangeSignalTests` and
  `FileStateImportTests`. The last two were re-run against a stashed tree at `23bb326` and fail
  identically there.
- **Elarion moves faster than model training data.** Read the `elarion` skill before adding
  handlers, modules, entities, or host wiring; copy conventions from `Modules/Tenancy` and
  `Modules/Ci` rather than writing them from memory.
- **Live verification is feasible** and was used for the frontend stages: a Postgres container plus
  the API and Vite dev server, driven through the browser tools, then torn down. Worth doing for UI
  work; typecheck alone missed nothing structural but did miss the stale-form and stranded-form
  defects that a walkthrough surfaced — and in stage 3 the "card collapses once releases exist" bug,
  where the state was initialised from a list that had not loaded yet. On this Windows host the
  browser pane does not composite, so screenshots and synthetic clicks fail; driving the page with
  `javascript_tool` (`.click()`, native value setters) works and is what stage 3 used.
- **The language server in this worktree reports phantom errors** (missing `Microsoft.*`/`Xunit`
  namespaces, `WatchtowerDbContext` "has no member"). `dotnet build` is the authority; it has been
  clean at every commit.
- **How these stages were built:** an implementation agent per stage, then a fresh-context reviewer
  that only reports findings, looping until the reviewer has none, then a final architectural pass
  before commit. Two defects that mattered were caught only because the reviewer mutation-tested the
  new tests (they passed with the fix reverted) — worth keeping that habit for stages 4 and 6.
  Stage 4a mutation-tested its load-bearing claims before handing over: widening `MayShortCircuit` to
  every trigger (4 failures), making `ReleaseResolver.UsesReleases` return true (the Git golden test,
  the Git update-check test and the Git eligibility test), resolving the release above the
  concurrency gate (both convergence tests), dropping the `AutoDeployMode` clause from the fan-out
  predicate (2 rollout tests + the webhook's `stacksEnqueued`), disabling the pinned-delete guard,
  removing the coalesce trigger-promotion rule
  (`Enqueue_CoalescingAManualDeployOntoAPendingRelease_RunsTheFullPipeline`), and re-adding the
  stack-status write to the short-circuit
  (`Deploy_ThatShortCircuits_LeavesTheStacksLastDeployFieldsAlone`). All were caught.

## Stage 4b handoff — what the backend already gives the UI

Everything below is on the wire at `_pending_` and needs no further backend work. `rpc-schema.json`
is regenerated; `npm run generate:rpc` produces the types.

- **`StackDto`** gained `releaseMode` (`"git"` | `"releases"`), `trackingMode`
  (`"latest"` | `"pinned"`, derived from the pin — there is no column), `pinnedRelease` and
  `lastDeployedRelease` (each `{ id, version }` or null), and three update-check fields:
  `availableReleaseId`, `availableReleaseVersion`, `driftedContainers` (container names). Invariant 4
  is `releaseMode`: render the Updates panel or the Version panel, never both. "N behind" needs a
  release list, which `products.listReleases` already serves.
- **`ProductDto`** gained `releaseMode`, beside the existing `latestRelease`. The Overview page's
  "latest ≠ branch head" warning compares `latestRelease` against the branch head the product page
  already shows; nothing new is needed for it.
- **`stacks.setRelease(stackId, releaseId | null, deploy = true)`** → `{ stack, deployed,
  deployEventId }`. `deploy: false` is the Save button, `true` Save-and-deploy. It answers `409`
  when an image of the target release is gone (message names the `repo@digest`), `409` when the
  product is in Git mode, a business-rule error when a registry did not answer (retryable — say so
  rather than blaming the release), and a validation error for a release of another product. A stopped stack is pinned successfully with
  `deployed: false` — show that, do not treat it as an error.
- **`products.deployRelease(productId, releaseId?)`** → `{ releaseId, version, stacksEnqueued,
  deployEventIds }`. Pass the release id the dialog displayed and it is refused with `409` if a newer
  one landed meanwhile, which is the roll-out dialog's staleness guard. It reaches `Off` and
  `Scheduled` stacks too (deliberate — the canary workflow depends on it) but never pinned or stopped
  ones, so the dialog should say "N latest-tracking, running stacks".
- **`products.update`** takes an optional `releaseMode`; `"releases"` is refused for a product with
  no releases. That is the operator's revert control on the product page.
- **`products.deleteRelease`** now answers `409` naming the stacks that pin the release.
- The deploy output the log pane renders gained three line shapes:
  `[Watchtower] Deploying release v… (commit ab12cd34)`,
  `[Watchtower] Pinning service 'api' to ghcr.io/acme/api@sha256:…`, and
  `[Watchtower] Already on v… — nothing to do.` for a short-circuited fan-out deploy.
- **Not built, and 4b's job:** the Version panel itself, the pin/roll-out dialog, the tracking chip
  and behind-badge, the mode-revert control, the `AutoDeployMode` selector defaulting to `OnChange`
  when creating a stack from a `Releases`-mode product (design.md §"Auto-deploy precedence" — the
  model still defaults to `Off`, so this is a UI default), and the relabelling of the three
  `AutoDeployMode` options in release mode.
