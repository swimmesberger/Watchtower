# Products & Releases — implementation status

Living companion to [design.md](design.md) and
[ADR-0026](../decisions/0026-products-are-the-deployable-unit.md). The design doc says what to
build; this file says how far it got and what is owed. **The roadmap is complete**: stages 0–7 have
all landed, so this file is now the handover for whoever maintains the feature rather than the brief
for the next stage.

Branch: `wt/watchtower-multi-tenant-design-bad75e` (pushed). Last updated 2026-08-26 after stage 9
(adopting an existing stack as a tenant — a user-requested addition to the end-state PR, on branch
`wt/adopt-stack-as-tenant`). Stage 8c before it was the dashboard fleet view, frontend only. Stage 8b
was the last *structural* piece: the end-state IA plus the two owed frontend gaps. Stage 7 was the last *feature* stage;
8a added no behaviour an operator can see, retired four accepted-debt entries and moved the `xmin`
concurrency token onto the entities; 8b lands the information architecture design.md always specified
(Templates leaves the sidebar and becomes the product's Instances tab) plus the two frontend deferrals
that were still owed. All of it on the rule PR #58 introduced — this architecture is the end state, so
debt that is architectural rather than an irreducible trade-off is paid before it merges.

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
| `6bf8997` | **4a — release-aware deploys, backend** | `Product.ReleaseMode` (default `Git`), `Stack.PinnedReleaseId` (Restrict) / `LastDeployedReleaseId` (SetNull), `DeployEvent.ReleaseId` (SetNull) and the update check's `AvailableReleaseId`/`AvailableReleaseVersion`/`DriftedContainers`, in one additive migration (`AddReleaseAwareDeploys`). `ReleaseResolver` (`PinnedReleaseId ?? newest`, resolved at execution time), `ImagePinPlan` + `ComposeOverrideFile.Render(envPlan, imagePlan)`, `ReleaseRolloutService`, `ReleaseImageValidator` (the pin pre-flight), `DeployTriggers`. `ReleaseIntakeService.PublishAsync` flips `Git → Releases` in the same `SaveChanges` as the insert and takes an optional post-commit roll-out hook; the webhook's `stacksEnqueued` is real. `stacks.setRelease`, `products.deployRelease`, the owed `products.deleteRelease` pinned guard, `releaseMode` on `products.update`. `AutoDeployBackgroundService` and `StackUpdateService` gained a mode branch, and `Enqueue`'s coalesce branch gained the trigger-promotion rule that keeps an operator's deploy from merging into a skippable one. **Git mode is byte-for-byte unchanged and that is a golden test.** |
| `15eb618` | **4b — release-aware deploys, frontend** | The version UX, in three new files plus eight edits and **zero backend changes**. `lib/release.ts` is where both UI invariants are expressed once, as pure functions of the DTOs: `usesReleases` (invariant 4's predicate, mirroring `ReleaseResolver.UsesReleases`), `newestRelease` (one source, live list preferred over the cached check), `deployTargetVersion` (invariant 6's single answer), `availableRelease`, `pinnedBehind`, `behindCount`. It lives in `lib/` because three modules read it and **modules never import each other** — stacks, dashboard and products. `modules/stacks/StackVersion.tsx` turns those answers into the three stack-page surfaces (header fragment, Version dialog, Version panel — the only renderer of the latter); `modules/products/DeployLatestButton.tsx` is the product-scoped roll-out (`products.deployRelease`) shared by the product header and the Releases tab header. Edits: the stack header meta line + mobile FAB (`StackDetailPage`), the Updates-vs-Version ternary and the containers empty state (stack `OverviewTab`), "Automatic rollout" + the pinned-and-disabled select (stack `SettingsTab`), the version chip and mobile card (`StacksPage`), the dashboard card's Deploy label and its mode-aware update badge (`dashboard/sections.tsx`), the `OnChange` creation default (`StackNewPage`), the product primary button (`ProductDetailPage`), the Releases tab header (`ReleasesTab`), and `lib/{types,api}.ts` for the new `StackDto` fields, `stacks.setRelease` and `products.deployRelease`. |
| `0c51bc9` | **5 — secret sync** | `CiActionsConfigSync`, the per-repo Actions-config pass lifted out of `CiRunnerOrchestrator` and generalized into two independent contributors with independent hash guards. Registry (unchanged, state on `CiRepo`) plus release (new, state on `Product`): the `WATCHTOWER_URL` and `WATCHTOWER_PRODUCT_ID` variables and the sealed-box `WATCHTOWER_RELEASE_TOKEN` secret. `Product` gained `SyncReleaseSecrets`, `ActionsSyncedHash`, `ActionsSyncedAt`, `LastActionsSyncError` in one additive migration (`AddReleaseSecretSync`) with the filtered unique index `ix_products_ci_repo_id_sync_release_secrets`. `CiRepoResolver.FindSyncingProductsAsync` is the reverse lookup. `ci.setReleaseSecretsSync` is the toggle (PAT probe, monorepo refusal in words, token minted when missing); `products.rotateReleaseToken` and `products.update` clear the hash and wake the loop, and a repository move turns the sync off. `ci.getProductCi` carries the state. Frontend: a release-secrets card on the CI tab built to `RegistrySyncCard`'s pattern, and the Releases tab's token card collapses its manual instructions and switches the snippet to `${{ vars.WATCHTOWER_* }}` once — and only once — a push has actually landed. |

| `bd2adbd` | **6 — tenant release policy** | `StackTemplate.DefaultPinnedReleaseId` (SET NULL) and `Product.RetainReleases` (default 50) in one additive migration (`AddTenantReleasePolicy`). `TenantProvisioningService` copies the default onto each new tenant — the one field family ADR-0026 copies. `templates.setTenantsRelease` writes the pin onto every tenant *and* the template default in one call, behind the `stacks.setRelease` pre-flight and its Git-mode refusal. `products.getReleaseRollout` / `products.retryFailedRollout` are the partial-failure surface, and `DeployEventDto` gained `releaseId`/`releaseVersion` (the widening 4b owed). `ReleasePruner` is release retention, run post-create by `ReleaseIntakeService` and guarded by four protection rules (invariant 15). Frontend: the Instances roster's Version column, rollup and bulk action; `components/set-release-dialog.tsx` (the shared roll-out dialog, two apply paths); the Releases tab's contextual row action and per-row rollout summary + Retry failed; the product Settings mode-revert control and the Overview "latest ≠ branch head" warning; the deploy-history version chip. `useProductReleases` moved to `hooks/use-product-releases.ts` — three modules read it now. |
| `3666733` | **7 — tenant-aware backups** | The last stage. `Stack.BackupDirectory` (nullable, stamped at creation, legacy rows computed as before and stamped on their next *successful* backup); `Stack.BackupEnabled/BackupStopContainers/BackupQuiesceMode` widened to tri-state; `StackTemplate.BackupEnabled?/BackupCron?/BackupStopContainers?/BackupQuiesceMode?` plus the `template_backup_service_overrides` table — one additive migration (`AddTenantAwareBackups`) that rewrites no values. `BackupPolicyResolver` is the one answer to "what policy does this stack run under" (invariant 18), read by the schedule tick, the run, the preparation and the plan preview. `BackupChainCoordinator` is backup-then-something (invariant 19): the `pre-deploy` trigger behind `stacks.setRelease(backupFirst)` / `templates.setTenantsRelease(backupFirst)`, and the `final` trigger behind `templates.removeTenant(finalBackup)`. `templates.backupAll`, `backups.getProductBackups`, `backups.setTemplatePolicy`, and a `productId` filter on `backups.events`. Manifest `formatVersion` 3 (`productId`/`productName`/`templateId`/`tenantSlug`/`releaseId`/`releaseVersion`, appended). The stage-6 owed item is paid: `RetainReleases` on `products.update` (clamped) with a field on product Settings. Frontend: the product Backups tab (`modules/backups/ProductBackupsTab.tsx`, contributed to `productDetailTabs` at order 35), provenance chips and a "use the fleet policy" reset on the stack Backups tab, the pre-rollout checkbox in the roll-out dialog, and the final-backup switch on the remove-tenant confirm. |
| `77b0f72` | **8a — end-state hardening** | No behaviour an operator can see: the asynchronous registry-resolution path, two `xmin` concurrency retries, the `ProductCatalog` savepoint seam, and `xmin` as a real `IHasXmin` property with the empty `XminConcurrencyTokenAsProperty` migration. Written up in full in the two "From stage 8a" sections below. |
| `f57f9e7` | **8b — end-state IA + the owed frontend gaps** | The fold design.md §Navigation always specified, and the last two deferrals. **Templates leaves the sidebar**: the Tenancy module now contributes an **Instances** tab to `productDetailTabs` (order 30, so CI moved 30 → 32 and the design's Overview/Releases/Instances/CI/Backups/Settings order holds), and `modules/templates/{TemplatesPage,TemplateNewPage,TemplateDetailPage}.tsx` are **deleted** — their content moved into `InstancesTab.tsx` (summary card + [Edit], add-tenant row, Management API, rollup, roster, every dialog) and `TenancyConfigForm.tsx` (the template form **minus the whole Source card**, with the `{tenant}` live preview). `/templates` and `/templates/new` redirect to `/products`; `/templates/$id` resolves through `TemplateRedirect.tsx` to `/products/$id?tab=instances`. The one-time "Templates moved here" banner on the catalogue (`localStorage`). **Template per-service backup overrides got their write side**: `backups.setTemplateServiceOverride` (contract-identical to `backups.setServiceOverride`, audited `backups`/`template.service-override.update`), `BackupTemplatePolicyDto.ServiceOverrides` on the read side, and an editor on the product Backups tab's policy card that borrows one instance's service list; `BackupOverrideMenu.tsx` is now the one override control both rungs render. **The pin pickers page**: `useProductReleases` became a keyset infinite query and both pickers grew "Show older", so a pin older than the first page is selectable again. From the review round: design.md's **Next-steps card** on the product Overview (the last unbuilt §UX element), the two-module gate that makes the Tenancy-without-Products combination behave, the override menu's consequence line, and design.md's tab cap amended 5 → 6. |

| `797569b` | **8c — the dashboard fleet view** | A user-requested addition to the end state, and **frontend only** (two files, zero backend, no new RPC). `modules/dashboard/sections.tsx` gains a **Fleets** section (order 45) rendering one card per fleet — product, tenant count, a danger `N failing` chip, the roster's rollup line and the latest release with its age — and `StacksGridSection` drops the stacks those cards represent. The join is `useFleets()`, a dashboard-owned hook both sections call: `products.list` gated on the `Products` module says which products have tenancy, one `products.get` per fleet (on the `['product', id]` key the product page already uses) says which of its stacks are tenants, and the live `stacks.list` rows supply every value rendered. Written up under "Stage 8c" below. |

| `_pending_` | **9 — adopt an existing stack as a tenant** | `templates.adoptStack(templateId, stackId, slug)`: a **standalone stack of the template's product** becomes the tenant `{slug}` while it keeps running — same containers, volumes, data, `Name`, `ComposeProjectName`, environment values, `PinnedReleaseId` and `BackupDirectory`, and **no deploy**. One new handler (`Modules/Tenancy/Handlers/AdoptStack.cs`), **no schema change**, one additive RPC method. It adds the template link, the slug, the base env vars for keys the stack does not already define, and a managed route — `IsPrimary` only when the stack had none. Frontend: an **Adopt existing stack…** action beside the add-tenant row (rendered only while the product has a standalone deployment) and `modules/templates/AdoptStackDialog.tsx`. Written up under "Stage 9" below. |

**The roadmap is complete, and so is the design.** Every stage in
[design.md](design.md#staged-roadmap) has landed, and with 8b so has every UX commitment in
design.md §UX that a later stage had deferred. What follows is the accepted debt and the invariants a
maintainer has to keep.

**Every 4b deferral landed in stage 6**, and the one that survived it landed in 8b. The mode-revert
control, the "latest ≠ branch head" warning, the per-row contextual labels and the instance-checklist
roll-out dialog shipped in 6; the pickers' fixed 20-release window is gone in 8b:

- ~~The pin pickers offer the **newest 20 releases and no "Show older"**.~~ **Resolved in 8b.**
  `useProductReleases` is a keyset `useInfiniteQuery` (`RELEASE_OPTIONS` is a page size now, not a
  ceiling) that still hands every caller the `{ releases, hasMore }` shape they already read, plus
  `showOlder` / `hasOlder` / `loadingOlder` for the two pickers. Both the stack Version dialog and the
  roll-out dialog render "Showing the newest N. Show older" **below** the select rather than inside it —
  a control in a Radix listbox that is not an option fights the keyboard and typeahead — so a pin older
  than the first page is now reachable by paging down to it. The Version dialog also gained the
  out-of-window row the roll-out dialog already had (`release #N (not loaded yet)`), because a stack
  pinned outside the window rendered the "Select a release" placeholder, which reads as "nothing is
  pinned" over a stack that is. "N behind" is unchanged and still exact only within what is loaded.

**Both things stage 6 deliberately did not ship landed in stage 7**: `RetainReleases` has its setter
(`products.update`, clamped to 5…1000, with a field on product Settings and the clamped value echoed
back into the form after a save) and the roll-out dialog has its pre-rollout-backup checkbox.

Two zero-release edges in `Releases` mode, both reachable only through a mode revert (a product is
flipped *into* `Releases` by its first release, so the state cannot occur naturally): the stack
header reads "no releases yet", and every Deploy affordance — the mobile FAB included, which reverts
to its Git-mode 52px circle — renders version-less exactly as it did before stage 4.

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
   Since stage 8a the flip's `xmin` race is **retried** rather than surfaced (see the retired debt
   entry), and the retry is written to keep this invariant rather than to route around it: the second
   attempt is again one `SaveChanges` carrying both, and a reload showing the mode already flipped
   drops the flip and inserts alone — which is the invariant satisfied, not waived. What must never
   appear here is an unconditional second statement.
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
12. **The two Actions-config contributors are independent, and each guard is separate.** `CiRepo`
    holds the registry hash, `Product` holds the release hash, and neither contributor may read the
    other's state — a registry credential rotation must not re-push a release token, and vice versa
    (design.md §"Secret sync"). Three mutations prove it: dropping either hash guard, and mixing
    `CiRepo.RegistrySyncedHash` into the release hash. Independence is also failure isolation: each
    contributor runs in its own `try`/`catch` and its own scope inside
    `CiActionsConfigSync.SyncActionsConfigAsync`, so one blowing up leaves the other's work done and
    the runner reconcile around them untouched — collapsing that to two bare awaits fails both
    `FailureIsolation_*` tests. The **retry defer is deliberately shared, and armed only by
    GitHub-call failures** (one `CiRepoRunnerStatus.ActionsSyncRetryAt`): both authenticate with the
    same PAT through the same two permissions, so the failure that actually happens fails both at once,
    and two timers would double what it costs in round-trips and give the UI two answers to "when does
    this retry". Because it is shared, a *local* failure — an unset `Watchtower:PublicBaseUrl`, a
    product with no token, an unresolvable sync registry, an ambiguous repo — must record its durable
    error **without** calling `DeferActionsSyncRetry`: no round-trip was spent, no amount of retrying
    fixes it, and parking the other contributor for five minutes over it would slow a credential
    rotation that has nothing to do with the problem. Re-writing the same message is a no-op in the
    change tracker, so the per-pass re-evaluation issues no SQL.
    `LocalFailures_DoNotArmTheSharedDefer_SoTheRegistryContributorKeepsItsLatency` and
    `AGitHubFailure_DoesArmTheSharedDefer` pin both halves.
13. **The manual token path never becomes a second-class citizen.** A hobby install with no admin PAT,
    a non-GitHub remote, or a repository with no CI must reach exactly the instructions it reached
    before stage 5 — the design is explicit that it must never hit a wall. Three rules hold it: the
    Releases tab collapses its manual instructions only when a push has *actually landed*
    (`syncReleaseSecrets && status === 'synced'`, not merely "the switch is on"), so pending and
    failed both fall back; every refusal from `ci.setReleaseSecretsSync` names the by-hand path in the
    same sentence; and the `${{ vars.WATCHTOWER_* }}` snippet is only ever rendered when those
    variables exist, because a snippet naming variables nobody set fails with an empty URL and nothing
    to point at.
14. **`SyncReleaseSecrets = true` may never outlive the CI repo it syncs into.** The FK is `SET NULL`
    on delete and PostgreSQL treats NULLs as distinct, so a stranded row is invisible to the filtered
    unique index *and* to any conflict query written as `CiRepoId == repo.Id` — enabling sync for a
    second product of the same repository would then be accepted, and the two would overwrite one
    fixed set of secret names while the UI read the surviving hash and called both of them synced.
    Two layers hold it. `ci.removeRepo` clears the flag and the state for every product syncing into
    the repo, with its own audit row (`release-token.sync.cleared`) — that is the fix at the source.
    And defensively, `CiRepoResolver.FindSyncingProductsAsync` matches on the **parsed URL as well as**
    the FK and returns a *list*: the sync pass refuses a repo with more than one candidate, syncing
    neither and recording the conflict on both, and `ci.setReleaseSecretsSync` asks the same question
    so its refusal names a stranded product the FK query could not see. Any new writer that clears a
    CI link must clear the sync flag with it.
15. **Release pruning has four protection rules, and only one of them the schema would catch.**
    `ReleasePruner` keeps the newest `Product.RetainReleases` and deletes the rest — except a release
    that a stack **pins**, that a template names as its **`DefaultPinnedReleaseId`**, that a stack
    records as its **`LastDeployedReleaseId`**, or that **any stored `DeployEvent`** references. Only
    the first is backed by a `Restrict` foreign key (and even there the guard matters: without it one
    hand-pinned tenant would make every future pruning pass of its product throw). The other three FKs
    are `SET NULL`, so a pruner that forgot one would *succeed* and silently clear a fleet default,
    blank out "what is this stack running", or empty the rollout view. All four rules, plus the clamp
    on a hand-edited `RetainReleases`, were mutation-checked one at a time in `ReleasePruningTests` —
    each mutation failed exactly one test. **"Referenced by any deploy event"** is deliberately not
    "recent": `deploy_events` has no retention of its own, so any narrower rule would be an invented
    number. The pass runs post-create inside `ReleaseIntakeService`, last, inside a `try`/`catch` that
    logs and swallows — on the webhook path the release is already committed, so a throw would 500 a
    call that succeeded.
16. **`templates.setTenantsRelease`'s two writes are one transaction.** The tenants' `ExecuteUpdate`
    and the template default's `SaveChanges` are two statements, and without a `BeginTransactionAsync`
    around them they are two *implicit* transactions — a failure between them leaves the fleet pinned
    to a release the default never got, so the next tenant provisioned joins a fleet it disagrees with.
    That is the exact state the handler exists to prevent, and it is the state a half-written pair
    produces silently. `CreateTemplate` is the in-repo pattern; the enqueues stay outside the
    transaction for invariant 9's reason (a deploy resolves its release on another connection).
    `SetTenantsRelease_RollsTheTenantWriteBackWhenTheDefaultWriteFails` forces the second write to throw
    and asserts neither half survived; removing the wrapper fails it.
17. **`StackTemplate.DefaultPinnedReleaseId` is copied at provisioning, and that asymmetry is the
    point.** It is the one field family ADR-0026 copies rather than references (invariant 5 is about
    everything else). A reference cannot express either half of what tenancy needs: a tenant given a
    hotfix pin must not drag the fleet with it, and moving the template's default must not silently
    repin a tenant somebody pinned by hand. `templates.setTenantsRelease` is what brings the fleet back
    together — it writes both halves in one call, so "the fleet is on 1.4.0" survives the next tenant.
    `Provision_TakesTheDefaultOnce_SoALaterFleetMoveDoesNotFollowIt` is the test that would fail if the
    copy ever became a read-through.
18. **There is exactly one backup policy ladder, and `BackupPolicyResolver` is it.** The rungs are
    **compose label > stack override > template policy > instance default** (ADR-0020 extended by
    design.md §"Backups across tenants"). The label rung is per *service* and belongs to `BackupPlan`,
    which has always applied it; the three below it are per *stack* and live in the resolver. Every
    consumer of `Stack.BackupEnabled` and its three siblings goes through it — `BackupScheduleJob`,
    `BackupService.ExecuteBackupAsync`/`PrepareAsync`, `PreviewPlanAsync`, `BackupStackConfigDto.From`
    — so a tenant cannot be scheduled by one reading of the ladder and prepared by another. Reading a
    `Backup*` column directly to decide behaviour is the bug the class exists to prevent.
    The stack tab's standalone-switch design additionally rests on the three instance defaults being
    compile-time constants in the resolver — inherit vs. explicit-equal-to-default is therefore
    unobservable on a standalone stack. Making any of the three configurable breaks that assumption
    and the standalone tab must become tri-state too.
    Three consequences that are easy to break separately:
    - **The four fields resolve independently.** A tenant that overrides only the quiesce mode keeps
      inheriting the fleet's schedule and enrolment. `OneOverriddenField_DoesNotDetachTheOthersFromTheTemplate`
      pins it, and so does the *write* path: the stack Backups tab posts the stack's **own** values for
      the fields a control is not touching, never the effective ones — otherwise flipping one switch
      would silently freeze all four. Verified live: flipping "include in the schedule" on a tenant
      wrote `backup_enabled` and left the other three columns null.
    - **`false` is an answer and null is silence.** A tenant that opted out by hand stays out when the
      fleet is switched on. That is also why the migration only widens the column type and rewrites no
      values (`TheMigration_OnlyRelaxesTheColumns_AndBackfillsNothing` asserts on the generated SQL):
      every pre-existing row keeps its value *as an explicit one*, so nothing an operator had configured
      started inheriting the day the feature shipped.
    - **The scheduler's SQL predicate narrows; the resolver decides.** `BackupScheduleJob` filters to
      `enabled == true OR (enabled == null AND template.enabled == true)` because it runs once a minute
      over every stack, and then re-resolves each candidate. Widening the predicate is safe (the
      resolver refuses); *narrowing* it silently drops tenants, which is what
      `ScheduleTick_EnqueuesATenantEnrolledOnlyByItsTemplate` guards.
    Per-service overrides follow the same ladder but **per service, not per knob**: a stack row replaces
    the template's row for that service outright. Per-knob merging is not expressible —
    `StackBackupServiceOverride.Exclude` is a plain `bool`, so "not overridden" and "overridden to
    false" are the same value.
19. **A chained backup gates its follow-up, and a failed one leaves a trail where the follow-up would
    have been.** The backup queue is single-flight process-wide and the deploy queue is per stack behind
    an instance-wide gate; neither can express "and then the other one", so `BackupChainCoordinator`
    holds that relationship. Four rules:
    - **The key is the backup event id**, which is exactly what coalescing collapses onto — two
      pre-deploy requests for one stack become one backup with both follow-ups attached, and both run.
    - **The step is attached inside `Enqueue`'s lock, before the job reaches the channel.** Attaching
      after the enqueue is a race: a run that fails in milliseconds (no storage configured) would finish
      before its follow-up existed, and the follow-up would then hang off a dead event forever.
    - **The follow-up is decided from the stored terminal status, not from whether the call threw.**
      `BackupService` catches its own failures and records them on the event, so the event is the only
      honest source of "did it work".
    - **A failure is visible where the missing thing would have been.** A blocked deploy writes a
      *failed `DeployEvent`* under the trigger it would have carried, naming the backup run; an aborted
      teardown writes a `backups`/`tenant.remove.aborted` audit row and the tenant is simply still
      there. Silence in either place is the failure mode.
    The corollary for `templates.removeTenant(finalBackup: true)`: it answers `removed: false` with a
    `backupEventId` and the removal happens later. Blocking the RPC until a possibly-minutes-long backup
    behind every other backup on the box finished was the alternative, and it is worse.
20. **`Stack.BackupDirectory` is stamped once and never recomputed.** It closes a hazard that predates
    products — renaming a stack orphaned its archives — and it closes it by making the directory a
    stored fact rather than a function of two mutable inputs. `BackupNaming.ResolveDirectory` is the one
    answer, read by all four sites (run, restore download, remote listing, retention), so a stack cannot
    be written to one directory and listed from another. Null means "compute it as we always did", which
    is what keeps an upgraded install's existing archives discoverable: the instance name is
    *configuration*, so no migration could have backfilled a value without guessing. A legacy row is
    stamped after its next **successful** backup — the moment the value is known to be where the bytes
    actually went — guarded both in memory and in SQL (`WHERE backup_directory IS NULL`), so a run
    holding a stale copy can never overwrite one. The visible consequence, and it is deliberate: an
    instance rename no longer moves a stamped stack's archives. Before this column an instance rename
    orphaned *every* archive of *every* stack, so the new behaviour is strictly better and never worse.

## Owed work and accepted debt

The four stage-2 review items that were queued behind the stage landed in the hardening commit
above, and stage 4a paid off the pinned-release delete guard it owed. **Stage 8a retired four more**
— the synchronous registry-resolution path, both `xmin` microwindows and the untested `ProductCatalog`
savepoint branch — on the rule that debt which is *architectural* rather than an irreducible trade-off
is paid before this design is declared the end state. Retired entries are struck through and say what
replaced them; what is left below is accepted debt, not owed work:

- **`docker.io` is always an admitted registry** for a release image, because an unqualified image
  lives there and a default install has no Hub credential to recognize it by. A leaked release token
  can therefore pin any public Hub digest; the compose-service match, the pin pre-validation and
  rollout visibility are the mitigations (design.md §Release intake spells the three accepted
  properties out, including the 401-vs-404 disclosure for *enabled* products).
- ~~**`ReleaseIntakeService.KnownRegistryHosts()` does synchronous database and file I/O** on the
  anonymous webhook path.~~ **Resolved in 8a.** `RegistryAuthBuilder`'s whole public surface is
  asynchronous — `ListResolvedRegistriesAsync` and `CreateTempConfigDirAsync`, both taking a
  `CancellationToken`, with `ToListAsync` for the registry query and `File.ReadAllTextAsync` for the
  host `docker/config.json`; `ReleaseImageValidator.KnownHosts` became `KnownHostsAsync` with it, and
  the six call sites (release intake, the pin pre-flight, `CiActionsConfigSync`'s registry
  contributor, `DeployQueueService.CreateRegistryConfigDirAsync`, `registries.list` and
  `ci.updateRepo`) all await. **No sync-over-async bridge exists anywhere on the registry-resolution
  path, and none was introduced by the changed files.** (That claim is scoped on purpose — the codebase
  still has two elsewhere, both pre-existing and both outside this feature: `AuthTokenSigner`'s
  synchronous `ValidateToken`/initialization at lines 446 and 475, and `CertificateManager`'s
  in-flight-issue join at line 475.) Two synchronous calls remain
  and are metadata probes with no asynchronous BCL counterpart — `File.Exists` on the host config
  (one `stat`, and on a containerised install the *only* filesystem call the merge makes) and
  `Directory.CreateDirectory` for the scoped `DOCKER_CONFIG` — both documented on the class. The
  host-config catch also stopped swallowing `OperationCanceledException`, which would otherwise have
  answered a cancelled intake "no host registries" and turned its registry gate into a refusal.
- ~~**The mode flip carries `Product`'s `xmin`.** A product edit landing in the microseconds between
  the read and the save makes that one call fail with a concurrency exception and write nothing.~~
  **Resolved in 8a**, by retrying rather than by weakening invariant 9. The flip-and-insert
  `SaveChanges` now sits in a loop bounded at two attempts (`MaxFlipAttempts`): the batch is atomic, so
  a lost race has written nothing, and the retry reloads the product, re-applies the flip and re-adds
  the release — one write again, not two. A reload showing the mode *already* flipped drops the flip
  and inserts alone, which satisfies invariant 9 without a second statement and keeps the
  `release.mode.change` row honest about who flipped it; a reload showing the row gone answers
  `ProductNotFound`. The retry composes with the unique-violation recovery rather than duplicating it —
  the two live in the same `try` and the concurrency clause is guarded on `flipped is not null`,
  because the release rows carry no concurrency token and nothing else in the batch can raise it.
  `ReleaseIntakeService.OnWriteStagedAsync` is the injection seam (the `PrecheckAsync` precedent),
  and `Publish_ThatLosesTheModeFlipRace_RetriesAndStillRecordsTheRelease` plus
  `Publish_WhoseRaceWinnerAlreadyFlippedTheMode_RecordsTheReleaseWithoutASecondFlip` cover both
  outcomes; both fail with `MaxFlipAttempts` set to 1.
- **`StackUpdateCheck.AvailableReleaseId` is deliberately not a foreign key.** It is a cache row; a
  value that stops matching a live release is corrected by the next check rather than by a schema
  rule that would make deleting a release harder. `AvailableReleaseVersion` is denormalized beside it
  so a stack list renders "v… available" without a join per row.
- **`AutoDeployBackgroundService` has no timing tests.** It had none before stage 4a either; what
  stage 4a added is `IsEligible` (the two release-mode rules), which is `internal` and directly
  tested. The interval and window machinery around it is still only exercised in production.
- **Turning the release sync off leaves the values at GitHub.** Watchtower stops maintaining
  `WATCHTOWER_URL`, `WATCHTOWER_PRODUCT_ID` and `WATCHTOWER_RELEASE_TOKEN`; it does not delete them —
  the same rule `ci.updateRepo` already follows for the registry secrets. Deleting them is a
  repository decision, and silently revoking a running workflow's credentials on a toggle would be
  the surprise. Rotating the token is how an operator actually invalidates what is out there.
- ~~**The release contributor's `SaveChanges` carries `Product`'s `xmin`.** A product edit landing in
  the microseconds between the read and the stamp makes that pass throw
  `DbUpdateConcurrencyException`, which the contributor's own isolation catches and logs.~~
  **Resolved in 8a.** `CiActionsConfigSync.StampReleaseSyncAsync` retries the stamp once against a
  reloaded row (`MaxStampAttempts`), re-applying the three fields after the reload because a reload
  overwrites them. It is worth a retry precisely because the values are *already at GitHub* by then:
  leaving it to the next pass means re-sealing and re-pushing three values over the network to write a
  row this pass can write from a second read — and doing it minutes later, because the push is what
  the shared defer rate-limits. Stamping the hash this pass pushed stays correct even when the
  concurrent edit rotated the token: the hash describes what went out, and the next pass computes it
  from the *current* token, sees the difference and re-pushes.
  `Release_WhoseStampLosesToAConcurrentProductEdit_RetriesAndStillStamps`
  forces the conflict through the GitHub stub — no production seam
  was needed, because the window is exactly the round trip the stub stands in for — and fails with
  `MaxStampAttempts` set to 1.
- **Any repository-URL edit turns the release sync off rather than following the URL** (an ordinal compare: a move, but also a `.git` suffix or case fix — audited and one click to re-enable). `products.update`
  clears the flag and the state, and the audit line says so. Following the move would mean re-probing
  a PAT that may not exist for the new repository, from inside a handler whose job is a product edit;
  re-enabling on the CI tab does that probe where its failure has somewhere to be shown.
- **Only one product per repository can sync**, enforced by the filtered unique index, reported in
  words by the handler, and refused outright by the sync pass when it sees two candidates (invariant
  14). design.md's v2 answer is name-suffixed secrets; until then the second product of a monorepo
  uses the manual path, which the refusal message names.
- **The registry contributor's unresolvable-registry failure no longer arms the retry defer.** That is
  a deliberate change to shipped behaviour, forced by the timer now being shared: leaving it would let
  a missing registry credential park the release contributor for five minutes. It is a local check
  with no GitHub call, and the repeated `SaveChanges` writes nothing because the message is unchanged,
  so the only difference is that the message is re-evaluated once per pass instead of once per five
  minutes.
- **`ci.removeRepo` turns the sync off but does not delete anything from GitHub**, same as the manual
  off-switch. The audit row says so; rotating the token is how an operator invalidates what is out
  there.
- ~~**`ProductCatalog`'s savepoint retry branch is untested** — forcing the interleave needs an
  injection seam inside `FindOrCreateAsync`, which is more test scaffolding than the branch is
  worth.~~ **Resolved in 8a.** The seam is `protected virtual OnInsertStagedAsync`, the same shape
  `ReleaseIntakeService.PrecheckAsync` already had for the same reason, and the scaffolding turned out
  to be a subclass and a helper. It sits *after* `UniqueNameAsync`, so it covers the race the unique
  index catches — the narrower window before the name is derived is a **separate, still-unguarded**
  race, now recorded as its own debt entry below.
  `FindOrCreate_ThatLosesTheNameRace_RollsBackToItsSavepointAndAdoptsTheWinner` runs inside a
  real transaction — the caller shape, and the only shape in which the savepoint exists at all — and
  asserts the loser adopts the winner *and* that the transaction is still usable afterwards, which is
  what the savepoint rather than a bare catch buys. Disabling the catch fails it with the raw
  `DbUpdateException`. `ProductCatalog` is no longer `sealed`, which is what the seam costs.
- **The Tenancy UI now requires the Products module, and with `Products` off a tenancy install has no
  tenancy screens at all.** This is new in 8b and is the price of the fold: every surface the Tenancy
  module has is *inside* a product page now — there is no `/templates` list, no `/templates/$id`, and the
  Instances tab has no page to be a tab of. The combination is handled rather than left to fail: the
  `productDetailTabs` contribution carries its own `when: { module: 'Products' }`, ANDed with the
  manifest's `{ module: 'Tenancy' }` (a contribution's `when` is ANDed with its module's — that is how
  the kernel expresses a two-module condition), so the tab is *absent* rather than
  registered-and-unreachable; and all three `/templates*` routes run a `Products` guard after the
  `Tenancy` one, so a deep link goes straight Home in one hop instead of bouncing off `/products`
  on its way there. **Operator guidance: enable `Products` wherever `Tenancy` is enabled.** Tenants
  themselves are unaffected — they are stacks, they keep running and they stay on `/stacks` — and the
  whole `templates.*` RPC surface is untouched, so the public Management API and any script keep working.
  Not closed properly because closing it means either keeping a second copy of the tenancy UI outside the
  product page (the duplication the fold exists to remove) or making the backend refuse the
  configuration, which is a module-dependency mechanism Elarion does not have and which this feature
  should not invent.
- **Two concurrent implicit creates over one source, interleaving *before* the name is derived, produce
  two products for that source.** Pre-existing (it predates 8a and is not something 8a introduced or
  scoped), and it is the price of there being **no unique index on the normalized source** — which is
  deliberate: the catalogue allows several products over one repository (different compose files), and
  the source key is a normalization computed in C# that no index can express. If the rival commits
  before `UniqueNameAsync` runs, the loser derives `name-2`, hits no constraint, and both rows land.
  The window is narrower than the one 8a closed (it ends at the name query rather than at the insert)
  and the mitigation is that it is the *only* way to reach the state: `products.create` and
  `products.update` both refuse a duplicate source through `FindConflictAsync`, so no single operator
  action produces it — `FindConflictAsync` is itself check-then-act with no index behind it, so two
  *concurrent* explicit creates share the same residual window. The result is also visible and repairable rather than silent — two catalogue
  rows over one repository, fixed by pointing the stacks at one of them and deleting the other. Closing
  it properly means an index on a stored normalized-source column, which is a schema change and a
  backfill, and belongs with whatever next needs that column rather than to a hardening pass.

### From stage 9

- **Detach — the reverse of adoption — is out of scope in v1.** `templates.adoptStack` makes a
  standalone stack a tenant; nothing makes a tenant standalone again except deleting the whole tenancy
  setup, which detaches every tenant at once (`TemplateId` SET NULL, and they keep running because
  they hold their own `ProductId`). A per-tenant "leave this setup" is future work rather than an
  oversight: the mechanics are one column write, but the *decision* it owes is what happens to the
  managed route the adoption created — deleting it takes a live hostname down, keeping it leaves a
  `Managed` route under a pattern nothing owns any more, and asking is a third dialog on a screen the
  Übersichtlichkeit audit is already watching. `templates.removeTenant` is not the answer either: it
  tears the containers down, which is the opposite of what "detach" means. Whoever needs it should
  decide the route question first and build the column write second.

### From stage 7

- **A chain does not survive a process restart.** `BackupChainCoordinator` holds its pending steps in
  memory, like both queues: a Watchtower that dies between a backup finishing and its deploy being
  enqueued loses the chain, and the backup event is the durable record that the backup happened. That
  is the same guarantee the deploy queue already gives (a queued deploy does not survive a restart
  either); buying more means a durable job table, which this feature does not justify. The blast radius
  is one un-run deploy, or one tenant that was going to be removed and now is not — both re-triggerable
  by hand, and the second is the safe direction to fail in.
- ~~**Template-level per-service overrides have a table, an entity and a resolver rung, but no UI.**~~
  **Resolved in 8b**, built as the "honest v2" this entry described: a per-service editor on the
  template that **borrows one instance's service list**. `backups.setTemplateServiceOverride` is the
  writer, and it mirrors `backups.setServiceOverride` field for field on purpose — the whole override is
  replaced, an omitted knob is cleared, and clearing every knob deletes the row — because two setters
  that agreed about the values but disagreed about what an omitted field means is exactly how the ladder
  starts lying, and the two forms that post them are now literally the same control
  (`modules/backups/BackupOverrideMenu.tsx`, extracted from the plan preview and shared; the only
  difference is whether the "not set" row is called *Stack default* or *Fleet default*, and which knobs
  a compose label has locked). Three things make the borrowed list honest, and each of them is a way
  this could have shipped wrong:
  - **A row's current value is the template's own** (`BackupTemplatePolicyDto.ServiceOverrides`, new and
    additive, every entry `inherited: true`), **never the borrowed preview's `override`**. A preview
    shows the *donor's effective ladder*, so a donor that overrides a service itself would hide the
    fleet's setting behind its own.
  - **A stored row whose service is absent from the borrowed list still gets a row**, marked "not in the
    listed instance". Otherwise a setting for a service that was renamed away becomes unreachable.
  - **The donor is named and pickable, and the footer only claims a borrow that happened.** With no
    reachable daemon (or an instance that never deployed) the preview yields nothing, the stored rows are
    still listed, and the line says so instead of crediting an instance it could not read.
  The empty state is design.md's: "Add/Start a tenant to see its services — or set overrides as compose
  labels, which win anyway." **One shipped behaviour changed with it**: the *stack* plan preview's
  override menu used to open over an inherited row pre-filled with the template's values, so a single
  click wrote a stack row that silently copied them. It now opens on "no stack override", because
  precedence is per service and not per knob (invariant 18) — a stack row replaces the template's row
  whole, and pre-filling invited the reader to think they were editing a merge. It is the same tri-state
  trap the stage-7 review round caught on the four switches, one rung down. **The reader is told, where
  they act**: the menu carries "Overriding here replaces the fleet's whole setting for this service."
  above its controls, and only on a row that is actually inherited — on a row with nothing above it the
  sentence would be true of nothing.
- **`products.deployRelease` does not offer a pre-deploy backup.** The checkbox is the roll-out
  *dialog's*, and the dialog applies through `stacks.setRelease` / `templates.setTenantsRelease`, which
  both take `backupFirst`. `products.deployRelease` is the "Deploy latest" button — one click, no
  dialog, no consequence sentence — and bolting a hidden backup onto it would make a button that
  currently returns in milliseconds take minutes with nothing on screen saying why. An operator who
  wants the guarded rollout opens the dialog.
- **The backup fan-out is serial and the UI can only say so, not estimate it.** `templates.backupAll`
  and a `backupFirst` rollout both queue N runs onto the single-flight queue. The dialog and the toast
  state "backups run one at a time" (design.md §Risks, open question 12); neither predicts a duration,
  because that needs a per-stack size history nothing records.
- **`BackupService.StampDirectoryAsync` is the only part of the run path tested directly.** Driving
  `ExecuteBackupAsync` end to end needs a Docker daemon and a storage provider, so the stamp's two
  guards are tested on their own and the *call site* (after success, never after a failure) is verified
  by reading rather than by a test. The live pass exercised the failure direction for real: nine failed
  runs against an unreachable daemon left every `backup_directory` untouched. **Re-examined in 8a and
  kept**: `BackupTestDoubles` carries a recording *queue* and nothing that stands in for a run, and the
  half that matters — stamped *after success* — needs the whole of `RunAsync` (volume listing, a
  container-exec tar stream, an upload) to succeed, which is a fake daemon and a fake storage provider,
  not a double. The failure half is reachable cheaply (a stubbed daemon answering "no volumes" makes
  `RunAsync` throw), but pinning only the direction the live pass already exercised nine times would be
  a test that cannot fail for the reason the entry is about.

### From stage 8a — the synchronous-I/O sweep

The async-ification above came with a sweep of the feature's new code (`Modules/Products`, the release
parts of `Modules/Ci`, `Services/Release*`, `Services/CiActionsConfigSync`, the release webhook,
`BackupChainCoordinator`, `BackupPolicyResolver`) for synchronous I/O on a request or reconcile path.
Everything it found is listed here, so a later reader does not have to redo it:

- **Fixed:** the whole `RegistryAuthBuilder` surface and its five call sites (the entry above).
- **Not I/O at all:** every `.ToList()` / `.FirstOrDefault()` the grep flags in `products.list`,
  `products.get`, `products.retryFailedRollout` and `ReleasePruner` runs *in memory* over a list a
  preceding `ToListAsync` already materialized — the projection-then-shape pattern those handlers use
  deliberately, so the enum-to-string and bucket rules are not asked of the database. The webhook's
  `buffer.Write(chunk, 0, read)` writes to a `MemoryStream`; the read beside it is `ReadAsync`.
- **Kept, with reasons:** `DeployQueueService`'s `RecordEventRelease` and `RecordDeployedRelease` (the
  two stage-4a stamps) issue a synchronous `ExecuteUpdate`. They run on a **thread-pool thread taken by
  `Task.Run` in `Enqueue`, bounded by the `Watchtower:MaxConcurrentDeploys` slot gate** — so the block
  is on a thread that is already dedicated to one deploy and is capped instance-wide, not on a request
  thread and not in the reconcile loop — and they are two of five identically shaped incremental
  writers in that file (`RecordDeployedCommit`, `DeleteUpdateCheck`, `UpdateDeployStatus` are the
  others, all older than this feature). Making two of the five asynchronous buys nothing measurable on
  a thread that is about to shell out to `docker compose`, and costs the property that makes them
  readable — that they all look the same. If that file is ever converted, it is converted as a whole.
- **`BackupChainCoordinator`'s two `lock`s** hold only dictionary mutation and a copy-out; no `await`
  is possible inside a `lock`, which is the language enforcing what the design already wanted.

### From stage 8a — `xmin` became a real property

Both retries above are `xmin` races, which put the token itself under review — and the review found the
codebase on the wrong side of the provider maintainer's own reasoning. `xmin` was mapped as an EF
**shadow** property, on the argument that the database's bookkeeping should not be readable from the
domain model. That is the argument roji rejected when removing `UseXminAsConcurrencyToken`
(npgsql/efcore.pg#3539): standard `IsRowVersion` already covers the case, and a shadow property makes
the change tracker the *only* holder of the value — so it does not survive detaching, attaching or
serializing, and every read-detached / mutate / attach flow fails as a phantom conflict against a
`default(uint)` that matches no row.

**Watchtower had hit that twice and worked around it twice, in comments rather than in code.** `User`
carries Identity's `ConcurrencyStamp` instead of `xmin` because `WatchtowerUserStore` is attach-based,
and `CiRepo` was left with *no* token at all so `CiToolchainRecorder` could attach a no-tracking read —
the attach guard exists **because** `CiRepo` could not safely be given one.

What changed:

- A `uint Xmin { get; private set; }` on each of the six entities that carry the token — `Realm`,
  `Product`, `Stack`, `Route`, `Group`, `ProxyCertificate` — declared through a new
  `IHasXmin` marker interface. **Interface rather than six loose properties, deliberately:**
  `XminConcurrency.UseXminAsConcurrencyToken` is constrained `where T : class, IHasXmin` and maps
  `e => e.Xmin`, so an entity configured with the helper but missing the property is a *compile error*
  — where the shadow version would silently have created a second, unread property. The private setter
  keeps the half of the old argument that was worth keeping: application code still cannot write a
  token. Reading one is harmless and now possible.
- **The helper's remarks state the reversal and cite the issue**, so the next reader meets the reasoning
  rather than re-deriving it. The two guard comments were softened truthfully: `CiToolchainRecorder`'s
  now says the attach flow *would survive* a token on `CiRepo`, because the value travels with the
  instance; `CiRepoResolver`'s says what is still true — that `IHasXmin` fixed detach-and-attach and
  fixes nothing about an `ExecuteUpdate` bumping a row behind the change tracker, which remains its
  constraint. `UserConfiguration`'s comment now rests on "Identity already models this as a column and
  two tokens on one row is two ways to be refused" rather than on a hazard that no longer exists.
- **One migration, `XminConcurrencyTokenAsProperty`, empty of operations** — verified: both `Up` and
  `Down` contain no calls. `xmin` is a PostgreSQL *system* column, in no `CREATE TABLE`, so renaming the
  property that maps to it changes nothing a migration can express, and `has-pending-model-changes` was
  in fact already clean *without* it. It exists for the **model snapshot**, which records property
  names: without it the snapshot keeps describing a shadow property that no longer exists and the next
  real migration is diffed against a model that is subtly not this one. The file says all of that in its
  own remarks, because an empty migration is exactly the kind of thing a later reader deletes as dead
  weight. The snapshot diff is **exactly** six `Property<uint>("xmin")` → `("Xmin")` renames plus one
  `Xmin = 0u` line in the seeded system realm's `HasData` — and that last line is inert, which is worth
  saying because it looks alarming: the property is `ValueGeneratedOnAddOrUpdate`, so EF excludes it from
  insert operations, the initial migration's `InsertData` for `realms` still lists six columns and no
  `xmin`, and the differ emitted no seed operation. The proof is mechanical rather than by reading —
  every test database is built by `db.Database.Migrate()` (`PostgresTestServer`), so the whole suite runs
  against a database freshly scaffolded through this migration with that realm seeded.
- **No wire impact.** None of the six entities is a `[JsonSerializable]` root and none is reachable from
  one — every entity reaches the wire through an explicit projection — so nothing exposes a transaction
  id. `rpc-schema.json` is byte-identical, which is the mechanical proof.

The two existing concurrency tests are **unchanged and still green**, which is the equivalence evidence:
the token behaves exactly as it did. The new one,
`AnXminEntity_ReadDetachedAndAttachedElsewhere_SavesWithoutAPhantomConflict`, is the roji scenario end to
end — read `AsNoTracking` in a scope that is then disposed, assert the token survived on the object,
attach to a second context and save — plus a third assertion that a genuinely stale copy is *still*
refused, so this bought detach-and-attach without quietly buying last-writer-wins.

**Mutation testing**, four mutations and four catches: `MaxFlipAttempts` set to 1 fails both mode-flip
tests, `MaxStampAttempts` set to 1 fails the stamp test, disabling `FindOrCreateAsync`'s
unique-violation catch fails the savepoint test with the raw `DbUpdateException`, and putting `xmin`
back on a shadow property (`Ignore(e => e.Xmin)` plus the old `Property<uint>("xmin")`) fails the new
detach test on the surviving-token assertion while leaving the two older concurrency tests green.

**Verification at hand-over** (from the worktree root): clean `--no-incremental` Release build with
**0 warnings**; `dotnet test` 38 failures out of 2064 — the same families this document records, with
one *fewer* than the 39-failure baseline because the flaky `AuthEndpointTests` audit-ordering case
happened to pass (17 `CertificateStoreTests`, 11 ACME across the same four classes, 4
`CertificateManagerTests`, 2 `CertificateManagerProjectionTests`, and one each of
`BackupPlanOverrideTests`, `ProxyIngressEndpointReloadTests`, `ProxyChangeSignalTests`,
`FileStateImportTests`) — no new failures; `has-pending-model-changes` clean (run it with
`--configuration Release --no-build`, see the env notes) after the empty
`XminConcurrencyTokenAsProperty` migration; **`rpc-schema.json` byte-identical** (the async-ification
moved no wire shape — `ListRegistries` and `ci.updateRepo` only changed how they read, not what they
answer — and `Xmin` reaches no DTO); `npm run build` green with no frontend file touched.

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
  `dotnet ef migrations has-pending-model-changes` defaults to a **Debug** build, which fails outright
  while a `dotnet run` instance from a live-verification session still holds
  `bin/Debug/net10.0/Watchtower.Application.dll`. Pass `--configuration Release --no-build` and it runs
  against the build the other three gates already made.
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
  `javascript_tool` (`.click()`, native value setters) works and is what stage 3 used. Stage 4b's
  recipe, which worked end to end: **podman, not docker** on this host
  (`podman run -d -p 55433:5432 … postgres:17-alpine`), then
  `WATCHTOWER__DATABASE__CONNECTIONSTRING=Host=localhost;Port=55433;… Auth__Enabled=false dotnet run
  --project src/Watchtower.Api -c Release --no-build`, then `npm run dev`. Two things worth knowing
  next time: **the API's JSON omits nulls**, so every new nullable DTO field arrives as `undefined`
  and client code must use `!= null` rather than `=== null`; and **error toasts render
  `role="alert"`, not `role="status"`** — a poll for `[role=status]` silently misses every failure
  message. Registry-backed paths (release intake's tag→digest resolution and the pin pre-flight) work
  against real `docker.io` images without a Docker daemon, so a release seeded with `nginx:1.27-alpine`
  exercises them for real; only the deploy itself fails, which is fine for UI work. **The dev server's
  API proxy target is hard-coded to `http://localhost:5080` in `vite.config.ts`** (stage 8b): run the API
  on that port, or point a throwaway `--config` at another one. Setting `VITE_API_URL` instead makes the
  client call the API cross-origin, which the backend does not allow, so every request dies on a CORS
  preflight and the app boots with "everything off" — a login page over an install with auth disabled is
  what that looks like.
- **Tests isolate themselves from the host docker config**: a module initializer in both test
  projects points `WATCHTOWER_DOCKER_CONFIG` at a nonexistent directory, because any environment
  logged into a registry (the GitHub Actions runner ships a `docker.io` credential) otherwise leaks
  real usernames into registry-resolution assertions — a failure that only shows on CI.
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
- **Built in 4b** (everything below except the mode-revert control, which is still owed — see the
  note under the table): the Version panel, the pin dialog, the tracking chip and behind-badge, the
  `OnChange` creation default and the relabelled `AutoDeployMode` options.
- **`DeployEventDto` carried no release** through 4b; **stage 6 widened it** with `releaseId` and
  `releaseVersion`, projected through one left join in `stacks.events` rather than a lookup per row.
  The chip is on the history rows now.

### Stage 4b — the three judgement calls, and why

- **The mobile FAB.** The header invariant covers it, and a fixed icon-only circle outlives the
  header line it would have to be read next to. In `Releases` mode the FAB grows into a pill reading
  `Deploy 1.4.0`; in `Git` mode it stays the 52px circle it has always been, because Git mode changes
  nowhere and its version has never been on the FAB. Verified at 375px: 140×52, no page overflow.
- **The transition line** ("This product now has releases…") needs "exactly one release", and the
  page already fetches the product's newest 20 for the pin picker — so it is
  `releases.length === 1 && hasMore === false`, exact, and **no backend field was added**. It clears
  itself when the second release lands.
- **The creation default.** `/stacks/new` exposes no automation selector, so the `OnChange` default
  for a `Releases`-mode product is applied to the create request and *stated* in the Source card
  ("New releases deploy automatically. Change this in the stack's settings.") rather than left
  invisible. Changing it is stack Settings' job, as it always was.
- One presentation note: versions are rendered **exactly as CI reported them** (`1.4.0`), not
  prefixed with a synthetic `v`. design.md writes `v1.4.0` illustratively; the Releases tab has
  always shown the raw string, and inventing a prefix would make the UI disagree with the release the
  operator named.
- **"N behind" is honest about its window.** It counts the pin's index in the fetched page, so a pin
  older than 20 releases renders a bare `behind` chip rather than a guessed number. And it is
  `newest.id != pinnedRelease.id`, not merely "an available release exists" — otherwise saving a pin
  without deploying would immediately nag about the version just chosen.

**Invariant 6 is enforced on five surfaces, not one**, and they must not drift apart:
`StackDetailPage`'s header fragment and mobile FAB, the stack Overview's containers empty state and
Version-panel Deploy, `StacksPage`'s version chip (table + mobile card), and the dashboard's
`StackCard`. All six labels come from `deployTargetVersion`; the three list surfaces call it with no
release list, which is exactly the DTO-only fallback chain the function documents, so **no list page
gained a request**. `StacksPage`'s column header is `Branch` until one release-mode stack is listed
and `Version` after — an all-Git install still sees the page design.md §Migration morning-after
promises is unchanged.

**One source for "what is newest", deliberately.** `newestRelease` prefers the live release list over
`StackUpdateCheck`'s cached `availableRelease*`, because that cache is only rewritten by the periodic
check: between a CI publish and the next tick the DTO still describes the previous world. The stack
page reads both (the header had the list, the panel had the DTO) and would otherwise contradict
itself — with no "Check now" to escape with, since Releases mode deliberately has none. Anything new
that answers "is there something to deploy" must go through that function rather than read
`availableReleaseId` directly.

**The dashboard's update badge is mode-aware.** `hasUpdates` is the only field that means the right
thing in both modes; `outdatedImages` is empty by construction in Releases mode and `newCommitSha` is
informational there (unreleased commits, which no redeploy picks up), so summing them badged an
up-to-date release-mode stack "1 update".

## Stage 5 handoff — secret sync

**Where the pass lives now.** `CiRunnerOrchestrator` no longer contains a sync method; it calls
`CiActionsConfigSync.SyncActionsConfigAsync(repo, status, ct)` once per repo, beside
`ReconcileWarmerAsync` and `ReconcileRepoAsync`. Anything that wants a third contributor adds it
there, as its own explicit try-block with its own hash, scope and literal log template — the two existing contributors in `SyncActionsConfigAsync` are the whole
extension point. The extraction is also what made the pass testable: it takes a `CiRepo` and a
`CiRepoRunnerStatus` and nothing else, so `CiActionsConfigSyncTests` drives it directly with a
`GitHubApiClient` stub holding a real libsodium keypair and asserts on what came out of the sealed box.

**Why the toggle is `ci.setReleaseSecretsSync` and not a field on `products.update`.** The module
boundary follows `ci.enableForProduct`, which is likewise product-scoped, likewise writes product
columns and likewise audits under `ci` — what is being configured is the *repository's* Actions
configuration, whose other contributor (`ci.updateRepo`'s registry selection) and whose read model
(`ci.getProductCi`) already live in the Ci module. It is a separate method rather than a field because
enabling is four fallible steps (resolve the CI repo, refuse the monorepo conflict, probe the PAT,
mint a token when there is none), and folding those into the product edit form would make an unrelated
rename fail on a PAT problem.

**The state is on `CiLinkDto`, not on `ProductDto` and not on `CiRepoDto`.** Not `ProductDto` because
of the stage-2 precedent — the CI tab already has a query and the catalogue lists every product. Not
`CiRepoDto` because the state is *per product*: the runner pool is shared between products of one
repository, the release token never is, and `ci.listRepos` has no product to answer for. The three new
fields are `syncReleaseSecrets` (bool), `releaseSecretsSync` (`{status, syncedAt, error}` or null — the
same shape and the same null rule as `registrySync`) and `releaseSecretsSyncBlocked` (a sentence, or
null when the sync is possible). `ci.getStackCi` forwards to `ci.getProductCi`, so it carries them too.

**One string, one owner.** `GitHubApiClient.MissingActionsPermissionMessage(feature, permission)` is
public precisely so tests assert the production wording instead of a stub's paraphrase — the stale
"the registry sync needs…" survived into the release path exactly because a stub had invented its own
copy. `ValidateSecretsAccessAsync` now takes the feature name (`CiActionsConfigSync.RegistryFeature` /
`ReleaseFeature`, the same constants `Explain` uses for its 403 hint), so each caller's error names
itself.

**What wakes the loop.** `ci.setReleaseSecretsSync` (both directions), `products.rotateReleaseToken`
when the product syncs, and `products.update` when it clears a standing failure — each clears the hash
*and* the stamps *and* the error, then `ClearActionsSyncBackoff(repoId)` + `RequestReconcile()`. The
hash has to go with the error: a standing failure whose hash still matches the current values means the
push itself failed, so clearing only the message would satisfy the "unchanged, nothing to do" guard and
quietly stop retrying. `products.rotateReleaseToken` also gained `resyncing` on the wire, so the toast
can say "on its way to GitHub" instead of "go and paste it somewhere".

**Two schema notes for whoever touches `products` next.** The filtered unique index needed *both*
declarations spelled out in `ProductConfiguration` — declaring only the filtered one suppresses the
convention index EF creates for the FK (the convention fires only when nothing already indexes those
properties) and the plain `ix_products_ci_repo_id` disappears from the model with no diagnostic. The
second declaration also needs a model name *and* a `HasDatabaseName`, because the snake-case convention
derives the database name from the columns alone and both would otherwise collide. Getting either wrong
produces a migration that silently drops the FK's lookup index — check the generated `Up` for a
`DropIndex` before accepting it.

**Live verification** (podman + API + Vite, the stage-4b recipe, on port 55434/5080/5175) covered the
four states the design cares about, all with real wire data: `synced` (badge, "Last synced 5m ago",
Releases tab collapsed to the cross-link line, snippet on `${{ vars.WATCHTOWER_URL }}` /
`${{ vars.WATCHTOWER_PRODUCT_ID }}`), `failed` (badge plus the server's message verbatim in the
danger banner, and the Releases tab correctly back on the manual instructions with the literal URL),
off-but-available (manual instructions plus the quiet "Watchtower can place it for you" line), and
blocked (manual instructions, no cross-link, exactly the pre-stage-5 card). The PAT probe was also
exercised against real github.com with a bogus token: `ci.setReleaseSecretsSync` refused with "The PAT
is invalid or expired" and named the by-hand path in the same sentence. No console errors.

## Stage 6 handoff — tenant release policy

**Where "which instance runs which version" is answered, exactly once.** `lib/release.ts` gained the
roster half of the derivations it already held for the stack page: `versionBucket` / `versionRollup`
(the "18 on latest · 2 pinned · 1 behind" line) and `rosterVersion` (a row's version, `pinned` and
`behind`). They take a narrow `VersionState` — `{trackingMode, pinnedRelease, lastDeployedRelease}` —
rather than a `Stack`, and the backend now puts that same wire shape on **three** DTOs: `StackDto`,
`ProductStackDto` and `TenantDto`. Spelled three times in C# on purpose: modules do not reach into
each other's contracts (ELMOD002), and a two-field projection is not worth a shared module to own it.
One TypeScript interface reads all three. Anything new that renders a version cell or a rollup goes
through those functions rather than re-deriving `pin ?? deployed`.

**The roll-out dialog has two apply paths, and the checklist decides which.**
`components/set-release-dialog.tsx` is opened from two modules (Instances roster, Releases tab row
action), which is why it is in `components/` and not in either. Selecting **every** row when a `fleet`
is given runs one `templates.setTenantsRelease` — pins written *and* the template default moved, one
round trip for any fleet size. Selecting a **subset**, or having no fleet at all, runs
`stacks.setRelease` per row and leaves the default alone: that is the canary and per-tenant-hotfix
case. The consequence sentence names which one is about to happen, because the difference — whether
the *next* tenant joins where the fleet is — is invisible otherwise. The Releases tab only passes a
`fleet` when every deployment of the product is a tenant of the one template; a product that also has
standalone stacks would otherwise have them silently missed by a "select all" that looked like it
covered them.

**The two design decisions this stage had to make:**

- **"latest ≠ branch head" reads what is already polled.** `products.get` gained
  `unreleasedCommitSha`, derived server-side from `StackUpdateCheck.NewCommitSha` — which release mode
  keeps deliberately informational for exactly this — and compared against the latest release's
  `commitSha` (also newly on `ProductReleaseSummaryDto`). **No network call on a read path**: a
  `git ls-remote` per product page load was the alternative and was refused. Only stacks that track the
  product's *own* branch are consulted, because a staging stack on `develop` polls a different head.
  It is therefore a lower bound and honestly so — a product whose stacks are all on overrides, or which
  has never been polled, reports null rather than guessing. And it is a **sha, never a count**: "2
  commits on main since v1" needs a clone, so the UI names both shas and does not invent the number.
- **The row action's label compares against the fleet, not the release list.** `rowAction` in
  `ReleasesTab.tsx` takes each deployment's own position (`pin ?? lastDeployed`, the same rule every
  version surface reads) and labels the row against the newest of those: newer → "Deploy this release",
  older → "Roll back to this release", equal or a fleet that is nowhere yet → the neutral "Set this
  release". Comparing against the newest release that *exists* would describe the version list instead
  of the consequence of the click — a product with three unrolled-out releases is still on the first.

**`products.retryFailedRollout` targets less than "the failures".** It folds to the newest event per
stack (a stack that failed and was then fixed is not a failure), and then excludes two kinds it would
otherwise lie about: a stopped stack refuses deploys, and a stack now pinned to a *different* release
would deploy its pin rather than this release, because deploys are convergent. Both come back as
`skipped` with the count, and the toast says why — verified live: retrying a rollout whose one failed
tenant had since been repinned reported "Retrying 0 deploys… 1 skipped".

**The rollout view is honest about its two halves.** Rows with a `DeployEvent` are history. Rows
without one are reported as skipped with the reason the stack's state gives *today* — the fan-out
deliberately records nothing per stack it did not target (that is what keeps a 200-tenant release from
writing 200 rows of noise), so "pinned" can mean "pinned since". The remark on `ReleaseRolloutDto` is
the contract; do not let a later change quietly present it as enqueue-time truth.

**One schema note for whoever touches `stack_templates` next.** The migration is clean and additive
(no stray `DropIndex` — the stage-5 trap does not apply, because nothing else indexed
`default_pinned_release_id`), but the FK is `SET NULL` where `Stack.PinnedReleaseId` is `Restrict`, and
that asymmetry is deliberate: clearing a default for *future* tenants changes no running deploy. What
must not clear it is pruning, which is a rule in `ReleasePruner`, not a schema one.

**Live verification** (podman + API + Vite on 55435/5080/5176, the stage-4b recipe) exercised every
surface with real wire data and real `docker.io` digest resolution: two releases recorded against
`nginx:1.27-alpine`/`1.26-alpine`; `setTenantsRelease` pinning four tenants and the template default;
a fifth tenant provisioned **and coming out pinned to the fleet default with no operator action**; the
Instances roster's Version column (`1.3.0` `pinned` `behind`), its rollup line and its bulk button; the
roll-out dialog's checklist, both consequence sentences (full selection vs subset) and a live apply
that moved four pins and the default to 1.4.0; the Releases tab's contextual labels flipping to "Roll
back to this release" once the fleet moved past a release; the expanded row's "1 failed · 3 not
deployed" summary and its Retry failed; the product Overview warning; the Settings mode-revert control
including a full `releases → git → releases` round trip; and the deploy-history version chips. Flipping
the product to Git mode removed the Version column, the rollup and the bulk button from the roster —
invariant 4 on a surface that did not exist before. No console errors.

Two defects were found by the live pass and nothing else, both in the dialog: the consequence sentence
read *"4 instances will go back to tracking latest and deployed"* (the clause only parses after the
passive half), and the fleet dialog opened on **Track latest** over an already-pinned fleet, which made
Apply an accidental unpin. Both fixed; the second is why the roster passes
`seedReleaseId={template.defaultPinnedRelease?.id}`.

**Mutation testing** (the habit the doc asks for, and it earned its keep again). Twelve mutations, all
caught: the four pruning protection rules dropped one at a time, the retention clamp removed, retry
targeting widened to every stack, retry's two exclusions dropped, retry folding to the *oldest* event
instead of the newest, `setTenantsRelease` not writing the template default, not writing the tenants,
and deploying stopped tenants, and `TenantProvisioningService` not copying the default. Two real bugs
were caught by the new tests before that: `GetReleaseRollout` used `ToDictionary` where a stack can
legitimately have several events for one release (threw on a redeploy), and the intake pruning test
first used a `ghcr.io` image the test host's registry gate refuses.

### Stage 6 review round — what changed after the first pass

Two of these were real bugs a reader would have hit; the rest are the kind of thing that only shows up
when someone asks "and what happens when that lookup misses?".

- **The roll-out dialog announced the opposite of what Apply did.** Its consequence sentence and its
  toast both branched on whether a *version string* could be found in the newest-20 window — so on two
  entirely normal paths (the window still loading, and a pin older than 20) they read "will go back to
  tracking latest" over a dialog whose Apply pins. Everything now branches on `pin`, and an unknown
  version renders as `release #N (outside the loaded list)` — or bare `release #N` while the list is in
  flight, because "outside the list" is not yet true. The select trigger gets a row for the
  out-of-window pin too, so it names the pin instead of showing its "Select a release" placeholder.
  Both cases were verified live (a 22-release product for the window case, a delayed `listReleases`
  fetch for the load case).
- **`templates.setTenantsRelease` wrote its two halves in two implicit transactions** — now invariant
  16, with a test that forces the second write to throw.
- The pruner's delete inside `PublishAsync` now sits behind its own savepoint (`PruneSavepoint`),
  mirroring `InsertSavepoint`: swallowing its exception inside a caller's transaction would otherwise
  poison that transaction while intake reported `Created`.
- `templates.list` and `templates.update` were missing `Include(t => t.DefaultPinnedRelease)`, so both
  reported "no default" over a template that had one. `TemplateReads_AllProjectTheFleetDefault` covers
  all three read paths at once.
- **A zero-instance template can now set its fleet default from the dialog.** The backend always
  supported it (`SetTenantsRelease_WithNoTenants_StillWritesTheDefault`), and the "No instances yet…"
  sentence was already written — but Apply was disabled, so the sentence was dead code.
- **The Settings tab sent `releaseMode` on every save**, including saves that never touched it. Since
  the mode is also flipped from outside the form (the first release published), an unrelated save
  minutes later silently reverted a flip that had already landed.

  The first attempt at this gated on a *snapshot diff* (`form.releaseMode !== product.releaseMode`) and
  was still wrong, in the same direction: `form` is seeded once at mount and this component never
  remounts on a refetch, so the diff turns true by itself the moment the mode moves behind the page —
  and the next unrelated save posts the stale mount value. The first live check passed only because it
  happened not to refetch. **The gate is operator intent** (`modeTouched`, set from the select's
  `onValueChange`), and the displayed value is *derived* — `modeTouched ? form.releaseMode :
  product.releaseMode` — rather than re-seeded through an effect, which could race a selection in
  progress. Deriving it also fixes the display half: without it the control kept showing the mount-time
  value, so picking "Git" on an already-showing-Git control was a no-op that reverted nothing.

  Both paths verified live end to end. Untouched: load Settings on a Git-mode product → flip to
  `releases` by RPC → force a refetch (the select follows to "Releases", no banner) → save a
  description edit → mode **stays `releases`**, no `release.mode.change` row. Touched: operator picks
  Git → banner appears → save → mode becomes `git` with the `Releases → Git` audit row. There is no
  frontend test runner in this repo (no vitest/jest/testing-library, no test files), so the payload
  assertion is the live check plus the comment on `modeTouched` naming the hazard.
- `products.retryFailedRollout` gained the Git-mode refusal the other release writers have (without it
  a "retry" is a fleet-wide branch-head deploy), and its copy stopped implying it re-deploys *this*
  release. It does not, and cannot: the enqueue carries no release id (invariant 3), so a
  latest-tracking instance deploys whatever is newest now. Button, summary line and toast all say so —
  "Retry failed instances", "A retry deploys each instance's pin, or the newest release if it tracks
  latest."
- The row actions are **"Roll out this release…" / "Roll back to this release…" / "Set this release…"**.
  "Deploy this release" promised the one thing the dialog does not necessarily do — its Deploy-now
  checkbox can be turned off — and the ellipsis matches every other dialog-opening control.
- `versionBucket` now uses `>=` against `newestId`, the same comparison family `rosterVersion`'s `>`
  uses. They have to agree or a row lands in the *behind* count with no `behind` chip to explain it.
- The dialog's re-seed **preserves a subset across a roster change**. A tenant provisioned while the
  dialog is open used to re-select everything, which silently promoted a deliberate three-of-twenty
  per-stack apply into a fleet write that also moves the template default. The selection is now
  intersected with the new ids, and only a selection that *was* everything grows. Both branches
  verified live by invalidating the roster query with the dialog open.
- The apply toast reports the **server's** counts, not the checklist's — a tenant provisioned since the
  dialog opened is written by the fleet call and absent from the list it was rendered from.
- Smaller: the `SkipReason` doc now names the three constants it can actually hold; `UnreleasedCommit`
  documents its tie-break (first by stack name, the order the roster query already imposes) and the
  null-commit caveat; and `Prune_ClampsARetentionValueAboveTheCeiling` covers the ceiling side of the
  clamp the first pass only tested from below.

**Mutation-checked again** for the five backend fixes: dropping the transaction wrapper, dropping the
Git-mode guard, dropping either `DefaultPinnedRelease` include, and removing the clamp's ceiling — each
failed exactly the test written for it.

## Stage 7 handoff — tenant-aware backups

**Where the ladder lives, and the two shapes it comes in.** `Services/BackupPolicyResolver.cs` is pure
and static: it takes a `Stack`, its `StackTemplate?` and nothing else, and returns four effective values
each paired with a `BackupPolicySource` (`Stack` / `Template` / `Instance`). That pairing is the whole
reason it is a record rather than four booleans — the "Set by: …" chips on the Backups tab and the
`*Source` fields on `BackupStackConfigDto` are the same answer the run used, not a second derivation.
The instance rung is three constants on the resolver (`DefaultEnabled = false`,
`DefaultStopContainers = true`, `DefaultQuiesceMode = Stop`) rather than new `BackupOptions` knobs:
`Backup:Enabled` already means something else (the schedule master switch, checked before any stack is
looked at), and inventing instance-wide defaults for the other two would be a settings surface nobody
asked for. Anything that ever wants them configurable adds them there, and the resolver is the only
place that changes.

**The chain, in one paragraph.** `BackupQueueService.Enqueue` gained an optional `BackupChainStep`,
registered inside the same lock that writes the job to the channel. When the worker finishes a *backup*
(never a restore) it reads the event's terminal status and calls
`BackupChainCoordinator.OnBackupFinishedAsync`, which pops every step keyed on that event id and either
runs it or refuses it. Two kinds today — `Deploy` (enqueue on the deploy queue under the trigger the
caller intended) and `TenantTeardown` (resolve `TenantTeardownService` from a fresh scope and run it) —
and a third would be a case in two switches. The coordinator is a singleton and takes
`DeployQueueService` plus `IServiceScopeFactory`; `BackupQueueService` depends on *it*, not the other way
round, which is what keeps the cycle open.

**Why `templates.backupAll` lives in the Backups module although it is named `templates.*`.** The
operation is a backup, it audits under `backups`, and the right thing for it to do when the Backups
module is switched off is to disappear — which module gating gives for free and a home in Tenancy would
not. `ci.getStackCi` set the precedent that a handler's name and its module need not agree.

**The one wire shape worth knowing before touching `backups.setStackConfig`.** The first five members of
`BackupStackConfigDto` are the *effective* policy and have not moved, so the management API and any
script reading `enabled`/`cron` gets the same answer it always did. The tri-state values are the new
`own*` members, and the write side takes `bool?`/`string?` throughout: **the whole policy is posted on
every call**, so a field the caller omits is cleared, not left. The stack Backups tab therefore sends the
stack's *own* values for every control it is not touching — sending the effective ones would turn every
inherited field into an override the first time any switch is flipped, which is the subtlest way this
feature could have failed.

**Two behaviour changes worth a release-note line.** A caller of `backups.setStackConfig` that omitted
`quiesceMode` used to get an explicit `stop`; it now gets "inherit", which resolves to `stop` for every
standalone stack (identical) and to the fleet's choice for a tenant (the improvement). And the archive
manifest is `formatVersion: 3` — additive, with the v1 body byte-identical up to `encrypted` and every
key since appended after it, so a reader that knew v1 or v2 still finds what it knew where it was.

**Live verification** (podman + API + Vite on 55436/5080/5177, the stage-4b recipe; no Docker daemon, so
every backup run failed for real — which turned out to be the useful direction). Confirmed with real
wire data: four tenants provisioned and stamped `prod/acme-web/{slug}` with all four `Backup*` columns
null, the standalone stack stamped `prod/acme-web-prod`; the product Backups tab's three inherit selects,
its live cron preview ("every day at 02:15") and its dirty state; a policy save writing the template row
alone and the tenant's tab immediately reading `Set by: acme-tenants` / "Follows acme-tenants: every day
at 02:15"; flipping one tenant switch writing **one** column and leaving three null; "Use acme-tenants's
policy" clearing all four; the roll-out dialog's pre-rollout checkbox (visible only with Deploy-now on,
carrying the "4 of them finish well apart" duration line) and its consequence sentence changing to "and
be deployed after a backup. An instance whose backup fails is not deployed."; **the chain end to end** —
four `pre-deploy` runs failed and produced four failed `release-manual` deploy events reading "The
pre-deploy backup failed, so this deploy did not run. See backup run #N"; `templates.backupAll` queueing
four runs and one `backup.all` audit row; the rollup moving to "0 backed up in the last 24 h · 4 failed ·
5 never · 5 deployments" with the fleet history naming each instance; the remove-tenant confirm
defaulting its final-backup switch **on**, its toast ("Backing up initech before removing it…") and the
abort path — the tenant still standing with `tenant.remove.aborted` recorded. No console errors.

Two things the live pass changed. The retention field showed the number the operator typed after a save
that **clamped** it (type 2, store 5) — the same stale-form shape the stage-6 review caught on
`releaseMode`, fixed by re-seeding from the saved product. And the "Set by: instance default" chip was
rendering on every row of a *standalone* stack's Backups tab, where the ladder has one rung and the chip
is therefore noise on a page that has not otherwise changed; it is now suppressed when the stack has no
template.

**Mutation testing** — nineteen mutations, seventeen caught, two proven equivalent:

| Mutation | Caught by |
| --- | --- |
| Template rung outranks the stack | 6 tests across both suites |
| Template rung dropped | 9 tests |
| Stack cron rung dropped | 3 tests |
| Chain runs the follow-up regardless of the outcome | both blocking tests |
| Chain leaves no trail on failure | both blocking tests |
| `setRelease` with `backupFirst` also deploys immediately | `SetRelease_WithBackupFirst_…` |
| `setTenantsRelease` with `backupFirst` also deploys | `SetTenantsRelease_WithBackupFirst_…` |
| `removeTenant` ignores `finalBackup` | `RemoveTenant_WithFinalBackup_…` |
| `ResolveDirectory` ignores the persisted value | `ResolveDirectory_PrefersTheStampedValue…` |
| Provisioning copies the template's policy instead of inheriting | `Provision_…LeavesEveryBackupFieldInheriting` |
| The retention clamp is dropped | `Update_SetsTheReleaseRetentionFloor…` |
| The `productId` filter is ignored | `ListBackupEvents_FiltersByProduct` |
| The rollup counts any failure rather than the newest terminal run | `GetProductBackups_RollsTheFleetUp…` |
| `backupAll`'s template filter is widened | `BackupAll_QueuesEveryTenantOfTheTemplate…` |
| `setTemplatePolicy` fans out onto the tenants | `SetTemplatePolicy_WritesTheTemplateAlone…` |
| The migration backfills the relaxed columns | `TheMigration_OnlyRelaxesTheColumns…` |
| Both stamp guards removed together | `StampDirectory_FillsALegacyStackOnce…` |

The two survivors are **equivalent mutants, not gaps**, and both are defence in depth: dropping the
schedule query's `BackupEnabled == null` guard changes nothing because the resolver refuses the row
anyway (the query narrows, the resolver decides — invariant 18), and dropping `StampDirectoryAsync`'s
in-memory early return changes nothing because the SQL predicate still says `IS NULL`. Removing the
*load-bearing* half of the second one does fail the test, which is the check that matters.

**Verification at hand-over** (from the repo root): clean `--no-incremental` Release build with
**0 warnings**; `dotnet test` 39 failures out of 2046, which is *exactly* the Windows baseline family and
count this document records at stage 4a (17 `CertificateStoreTests`, 11 ACME across four classes, 4
`CertificateManagerTests`, 2 `CertificateManagerProjectionTests`, and one each of `AuthEndpointTests`,
`BackupPlanOverrideTests`, `ProxyIngressEndpointReloadTests`, `ProxyChangeSignalTests`,
`FileStateImportTests`) — no new failures; `has-pending-model-changes` clean; the schema diff additive
(three new methods — `backups.getProductBackups`, `backups.setTemplatePolicy`, `templates.backupAll` —
plus new optional fields; the only removed lines are `required`-array entries that gained a successor and
the two `backups.setStackConfig` booleans that widened to nullable); `npm run build` green.

### Stage 7 review round — what changed after the first pass

One of these was a real bug that every operator would have hit on their first restore; the rest are
the kind of thing that only shows up when someone asks "and what does this number mean when both are
true?".

- **The restore reader was behind its own writer.** `RestoreDumpPlan.KnownFormatVersion` stayed at 2
  while the writer moved to 3, so **every restore of an archive this build wrote** would have printed
  "the archive says formatVersion 3, which is newer than this Watchtower understands" — an operator
  stopping a restore over nothing. The constant is now `= BackupService.ManifestFormatVersion`, so the
  two cannot drift again: a format the reader genuinely cannot follow is a *reader* change landing with
  the writer bump, not a second number. `AnArchiveThisBuildCouldHaveWritten_ProducesNoFormatWarning`
  is the test that would have caught it (a `[Theory]` over 1, 2 and the writer's own constant), and
  the forward-compatibility test now uses 4.
- **The stack Backups tab's two switches were two-state over a three-state field.** An inherited `true`
  looked exactly like an owned `true`, so "confirming" the value already on screen — or toggling twice —
  silently detached the field from the fleet and froze it at whatever the fleet said that day. A tenant
  now gets a select whose first row *is* the inherited state and names the value in force
  (`Inherit (currently: Pause — from acme-tenants)`), which makes choosing what is on screen a no-op and
  choosing anything else visibly a decision; picking that row is also the per-field revert, so the
  bulk "Use X's policy" button is now a shortcut rather than the only way back. A **standalone** stack
  keeps the switches it has always had: there the ladder is stack-then-instance, so "inherit" and an
  explicit value equal to the instance default behave identically and a third state would be a
  distinction the reader cannot act on. The quiesce select's `Stop (default — application-consistent)`
  lost the word *default*, which was a lie for a tenant whose fleet says pause; the default is named on
  the Inherit row, where it can say where it comes from.
- **The rollup counted some stacks twice and blamed others for a choice.** A stack that had never been
  backed up *and* whose last run failed appeared in both `failed` and `never`, so three stacks could
  read as four problems; and a stack nobody had put in the schedule was counted as "never backed up",
  painting a deliberate choice red forever. The buckets are now a **partition of the enrolled stacks**
  in priority order — never > failed > backed-up-recently > stale — so they sum to `enrolled` and the
  line adds up, with `notEnrolled` reported apart and rendered neutrally. Enrolment is the *resolved*
  policy's (invariant 18), so a tenant enrolled only by its template counts and one that opted out by
  hand does not. `GetProductBackups_PartitionsTheEnrolledFleetIntoFourBuckets` asserts the sum itself;
  `GetProductBackups_CountsUnenrolledStacksApart_AndReadsEnrolmentThroughTheLadder` covers the
  denominator.
- **The compose-label snippet was rendering rows the stack does not own.** Its contract is "paste this,
  delete your overrides, nothing changes", which a tenant cannot honour for a row it inherits — and
  rendering it would quietly copy one instance's *fleet* policy into one instance's compose file, which
  is the opposite of what a fleet policy is for. `ComposeLabelSnippet.Render` now skips
  `FromTemplate` rows (`TheSnippetRendersTheStacksOwnOverridesOnly_NotTheOnesItInherits`).
- **`TemplatePolicyCard`'s comment claimed a re-seed it did not do.** It is now the stage-6 pattern for
  real: dirtiness is measured against what the form was last *seeded* from (a ref), never against the
  live prop — otherwise a policy moving behind the page turns the dirty flag true by itself and the next
  Save posts the stale mount values back over it — and the effect re-seeds only a form the reader has
  not touched, so an edit in progress survives a refetch. A successful save re-seeds from what the
  **server stored**, not from what was typed, because the cron is trimmed on the way in.
- **The audit line's "(inherited)" suffix described the wrong field.** With the stop switch on, the
  clause names the *quiesce mode*, so it now takes that field's provenance; only the keep-running clause
  takes the switch's. Saying the fleet chose pause when the stack did (or the reverse) is exactly the
  kind of thing an audit trail exists not to do.
- **A pre-deploy chain whose stack was deleted mid-run tried to write a foreign-key violation.** A
  backup takes minutes and a delete needs no permission from the backup queue; the blocked-deploy path
  now checks the stack still exists, logs once and writes nothing — there is nobody left to read the
  record anyway.
- **A double-clicked final-backup removal read as if nothing had happened.** The row stays on screen
  until the backup succeeds, so a second click enqueued a second removal. Two halves, both small: the
  row now disables itself with "Backing up before removal…" while its chain is in flight (page-local
  state — the durable record is the backup event, and this exists only to stop the second click), and
  the coordinator treats `TenantNotFound` on a chained teardown as **success**, because two steps landing
  on one coalesced backup is a legitimate shape and the second one finding the tenant already gone is
  the outcome it wanted. Auditing that as a failure would have put a red row under a removal that worked.
- **"Unstamped" is now spelled the same way in all three places that ask it** — `ResolveDirectory`, the
  stamp's in-memory guard and its SQL predicate all treat an empty string like null. Belt and braces
  (the column is only ever written from `BackupNaming`, whose `Sanitize` never returns blank), but three
  predicates that disagree about the same question is how the fourth one gets written wrong.

**Two defects the review round's own live pass found**, both in the control m4 introduced and neither
visible from a typecheck. The Inherit row named the value in force even while the stack *overrode* the
field — so over an owned "Stop" it read "Inherit (currently: Stop — from instance default)" and promised
that picking it changed nothing, when picking it is exactly what changes it back to the fleet's Pause.
It now drops the parenthetical while the field is overridden ("Inherit from acme-tenants") and keeps it
while the field is genuinely inherited, because the wire carries no separate "what you would inherit"
value and inventing one would be a guess. And the **standalone** stack's quiesce trigger rendered
*blank*: its value was the `inherit` sentinel while its option list deliberately has no Inherit row, and
Radix shows a value with no matching item as an empty box. It selects the effective value there, as it
did before the stage.

**Two tests the first pass owed, both now present.**
`TwoChainedEnqueuesForOneStack_CoalesceOntoOneEvent_AndBothStepsFire` drives the coalescing branch
through the real `BackupQueueService.Enqueue` (not `Attach`) against the host's own coordinator, with
the deploy queue replaced by a recorder — attaching by hand would have tested the dictionary and skipped
the decision that fills it. `ALabelAlsoWinsOverAnOverrideInheritedFromATemplate` pins the top rung
against the new one: a compose label beats a template-inherited service override, and the *unlabelled*
service in the same plan is attributed to `Template` rather than to a stack override the reader would go
looking for and never find.

**Wire impact of the review round:** the rollup DTO gained three members (`enrolled`, `notEnrolled`,
`stale`) — the only non-additive part of the round, and it is a field nothing outside this tab reads,
shipped in the same stage that introduced it. Everything else (M1, the tab work, the snippet, the audit
wording, the chain guards) leaves `rpc-schema.json` byte-identical.

## Stage 8b handover — the IA fold, and the end state

**The fold, in one sentence:** a template was always "a product plus tenancy rules"
(design.md §Navigation), so it is now the product's tenancy setup on the product's **Instances** tab,
and `/templates*` is three redirects.

**Who owns the Instances tab, and why it is not the products module's.** It is contributed by the
**Tenancy** module (`modules/templates/module.tsx`), exactly as CI and Backups contribute theirs. That is
the only arrangement that keeps the module rule intact in both directions: moving the roster into
`modules/products/` would have the products module owning tenancy's screen, and having products *import*
it would break "modules never import each other" outright. So products owns Overview / Releases /
Settings, tenancy contributes Instances, ci contributes CI, backups contributes Backups — and the
Tenancy module keeps its whole `templates.*` RPC surface untouched. **This was an IA move, not an API
change**: `templates.create` still accepts the inline source fields on the wire for back-compat, the UI
simply never sends them again (it posts `productId` with the three source fields blank, which both
`templates.create` and `templates.update` already treat as "no opinion").

**Ordering.** Instances is `order: 30` — the slot the Backups tab's own comment reserved for it in stage
7 — which forced **CI from 30 to 32**. They were tied at 30, and a tie is resolved by module *discovery*
order, which is alphabetical and therefore an accident; the design numbers the tabs Overview, Releases,
Instances, CI, Settings, so the tie had to be broken in that direction rather than left to `ci` sorting
before `templates`.

**Multi-template cardinality, decided honestly.** `Product.templates` is a collection and the backend
has always allowed several (different domain patterns over one codebase), so the tab renders **one
self-contained section per setup** rather than pretending there is one — each with its own summary card,
add-tenant row, grants card, rollup, roster and dialogs, and its own queries keyed on its own template
id, so two sections cannot share state. A product with one setup (every product anybody has) sees
exactly one section and no hint that a second is possible, except a quiet **"Add another tenancy
setup"** link at the bottom. That link exists because `/templates/new` could create one and *never
delete a control someone has used* — it is demoted, not removed. Verified live with two setups on one
product; the Backups tab renders one policy card per setup for the same reason.

**`/templates/$id` is a component, not an async `beforeLoad`, and that is load-bearing.** The hop needs a
lookup (the product id lives on the template). A guard that has to `await` leaves the router with no
match while it waits and — when the lookup *rejects*, which is exactly the deleted-template bookmark —
renders a blank page. `TemplateRedirect.tsx` resolves in a component instead, so every outcome is on
screen: a spinner while it looks, `/products/$id?tab=instances` when it finds one, `/products` when it
does not. It reads the `['template', id]` key the Instances tab reads, so the hop costs one request.
`/templates` and `/templates/new` stay synchronous `beforeLoad` redirects, because neither has anything
to look up — `/templates/new` goes to the catalogue rather than to a form, since creating a setup now
starts from a product and no id in that URL says which one.

**The migration banner.** One-time, on `/products`, dismissible, flagged in `localStorage`
(design.md §"Migration morning-after"; the sanctioned lightweight use — the worst case of losing the flag
is seeing one info banner twice, and a column for a sentence would mean a migration, a DTO field and a
write endpoint). Both accessors are wrapped, because a browser that refuses storage *throws* rather than
answering null, and the failure direction is "show the banner again". It is shown only to an install that
actually has a tenancy product: telling a hobby install about a move it cannot have noticed is the noise
the Übersichtlichkeit audit is about.

**The Next-steps card — built in 8b, and it was the last unbuilt §UX element.** design.md §"SaaS flow"
step 2 describes it (*Deploy it once* / *Run it for many tenants* / *Build it here*, "three rows, three
sentences, three buttons"); no earlier stage built it, and stage 8b's first pass left it out before the
review round called it in. It lives on the product Overview and renders **only while the product has no
deployments, no tenancy setup, no releases and no CI link** — the first three come free off the product
query, and the CI probe (`ci.getProductCi`, the key the CI and Releases tabs already share) is
`enabled` only once the other three hold, so a product with instances never pays for it. Three
decisions in it are worth knowing:
- **It replaces the Deployments empty state rather than sitting above it.** Both carry a "Create
  deployment" button, and two of them on one screen — one inside a card explaining there is nothing to
  list — is the noise the card exists to remove. The Deployments card returns the moment the card goes.
- **Each row is gated on its own module.** A *Run it for many tenants* button with `Tenancy` off, or
  *Build it here* with `Ci` off, would be a door into a wall, because the tab it opens is not
  contributed.
- **It waits for the CI probe instead of flashing.** Rendering three rows and then dropping one when the
  probe lands reads as a glitch on the one screen whose whole job is to teach.

The Instances tab's own empty state ("No tenancy yet" → **Set up tenancy**) stays: it is where a reader
who skipped the card, or who comes back later, meets the same door.

**Live verification** (podman on 55438 + API on 5085 + Vite on 5178; the stage-4b recipe, with one
wrinkle worth recording: **the dev server's proxy target is hard-coded to `:5080` in `vite.config.ts`**,
so with something else holding that port the walkthrough needs either that port freed or a throwaway
config — `VITE_API_URL` sends the client cross-origin and dies on CORS). Confirmed against real wire
data: Templates absent from the sidebar; the tab strip in the design's order; `/templates/1` →
`/products/1?tab=instances`, `/templates/999` → `/products`, `/templates` and `/templates/new` →
`/products`; the migration banner once, dismissed, and gone after a reload with the flag set; the summary
card reading `{tenant}.acme.io → web:8080 · 1 base env var · Realm: Operator`; [Edit] expanding it into a
form with **no source card**, the live preview tracking the input (`acme.acme.io · globex.acme.io`), and
a save that changed the pattern and the port while leaving `repositoryUrl` and the branch override
untouched; a tenant added from the row with its resolved-domain hint; the roll-out dialog over a
24-release product showing "Showing the newest 20. Show older", growing to 24 options after one click,
and applying a pin to `1.0.1` — a release outside the original window — across three tenants *and* the
template default; the stack Version dialog showing `release #1 (not loaded yet)` for the same
out-of-window pin and resolving it to `1.0.1 · …` after "Show older"; the per-service editor writing a
template row from its own menu (`exclude` → `exclude, stop=pause`) with the audit row
`backups`/`template.service-override.update`; two tenancy setups on one product rendering two sections
and two policy cards; and a Git-mode product correctly showing **no** version controls on its Instances
tab (invariant 4 on a surface that moved). Console clean.

**Two legs this host could not verify live, and how they were verified instead.** There is no Docker
daemon on this machine, so `backups.previewPlan` fails: the per-service editor's *populated* borrowed
list, and a tenant plan preview rendering a template row as "Template policy: …", were covered by the
new `GetProductBackups` / `SetTemplateBackupServiceOverride` tests and by stage 7's existing
`ALabelAlsoWinsOverAnOverrideInheritedFromATemplate` rather than through the browser. What the browser
did cover is the branch that only appears *without* a daemon — the stored rows still listed and editable,
the footer refusing to claim a borrow that did not happen — which is a real state on any install whose
daemon is down.

**Mutation testing**, three mutations and three catches: dropping `FromTemplate: true` from the setter's
response fails the write test's `Inherited` assertion, disabling the setter's delete branch fails
`SetTemplateServiceOverride_WithNothingSet_DeletesTheRow`, and making `GetProductBackups` project an
empty override list fails the read-model half of the write test.

**Verification at hand-over** (from the worktree root): clean `--no-incremental` Release build with
**0 warnings**; `dotnet test` **38 failures out of 2067** — the exact Windows baseline families this
document records (17 `CertificateStoreTests`, 11 ACME across the same four classes, 4
`CertificateManagerTests`, 2 `CertificateManagerProjectionTests`, and one each of
`BackupPlanOverrideTests`, `ProxyIngressEndpointReloadTests`, `ProxyChangeSignalTests`,
`FileStateImportTests`), no new failures and the three new tests passing; `has-pending-model-changes`
clean (no entity changed — the new handler writes a table that already existed); the `rpc-schema.json`
diff **additive** (one new method, `backups.setTemplateServiceOverride`, plus `serviceOverrides` on
`BackupTemplatePolicyDto` — the only removed lines are the two `required`-array entries that gained a
successor); `npm run typecheck` and `npm run build` green.

## Stage 8c handover — the dashboard fleet view

**Why it exists.** A tenancy install's dashboard was a grid of forty near-identical cards differing
only by customer name, which is a list, not a dashboard. design.md §Dashboard is the spec; this is
what landed. Two files, both frontend (`modules/dashboard/sections.tsx`,
`modules/dashboard/module.tsx`), **zero backend files and no new RPC** — the whole feature is a
client-side join of two calls the app already serves.

**Why the dashboard module owns it, and not products or tenancy.** The Fleets section and the stacks
grid are one rule seen from two sides: the grid drops exactly the stacks the cards represent. One
owner of both halves is the only arrangement in which that rule is stated once and cannot drift; a
section contributed by another module would need the grid to re-derive the same set. Modules never
import each other, and this imports none — only `lib/api`, `lib/release` and `lib/types`, which is
the same allowance stage 4b's `lib/release.ts` was created under.

**The join, and the one thing it works around.** `useFleets()` is the hook both sections call
(React Query dedupes them onto one request each):

- `products.list`, `enabled` on `caps.isModuleEnabled('Products')` — the gate that makes this
  self-adjusting at the *query* level and not merely at the render level. A fleet is a product with
  `templateCount > 0`.
- one `products.get` per fleet, on the `['product', id]` key the product detail page itself uses, so
  the click-through from a card costs nothing. This is the workaround: **`StackDto` carries no
  `templateId`**, so `stacks.list` alone cannot tell a tenant from a standalone stack, and
  `ProductStackDto` — which does carry it — was already on the wire. Adding the field to `StackDto`
  would have been a backend change for a presentation rule. The real cost of this choice is the
  *payload*, not the round trips: `products.get` is the one response that carries the release webhook
  token (deliberately kept off the catalogue DTO), and the dashboard now fetches and caches it for
  every fleet on every load, for a card that renders none of it. The handlers are ungated, so nothing
  new becomes *reachable* — but if `products.get` ever grows heavier or gains gating, revisit with a
  lighter roster read.
- the live `stacks.list` rows the grid already holds. **The roster answers membership; the live list
  answers state.** Every value a card renders (status, pin, deployed release) comes from the same
  rows the grid renders, so the two can never disagree about a stack, and the card follows the grid's
  poll for free.

`settled` is the third return value and it is what keeps the grid from flashing: until every roster
has answered, "is this stack a tenant" has no answer, and rendering a tenant card that vanishes a
moment later reads as a glitch (the Next-steps card's reasoning, one screen over). It is true from
the first render when there is nothing to look up, so a hobby install waits for nothing. A failed
`products.list` is deliberately silent for the same reason the section is: no fleets, today's
dashboard, and no second error banner over a grid that still works.

**Four rules in the card, each of which could have shipped wrong:**

- **No Deploy button.** Every fleet action already lives on the product page next to the dialog that
  states its consequence, and a card with no Deploy owes no version beside one — invariant 6 is
  trivially true here rather than newly enforced. The rollup *is* the fleet's version surface.
- **The rollup is `Releases`-mode only.** `versionRollup` over the tenants with the product's own
  `latestRelease.id` as newest (invariant 7 — the id, never a timestamp), rendered in the Instances
  roster's exact words. In Git mode the three buckets describe nothing, so the line is absent:
  invariant 4 on a third surface, verified live by a `releases → git` revert. The *latest release*
  line stays in Git mode, matching the catalogue's own "Latest release" column — it is a fact about
  the product, not an update mechanism.
- **The failing chip classifies exactly as `StacksPage`'s failed filter and `StackCard`'s red dot do**
  (`lastDeployStatus === 'failed'`), deliberately and not more narrowly. It is now the only thing
  standing between a tenant's failure and invisibility, so classifying it *differently* from the cards
  it replaces is how a failure would go missing. A stopped tenant is not failing under that rule — its
  last deploy succeeded — which is the outcome asked for; a stopped tenant whose last deploy *did*
  fail is still counted, because that one was red in the grid yesterday.
- **A tenancy setup with no tenants gets no card**, and if it is the only one, no section. It runs
  nothing; finishing that setup is the product page's job.

**The exclusion rule is membership, not "has a template id".** Only tenants of a fleet that actually
rendered a card are dropped from the grid. A tenant whose fleet card is missing for any reason — the
Products module off, the roster query failed, a fleet filtered out for having no tenants — keeps its
own card, because the one outcome this may never produce is a stack that appears nowhere. A detached
tenant (`templateId` null) was never in the set. Two consequences: the grid's heading becomes **Other
stacks** while fleets are on screen (calling the remainder "Stacks" would contradict the Summary's
total two sections up), and an install whose every stack is a tenant renders **no grid section at
all** rather than an empty heading over the "No stacks yet" empty state, which would be false.

**Summary and Active deployments are untouched, on purpose.** The Summary stays global — a tenant is
a stack, it exists, and the fleet card is presentation rather than ontology. On the verification
install it read `5 · 4 · 1` over two fleet cards and no grid.

**Live verification** (podman on 55440 + API on 5090 + Vite on 5180 through a throwaway config, since
this host already had something on `:5080`; the stage-4b recipe otherwise). Eight states, all against
real wire data and real `docker.io` digest resolution for the two releases:

- **Hobby install → byte-identical, proven mechanically.** Two standalone stacks, no templates. The
  dashboard's `main` innerHTML was captured, the two changed files were `git stash`ed, the page
  reloaded, and the strings compared: **identical, 13 013 characters, exact equality**. Not "looks the
  same".
- **Products module off** (`Modules__Products__Enabled=false`, API restarted): no Fleets section,
  heading back to "Stacks", every tenant carded, and — instrumenting `fetch` — the dashboard's RPC set
  was `stacks.list · metrics.host · deployments.active · metrics.stacks`, with **no `products.*` call
  at all**. The `enabled` gate holds at the query level.
- **A three-tenant fleet with releases**: `saas-app · 3 tenants · [1 failing] · 1 on latest · 1 pinned
  · 1 behind · 1.4.0 · 2m ago · Open instances →`, with the three tenants gone from the grid and the
  product's *standalone* stack (`saas-app-staging` — same product, no template) still carded beside
  the two hobby stacks under **Other stacks**.
- **Silence is healthy**: a second fleet with two successful tenants and no releases rendered
  `internal-tools · 2 tenants · No releases yet` — no chip, no rollup.
- **Git-mode revert**: the rollup line disappeared, everything else stayed.
- **Zero-tenant fleet**: a template added to `blog` with no tenants produced no card, and `blog`
  stayed in the grid.
- **Detached tenant**: nulling one tenant's `template_id` moved it back into the grid, and the card
  correctly read `1 tenant`.
- **Every stack a tenant**: deleting the three non-tenant stacks left two fleet cards and **no grid
  section**, with the Summary still reporting all five stacks.
- **Click-through**: *Open instances →* → `/products/3?tab=instances` with Instances selected, whose
  roster rollup read `1 on latest · 1 pinned · 1 behind` — the same three words as the card, from the
  same function.
- **375px**: cards 343px wide, stacked, `scrollWidth - clientWidth === 0`. No console errors in any
  state, and the fleet card carries no `<button>` at all (asserted in the DOM, not by eye).

**Verification at hand-over**: `npm run typecheck` and `npm run build` green; `dotnet build` untouched
and unnecessary — `git status` shows two `.tsx` files and these two documents and nothing else, so
`rpc-schema.json`, the migrations and the test suite cannot have moved.

## Stage 9 handover — adopting an existing stack as a tenant

**Why it exists.** Every install that predates its own tenancy setup has the same shape: the first two
or three customers were deployed by hand, and the setup was built afterwards. Before this there was no
way to make those stacks tenants — `TemplateId` was written by `TenantProvisioningService` and nowhere
else, so the only path was delete-and-reprovision, which destroys the data. `templates.adoptStack` is
the deliberate second writer of that column.

**The keep-contract is the feature, and it is the acceptance test.** Adoption is defined by what it
does *not* touch: `Name`, `ComposeProjectName`, environment values, `PinnedReleaseId`,
`BackupDirectory` — and it enqueues **no deploy**, because nothing about what the stack runs changed.
`Adopt_MakesTheStackATenant_AndChangesNothingItWasRunning` asserts all of it in one test, and it is
mutation-checked against the two mistakes that would look like features (see the table below).

**Five decisions worth knowing before touching it:**

- **The naming asymmetry is kept, not fixed.** A provisioned tenant is `{template}-{slug}` with a
  matching compose project; an adopted one keeps whatever it was called. Compose namespaces
  containers, networks and volumes by project name, so renaming the project *is* a recreate — the
  outcome the feature exists to avoid — and renaming only the stack would leave the two disagreeing
  for nothing. **The convention therefore describes provisioned tenants, not tenants in general**;
  anything that derives a tenant's project name from its slug instead of reading the column is now
  wrong. There is no migrate-with-recreate option in v1, and `templates.removeTenant` +
  `templates.addTenant` is still the way to get one.
- **The route is created, never stolen.** `IsPrimary` is set only when the stack has no primary route
  yet. A stack that has been serving a customer-owned domain has it on every link, bookmark and
  certificate; demoting it for a subdomain the operator has just invented would be a redirect nobody
  asked for. The response says which way it went (`domainIsPrimary`) and the toast reads
  `globex.saas.example.com was added; app.globex.test is still its primary domain.` A rendered domain
  that already exists is **refused** naming the route's owner rather than moved — domains are globally
  unique and re-pointing a live hostname is not something an adoption should do quietly.
- **Env: the stack is the override.** Provisioning merges per-tenant overrides *over* the template's
  base; adoption applies the same rule with the running stack's rows in the overriding position — a
  base var is copied in only for a key the stack does not define. Replacing a value would change what
  the next deploy applies. The added keys come back in `envKeysAdded` and are named in the toast,
  because "your instance quietly gained four environment variables" is not something to discover
  later.
- **The branch needed a write, and it is the one thing the brief did not anticipate.** A tenant
  inherits its template's `BranchOverride` (`ProductSourceResolver`), so a stack with none of its own
  would have started deploying the fleet's branch the moment it was adopted — a change to what it
  runs, which is exactly what the keep-contract forbids. The effective branch is read before the write
  and put back through `ProductSourceResolver.OverrideFor` against what the stack would inherit
  *after* adoption: null when the two agree (invariant 5 — never pin what is merely inherited), an
  explicit override only where the template would otherwise have moved it. "What it would inherit"
  comes from a new `ProductSourceResolver.InheritedBranch(StackTemplate?, Product)` overload — invariant
  5 says a new write path computing an override **must use** that function, and the existing
  `InheritedBranch(Stack)` now delegates to it, so there is exactly one fallback chain rather than one
  plus an inline copy. Both directions are tested and both were verified live.
- **Version policy is untouched, deliberately.** `StackTemplate.DefaultPinnedReleaseId` is documented
  on the entity as the pin every ***future*** tenant takes and is copied at provisioning (invariant
  17). An adopted stack is a running one, so its pin — set or unset — is left exactly as it is, even
  when the fleet default disagrees. **Both directions are pinned**: an existing pin survives a
  disagreeing fleet default (the happy path), and the *absence* of one is not filled in with it
  (`Adopt_LeavesAnUnpinnedStackTrackingLatest_EvenUnderAFleetDefault`) — an unpinned stack is exactly
  the row provisioning's copy would have landed on, so without the second test that mutation lives. `templates.setTenantsRelease` is how an operator brings it onto
  the fleet's version, with the consequence stated in a dialog.

**Who is admitted is never moved as a side effect — a pre-flight refusal.** A service route takes its
realm from its stack's category (`RouteAccessPolicy`), so adopting into a setup of a **non-system
realm** would silently re-point every *protected* route the stack already serves at another
population: today's accounts stop being admitted on their next request, and the new realm's are let in
without anybody having granted them anything. That is word for word why `templates.update` refuses to
move a **populated** template's realm, and the reasoning applies here unchanged — so adoption refuses
too, before any write, naming the routes and the way through:

> `Stack 'legacy-acme' has 2 protected domain(s) — admin.acme.test, portal.acme.test — and
> 'shop-tenants' serves the 'Customers' realm, so adopting it would change who is admitted to them.
> Make them public or remove their protection first; re-protect them after adopting, deliberately, in
> the 'Customers' realm — adoption must not move who is admitted as a side effect.`

Two clauses keep it from being a wall. **`Public` routes are excluded**: public admission never
consults a realm, so a stack whose domains are all public moves no population and adopts freely —
which is the common case, an unprotected legacy stack joining a customer-facing setup. And **the
system realm never asks the question**: a standalone stack's routes are already in it, so the
single-realm install — every install that has not configured realms — is untouched however protected
its domains are. All three branches are tested.

**The allowed case still states the realm, and a non-administrator can read it.** `realms.list` is
`[RequireRole("Admin")]` but `templates.adoptStack` is not, so a reader who may adopt a stack must
still be able to see which population its domains are joining. `StackTemplateDto` therefore gained
**`realmName`** (additive, beside the `realmId` it already carried), the dialog renders
*"Its domains join the Customers realm — that is the population admitted to them"* off the template it
already loads, and `TemplateReads_AllProjectTheRealmName` covers **all four** producers including
`create` — the `DefaultPinnedRelease` trap, where two read paths shipped without the `Include` and
answered "no realm" over a template that had one. `templates.create` and `templates.update` now load
the `Realm` entity rather than merely checking that its id exists, because both answer with the
template they just wrote.

**Two things adoption *does* change, and both are named in the UI.** Backup **policy** starts
following the fleet for every field the stack left null — no rewrite needed, which is the tri-state
ladder (invariant 18) paying off, and the dialog says so. Backup **directory** does not move:
`BackupDirectory` is stamped once and names where bytes already are (invariant 20), so an adopted
tenant keeps writing beside its existing archives instead of relocating to
`{instance}/{product}/{tenant}`. Separately, a service route takes its realm from its stack's
category, so adopting into a setup of a **non-system realm** moves the population admitted through
the stack's *existing* domains too; the dialog states that line only when the roster answered (it is
Admin-gated) and the realm is not the system one.

**One write, no transaction wrapper — a deliberate departure from the two writers beside it.** The
template link, the env rows and the route are a single `SaveChangesAsync`, which EF executes in one
transaction, so a route insert losing the unique-domain index rolls the whole adoption back. The
explicit `BeginTransactionAsync` that `templates.setTenantsRelease` (invariant 16) and
`TenantProvisioningService` carry exists because each of *those* spans two statements; adding a
wrapper here would be code that cannot fail. **Adding a second statement means adding the wrapper with
it**, and the property is pinned rather than assumed:
`Adopt_ThatCannotWriteItsRoute_LeavesTheStackStandalone` stages a duplicate route on the handler's own
change tracker and fails the moment the write is split in two. The recovery catch is filtered —
`catch (DbUpdateException ex) when (IsUniqueViolation(ex))`, the `ProductCatalog` /
`ReleaseIntakeService` idiom — so a `DbUpdateConcurrencyException` (this write mutates a loaded
`Stack`, which carries `xmin`) escapes to the pipeline that answers "someone else changed this"
instead of being reported as a slug collision that did not happen. "Nothing landed" is scoped to the
database in the comment beside it: the change tracker still holds the mutated stack, which is harmless
only because a handler scope is per dispatch. The proxy work (`ConnectStackAsync` +
`ApplyAsync`) is post-commit and best-effort, sequenced exactly as `proxy.createRoute` does it —
adoption creates a route, it does not deploy.

**Refusals are `Conflict`, not `Validation`.** `templates.addTenant` projects its collisions as
validation failures only because that is the shape it has always returned; a new method has no such
history. The message shapes are its, though — each one names the thing in the way, which is the only
actionable part. Verified verbatim against a live API:

- `Stack 'legacy-acme' is already the tenant 'acme' of tenancy setup 'saas-tenants'. A stack belongs to one setup at a time.`
- `Stack 'blog-prod' runs product 'blog' and 'saas-tenants' runs 'saas-app'. Adoption never re-points a stack at another codebase.` (the `templates.update` product-change refusal, one rung down)
- `Tenant 'initech' already exists in 'saas-tenants' — it is stack 'saas-tenants-initech'.`
- `Domain 'staging.saas.example.com' is already routed to stack 'saas-staging'.`
- `Slug 'accessible' is reserved.` / the slug-format sentence — both `TenantProvisioningService`'s, on the normalized value.

**Frontend.** `modules/templates/AdoptStackDialog.tsx`, opened from a secondary action beside the
add-tenant row's "Add environment overrides" link. The action renders **only while the product has at
least one standalone deployment** — a control that opens a dialog whose only message is "nothing to
adopt" is the noise the Übersichtlichkeit audit is about. The roster it offers is `products.get`'s
`stacks` filtered on `templateId == null` (`==`, not `===`: the API omits nulls, so the field arrives
`undefined`), passed down from `InstancesTab` so two tenancy setups on one product share one answer.
The slug input reuses the add-tenant row's live domain preview verbatim, and the consequence sentence
is design.md's two halves — what will happen and what will not: *"acme.saas.example.com will point at
web:3000. The stack keeps its name, project, environment and version."*

**One accepted nit.** Whether the new domain became the stack's canonical one is stated in the
*toast* and nowhere else — the dialog cannot know it in advance without a per-stack route query it
would otherwise have no reason to make, and the roster's Domain column shows the primary immediately
afterwards, which is the same fact one screen over. Worth revisiting only if a reader is ever seen
looking for it.

**Live verification** (podman on 55442 + API on 5091 through `--no-launch-profile` + Vite on 5182
through a throwaway `vite.adopt.config.ts`, since this host already had something on `:5080`; the
stage-4b recipe otherwise — and note that `ASPNETCORE_URLS` alone does **not** move the API, because
`dotnet run --project` reads `launchSettings.json` first). Against real wire data:

- **The happy path, checked in SQL rather than by eye**: `legacy-acme` (env `LOG_LEVEL=debug`,
  `DB_URL=…`; setup base `LOG_LEVEL=info`, `TENANT_MODE=saas`) adopted as `acme` — afterwards `name`,
  `compose_project_name` and `backup_directory` (`desktop-…/legacy-acme`, *not* the tenant path)
  unchanged, `LOG_LEVEL` still `debug`, `TENANT_MODE` added, one Managed primary route, **zero
  `deploy_events` for the stack**, and the audit row `stacks`/`tenant.adopt`/`legacy-acme` reading
  `adopted into 'saas-tenants' as tenant 'acme'; route acme.saas.example.com created (primary); 1 env
  var(s) added`.
- **The primary-route rule**: `legacy-globex`, already serving the Custom primary `app.globex.test`,
  adopted as `globex` — the new `globex.saas.example.com` landed `is_primary = f`, the custom domain
  kept `t`, and the roster row still shows `app.globex.test` (it reads the primary, as it always did).
- **The branch rule**: with the setup moved to `develop`, a fresh `legacy-umbrella` on `main` came out
  with `branch_override = main` rather than silently following the fleet.
- **The roster moves on both surfaces**: the adopted stacks left the dialog's list (`legacy-acme,
  legacy-globex, saas-staging` → `legacy-globex, saas-staging` → … → the action disappearing entirely
  once nothing standalone was left), the Instances roster grew, the setup's instance badge went
  1 → 2 → 3, both new domains appeared on `/routes`, and the dashboard's fleet card went `1 tenant` →
  `3 tenants` with the adopted stacks dropping out of **Other stacks**.
- **A refusal in the dialog**: adopting `saas-staging` as `initech` rendered the server's sentence
  verbatim in the danger banner with the dialog still open and nothing written; correcting the slug
  and re-submitting succeeded.
- **The realm rule, both branches** (a second pass on its own database, with a `Customers` realm and a
  setup in it): `legacy-acme`, whose `portal.acme.test` and `admin.acme.test` were `Authenticated`, was
  refused with the sentence above verbatim — over RPC *and* in the dialog's danger banner — and stayed
  standalone; `legacy-globex`, whose only domain was `Public`, adopted successfully in the same setup.
- **The realm line does not need `realms.list`, proven by removing it.** The API was restarted with
  `Modules__Realms__Enabled=false`, so `realms.list` answers `Method not found`. The tenancy summary
  card's `· Realm: Customers` — which reads `useRealms` — **disappeared**, and the dialog's *"Its
  domains join the Customers realm — that is the population admitted to them."* **stayed**, because it
  reads `template.realmName` off a DTO the page already has. That is the whole point of the field, and
  it is the one claim a typecheck could not have made.
- No console errors in any state.

**What could not be verified live, and how it was covered instead.** There is no Docker daemon on this
host, so "the containers are the same ones" could not be shown with `podman ps` before and after. It
is covered structurally instead, which is stronger than a screenshot: `AdoptStack` injects
`WatchtowerDbContext`, `IProxyProvider`, `AuditLog` and `ICurrentUser` and nothing else, so **there is
no code path from it that recreates, restarts or redeploys a container** — it reaches
neither `ComposeCliService` nor `DeployQueueService`, and the happy-path test asserts `deploy_events`
is empty. `IProxyProvider` *does* reach Docker, and deliberately: `ConnectStackAsync` joins the routed
container to the edge network, which is an attach and not a lifecycle operation — the same call
`proxy.createRoute` makes on a running stack. The handler remark says the same thing; the claim is
"the containers are the ones that were there", not "nothing spoke to Docker".

**Mutation testing**, five mutations and five catches:

| Mutation | Caught by |
| --- | --- |
| The template's base env wins by key (provisioning's `MergeEnv` reading) | `Adopt_MakesTheStackATenant_AndChangesNothingItWasRunning` (`SHARED` reads `fleet`) |
| `IsPrimary = true` unconditionally, as provisioning hard-codes it | `Adopt_DoesNotStealPrimaryFromAnExistingCustomDomain` |
| The `BranchOverride` write dropped | both branch tests — the preserve half *and* the don't-pin-what-you-inherit half |
| The write split in two (link commits, then env + route) | `Adopt_ThatCannotWriteItsRoute_LeavesTheStackStandalone` |
| The stack renamed to `{template}-{slug}` with a matching compose project | `Adopt_MakesTheStackATenant_AndChangesNothingItWasRunning` |
| The realm pre-flight removed (the adoption succeeds and the protected routes change hands) | `Adopt_IntoANonSystemRealm_RefusesAStackWithProtectedDomains` |
| `stack.PinnedReleaseId ??= template.DefaultPinnedReleaseId` — provisioning's copy, verbatim | `Adopt_LeavesAnUnpinnedStackTrackingLatest_EvenUnderAFleetDefault` |

**Verification at hand-over** (from the worktree root): clean `--no-incremental` Release build with
**0 warnings**; `dotnet test` **38 failures out of 2093** — *exactly* the Windows baseline families and
count this document records at 8b (17 `CertificateStoreTests`, 11 ACME across the same four classes —
`AcmeOrderFlowTests` 6, `AcmeFailurePathTests` 2, `AcmeIssuerLeaseTests` 2, `AcmeHttpClientTrustTests`
1 — 4 `CertificateManagerTests`, 2 `CertificateManagerProjectionTests`, and one each of
`BackupPlanOverrideTests`, `ProxyIngressEndpointReloadTests`, `ProxyChangeSignalTests` and
`FileStateImportTests`; the flaky `AuthEndpointTests` case passed, which is the documented 38-vs-39);
no new failures, and the tenancy + realm families the diff touches are 234/234 green.
`has-pending-model-changes` **clean — no entity changed**, which is the point: adoption writes columns
that have existed since tenancy did. The `rpc-schema.json` diff is **purely additive** — **155 added
lines, zero removed**: one new method (`templates.adoptStack`) plus `realmName` on `StackTemplateDto`,
which reaches the wire at the three places that DTO already appears (`templates.get`,
`templates.list`, `templates.create`/`update`); no `required` entry was removed. `npm run typecheck`
and `npm run build` green.
