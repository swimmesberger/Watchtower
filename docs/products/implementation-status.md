# Products & Releases — implementation status

Living companion to [design.md](design.md) and
[ADR-0026](../decisions/0026-products-are-the-deployable-unit.md). The design doc says what to
build; this file says how far it got and what is owed. **The roadmap is complete**: stages 0–7 have
all landed, so this file is now the handover for whoever maintains the feature rather than the brief
for the next stage.

Branch: `wt/watchtower-multi-tenant-design-bad75e` (pushed). Last updated 2026-08-26 after stage 7
(tenant-aware backups) — the final stage.

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
| `_pending_` | **7 — tenant-aware backups** | The last stage. `Stack.BackupDirectory` (nullable, stamped at creation, legacy rows computed as before and stamped on their next *successful* backup); `Stack.BackupEnabled/BackupStopContainers/BackupQuiesceMode` widened to tri-state; `StackTemplate.BackupEnabled?/BackupCron?/BackupStopContainers?/BackupQuiesceMode?` plus the `template_backup_service_overrides` table — one additive migration (`AddTenantAwareBackups`) that rewrites no values. `BackupPolicyResolver` is the one answer to "what policy does this stack run under" (invariant 18), read by the schedule tick, the run, the preparation and the plan preview. `BackupChainCoordinator` is backup-then-something (invariant 19): the `pre-deploy` trigger behind `stacks.setRelease(backupFirst)` / `templates.setTenantsRelease(backupFirst)`, and the `final` trigger behind `templates.removeTenant(finalBackup)`. `templates.backupAll`, `backups.getProductBackups`, `backups.setTemplatePolicy`, and a `productId` filter on `backups.events`. Manifest `formatVersion` 3 (`productId`/`productName`/`templateId`/`tenantSlug`/`releaseId`/`releaseVersion`, appended). The stage-6 owed item is paid: `RetainReleases` on `products.update` (clamped) with a field on product Settings. Frontend: the product Backups tab (`modules/backups/ProductBackupsTab.tsx`, contributed to `productDetailTabs` at order 35), provenance chips and a "use the fleet policy" reset on the stack Backups tab, the pre-rollout checkbox in the roll-out dialog, and the final-backup switch on the remove-tenant confirm. |

**The roadmap is complete.** Every stage in
[design.md](design.md#staged-roadmap) has landed. What follows is the accepted debt and the
invariants a maintainer has to keep.

**Every 4b deferral landed in stage 6.** The mode-revert control, the "latest ≠ branch head" warning,
the per-row contextual labels and the instance-checklist roll-out dialog are all shipped; the list that
stood here is gone. One 4b note survives unchanged:

- The pin pickers offer the **newest 20 releases and no "Show older"** (`RELEASE_OPTIONS` in
  `hooks/use-product-releases.ts`, now shared by the stack Version dialog and the roll-out dialog). The
  Releases tab pages properly; the pickers are a fixed window, so a pin to a release older than 20 can
  be read (the chip names it) but not re-selected from a dropdown, and "N behind" degrades to a bare
  `behind` chip rather than guessing a number.

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
- **Turning the release sync off leaves the values at GitHub.** Watchtower stops maintaining
  `WATCHTOWER_URL`, `WATCHTOWER_PRODUCT_ID` and `WATCHTOWER_RELEASE_TOKEN`; it does not delete them —
  the same rule `ci.updateRepo` already follows for the registry secrets. Deleting them is a
  repository decision, and silently revoking a running workflow's credentials on a toggle would be
  the surprise. Rotating the token is how an operator actually invalidates what is out there.
- **The release contributor's `SaveChanges` carries `Product`'s `xmin`.** A product edit landing in
  the microseconds between the read and the stamp makes that pass throw
  `DbUpdateConcurrencyException`, which the contributor's own isolation catches and logs; the values
  did reach GitHub, only the hash did not, so the next pass re-pushes them. Accepted for the same
  reasons as the mode flip: retryable, self-correcting, and the alternative is an unconditional
  second statement.
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
- **`ProductCatalog`'s savepoint retry branch is untested** — forcing the interleave needs an
  injection seam inside `FindOrCreateAsync`, which is more test scaffolding than the branch is
  worth. Accepted: the savepoint create/release path is covered by every implicit-create test, and
  the branch itself only rolls back to the savepoint, detaches the speculative entity and loops back
  into the same re-read. Revisit if it ever grows a decision.

### From stage 7

- **A chain does not survive a process restart.** `BackupChainCoordinator` holds its pending steps in
  memory, like both queues: a Watchtower that dies between a backup finishing and its deploy being
  enqueued loses the chain, and the backup event is the durable record that the backup happened. That
  is the same guarantee the deploy queue already gives (a queued deploy does not survive a restart
  either); buying more means a durable job table, which this feature does not justify. The blast radius
  is one un-run deploy, or one tenant that was going to be removed and now is not — both re-triggerable
  by hand, and the second is the safe direction to fail in.
- **Template-level per-service overrides have a table, an entity and a resolver rung, but no UI.**
  `template_backup_service_overrides` is created, `BackupService.LoadOverridesAsync` reads it under the
  stack's own rows and tags what it inherited (`BackupServiceOverride.FromTemplate`), and the stack's
  plan preview renders those rows as "Template policy: …" instead of "UI override: …". What is missing
  is a way to *write* them: the product Backups tab's policy card covers the four stack-level fields
  only. Deferred deliberately — the per-service editor is the plan preview's table, which is per stack
  and needs live containers to render, and a fleet has no containers of its own. The honest v2 is a
  per-service editor on the template that borrows one instance's service list; until then a fleet-wide
  exclusion is set per tenant or written as a compose label, which is the answer ADR-0020 prefers anyway.
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
  runs against an unreachable daemon left every `backup_directory` untouched.

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
  exercises them for real; only the deploy itself fails, which is fine for UI work.
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
