# Products & Releases — one deployable unit, from hobby stack to tenant fleet

Status: draft (2026-08-25). Decision record: [ADR-0026](../decisions/0026-products-are-the-deployable-unit.md).
How far the implementation got, and what is owed: [implementation-status.md](implementation-status.md).

## Motivation

Watchtower has three features that almost — but don't — compose:

1. **Tenancy exists** (`StackTemplate` → tenant `Stack`s with auto-created subdomain `Route`s), but
   templates *copy* the git source (`RepositoryUrl`, `Branch`, `ComposeFilePath`, `CredentialId`)
   onto each tenant at provision time and never propagate changes afterwards. The only
   parameterization axis is flat env vars; there is no per-tenant version control.
2. **CI exists** (`CiRepo`: ephemeral self-hosted GitHub Actions runners, registry-credential sync
   into a repo's Actions secrets), but it is linked to stacks only by URL string matching, and the
   chain **breaks after "image pushed"**: nothing bridges a finished build to a deployment except a
   hand-wired per-stack webhook or digest polling on an interval.
3. **The git source is denormalized.** The same four columns live on every `Stack` *and* every
   `StackTemplate`. There is no first-class notion of "the thing that gets built and deployed" — a
   deploy is "clone the branch head and hope the registry tag moved".

Two use cases must feel equally native, and neither may clutter the other:

- **Hobby / NAS**: point Watchtower at a GitHub repo; it builds (CI) and deploys. One stack, no
  tenants, often no routing.
- **Multi-tenant SaaS**: one product repo, many tenant instances on subdomains, controlled version
  rollout — most tenants track the newest build, some are pinned to a specific version — and a
  commit on the main branch flows through CI into the tenants that track it.

## The model

> **A Product is what you deploy. A Stack is a running copy of it.**

- **Product** — a git repository that defines a deployable application: compose file path, default
  branch, clone credential, optional CI, and its **releases**. Universal: every stack references a
  product.
- **Release** — one build of a product: a git commit plus the set of image digests CI pushed.
  Reported by the workflow through a product-level webhook.
- **Stack** — a running copy. It either **tracks latest** (deploys the newest release as it
  arrives) or is **pinned** to one release. A tenant is just a stack provisioned by a template.
- **StackTemplate** — narrows to *tenancy policy* on top of a product: the `{tenant}` domain
  pattern, target service/port, realm, base env vars, and fleet defaults.

Everything *definitional* (source, CI, releases, tenancy rules) lives on the product; everything
*runtime* (containers, domains, deploy history, backups, desired state, version policy) lives on
the stack. Every duplication in today's UI — repo fields on `/stacks/new` **and** stack Settings
**and** `/templates/new` — is a symptom of the missing noun.

Two contracts anchor the design:

- **Back-compat contract:** a product with no releases deploys byte-for-byte as today — shallow
  clone of the branch head, no image overrides. The entire release machinery is dormant until the
  first release arrives, which is also the migration guarantee: after upgrade, nothing behaves
  differently.
- **Implicit-product contract (hobby guarantee):** creating a stack stays one form at
  `/stacks/new` with the repo fields as today; the product is found-or-created silently behind
  `stacks.create`. There is never a separate "create product first" step.

## Domain model

### Product

| Property | Notes |
| --- | --- |
| `Id`, `Name` (unique), `Description?` | No slug; webhook URLs use the numeric id like the stack webhook does. |
| `RepositoryUrl`, `ComposeFilePath`, `DefaultBranch` | Moved off `Stack` / `StackTemplate`. |
| `CredentialId?` → `Credential` (SET NULL) | Git clone credential. |
| `ReleaseWebhookToken?` (unique index), `ReleaseWebhookEnabled` | Bearer for the release webhook; plaintext like `Stack.WebhookToken` (must be re-pushable to GitHub). `wtrel_` prefix. |
| `ReleaseMode` (`Git` \| `Releases`, enum name in DB) | Auto-flips to `Releases` on the first accepted release (audited, operator-revertible). The binary switch the whole UI keys on. |
| `CiRepoId?` → `CiRepo` (SET NULL) | Replaces URL string matching. |
| `SyncReleaseSecrets` | Push release token/URL to the CI repo's Actions secrets. Filtered unique index: at most one product per `CiRepo` may set it. |
| `ActionsSyncedHash?`, `ActionsSyncedAt?`, `LastActionsSyncError?` | Release-secret sync state (registry-sync state stays on `CiRepo`). |
| `RetainReleases` (default 50) | Pruning floor; never prunes pinned or recently-deployed releases. |
| `CreatedAt` | |

Table `products`, `xmin` concurrency token (several writers meet on it: edit handler, webhook,
sync, per ADR-0024 §3).

**Naming.** "App/Application" is taken by the opposite end of the pipeline (`/api/app/*`,
`WATCHTOWER_APP_TOKEN`, the `apps-portal` realm-user page); "Source" undersells it; "Blueprint"
collides with `StackTemplate`. The sentence that has to read well — "this stack runs product
*acme/web*, release *1.4.2*" — does.

**No `RealmId` on Product.** Realm scoping stays on `StackTemplate` because it governs who may
enter tenant routes (`docs/central-auth/design.md` §13). A second realm column would create two
sources of truth for the same question.

### Release and ReleaseImage

| `releases` | Notes |
| --- | --- |
| `Id` | **Also the ordering key**: latest = highest `Id` (monotonic, clock-skew safe). |
| `ProductId` (Cascade) | |
| `Version` | Display label, unique per product; defaults to the short commit SHA. |
| `CommitSha?` | Null for a poll-discovered release: it still pins digests, clone falls back to branch head. |
| `Branch` | What it was built from; validated against the product branch at intake. |
| `Fingerprint` (unique per product) | `sha256(commit + "\n" + sorted "repo@digest" lines)` — the idempotency key. |
| `SourceRunUrl?`, `Notes?` | |
| `CreatedVia` (`webhook` \| `poll` \| `manual`) | |
| `CreatedAt`, `PublishedAt?` | Display only, never ordering. |

| `release_images` | Notes |
| --- | --- |
| `ReleaseId` (Cascade), `Repository`, `Tag?`, `Digest` | `Repository` is canonical/lowercased (`ghcr.io/acme/api`, `docker.io/library/nginx`); `Digest` is the manifest **index** digest (`sha256:…`). Unique `(ReleaseId, Repository)`. |

A child table rather than newline-packed text (the `StackUpdateCheck` trick) because "which release
contains repository X" and per-image availability are real queries.

### What stays on Stack / StackTemplate

`Stack` keeps everything runtime: `Name`, `ComposeProjectName`, `EnvVars`, deploy webhook fields,
`AppApiToken`, `AutoDeployMode`/`AutoDeployTime`, `LastDeployed*`, `DesiredState`, all `Backup*`,
`TemplateId`/`TenantSlug`, `DeployEvents`, `UpdateCheck`. It gains:

- `ProductId` (required, **Restrict**)
- `BranchOverride?` — prod-on-`main` / staging-on-`develop` against one product stays possible
- `PinnedReleaseId?` (**Restrict** — deleting a pinned release is refused; SET NULL would silently
  flip a pin into latest-tracking, a deploy-behaviour change caused by a delete elsewhere)
- `LastDeployedReleaseId?` (SET NULL)

**No `TrackingMode` enum, no channels in v1.** `PinnedReleaseId == null` *is* "track latest"; the
DTO exposes a derived `trackingMode: "latest" | "pinned"`. Named channels (stable/beta) are future
work — a label on `Release` can add them later without invalidating any of this.

`StackTemplate` loses the four source columns and gains `ProductId` (required, Restrict),
`BranchOverride?`, `DefaultPinnedReleaseId?` (SET NULL — a default for *future* tenants, copied at
provisioning because per-tenant pinning is the point of the SaaS case), plus the backup policy
fields (see [Backups](#backups-across-tenants)). It keeps `RealmId`, `Name`, `DomainPattern`,
`TargetServiceName`, `TargetPort`, `BaseEnvVars`.

`DeployEvent` gains `ReleaseId?` (SET NULL) — what makes a rollout view possible without
string-matching `TriggeredBy`. `StackUpdateCheck` gains `AvailableReleaseId?` /
`AvailableReleaseVersion?` and a local-drift field.

### Source resolution

One place (`ProductSourceResolver`), nothing copied:

```
effectiveBranch = stack.BranchOverride ?? stack.Template?.BranchOverride ?? product.DefaultBranch
```

`ComposeFilePath` and `CredentialId` are product-only: a different compose file in the same repo
*is* a different deployable thing — create a second product over the same URL (products are
deliberately not unique on repository URL). A repo has one clone credential.

### Relationships and delete behaviour

```
Product 1──* Stack           (ProductId, REQUIRED, Restrict — delete refused, names blockers)
Product 1──* StackTemplate   (ProductId, REQUIRED, Restrict)
Product 1──* Release         (Cascade)  1──* ReleaseImage (Cascade)
Product *──1 CiRepo?         (SET NULL; many products may share one repo)
Stack   *──1 Release?        (PinnedReleaseId, Restrict)
Stack   *──1 Release?        (LastDeployedReleaseId, SET NULL)
Stack   *──1 StackTemplate?  (TemplateId, SET NULL — unchanged; a detached tenant now keeps
                              working because it holds its own ProductId, not because of copied
                              fields — same outcome, better mechanism)
```

`CiRepo` is **not** merged into `Product`: it is GitHub-specific infra (PAT, JIT runners, warm
hashes), its cardinality to products is one-to-many, and non-GitHub products must not carry GitHub
columns. `GitHubRepoUrl.TryParse` remains the *resolution* mechanism when enabling CI; the FK
records the answer instead of recomputing it per read. `products.update` clears/re-resolves
`CiRepoId` when `RepositoryUrl` changes. The FK is a cache, not the truth: `CiRepoResolver` ignores a
link whose `owner/name` no longer matches the parsed URL (case-insensitively, both on read and on the
write path) and falls back to the `owner/name` lookup. Correction lives there rather than in the
lazy link write, which stays a single `WHERE ci_repo_id IS NULL` statement that can never overwrite a
deliberate link — a stale row is harmless once no read believes it.

## Release intake

### Webhook contract

`POST /api/webhooks/products/{id:int}/release`, mapped in `WatchtowerHttpEndpoints` next to the
stack deploy webhook and following it exactly: product missing or `ReleaseWebhookEnabled == false`
→ **404** (never an existence oracle); bad bearer → **401**; token comparison via
`CryptographicOperations.FixedTimeEquals` (and the stack webhook's ordinal compare is worth
retrofitting in the same change — that retrofit also stops an *enabled webhook with an empty token*
from accepting unauthenticated deploys, which is a behaviour change worth a release-note line).
Fixed-window rate limit partitioned by product id (~20/min, the `LoginRateLimiting` shape), plus a
generous per-client-address one taken before the product lookup because the route is anonymous; body
capped at ~16 KB, max 20 images.

```json
{
  "commit":  "a1b2c3d4…40 hex",                        // required
  "branch":  "main",                                    // required ($GITHUB_REF_NAME)
  "images":  ["ghcr.io/acme/api:a1b2c3d",              // required, 1..20
              "ghcr.io/acme/worker@sha256:…"],
  "version": "2026.8.24-142",                           // optional; default commit[..7]
  "runUrl":  "https://github.com/…/actions/runs/…",     // optional
  "notes":   "…"                                        // optional
}
```

- **`branch` is required and validated against the product's branch.** Without it, a workflow that
  also runs on pull requests would publish a feature-branch build to every tenant. Relying on the
  author writing `if: github.ref == …` is not a safety property.
- **Digest resolution is Watchtower's job.** Each `images` entry is `repo:tag` *or* `repo@digest`.
  Tags are resolved to manifest digests at intake via the existing registry HEAD path
  (`DockerEngineClient.GetRemoteDigestAsync`), in parallel, ~10 s budget; digest refs pass through.
  Making the workflow wire `steps.build.outputs.digest` per image is the step people get wrong;
  resolving a tag the workflow just pushed is one HEAD request and the result is pinned forever.
  Credentials come from `RegistryAuthBuilder.ListResolvedRegistriesAsync()` matched on registry host —
  **not** the stack's git credential (`StackUpdateService` currently conflates the two; the release
  path must not inherit that, and the old path is worth fixing in the same neighbourhood).
- **Commit ancestry is not verified** (it would need a fetch or an API credential the product may
  not have); the deploy is the proof — `git fetch <sha>` fails loudly. Images *are* verified, for
  free, because resolution already contacts the registry.

| Condition | Code |
| --- | --- |
| New release created | `201` — `{releaseId, version, commit, images, stacksEnqueued}` |
| Fingerprint matches an existing release (replay) | `200` — same body, `stacksEnqueued: 0`, no fan-out |
| Malformed commit / branch mismatch / bad `images` / registry host not in the resolved registry view | `400` |
| `version` reused by a *different* fingerprint | `409` |
| Registry says the tag does not exist | `400` (names the image) |
| Registry unreachable / timed out | `503` + `Retry-After: 30` |
| Rate limit | `429` |

**Idempotency by fingerprint, not by commit.** A retried `curl` produces the identical fingerprint
→ `200`, no fan-out. A genuine *rebuild* of the same commit with new base-image layers produces new
digests → a new release with the same commit, which is correct and exactly the case a commit-keyed
rule would wrongly swallow. The unique index is the enforcement; the pre-check exists for the error
message.

**Threat model for a leaked token:** the attacker can create releases, but an image whose
repository matches no compose service is ignored, and a matching repository needs a digest that
exists in *your* registry — so the realistic damage is a forced redeploy or a rollback to an older
legitimate digest. Rejecting images whose registry host is unknown closes the rest; every accepted
release is audited with the caller IP.

Three properties of that gate are accepted rather than closed:

- **`docker.io` is always admitted** — an unqualified image lives there and a default install has no
  Hub credential to recognize it by, so a leaked token can pin any *public* Hub digest. The
  repository still has to match a compose service, the pin is pre-validated before it is applied, and
  the rollout is visible; those are the mitigations. Requiring Hub to be configured explicitly would
  make the common case fail closed for no gain against an attacker who already holds the token.
- **An enabled product answers 401 rather than 404.** The 404 covers "missing, disabled, no token",
  so an attacker enumerating ids learns which products have the webhook *switched on*. Accepted: the
  token is 256 bits of CSPRNG output, the disclosure is a boolean about configuration rather than
  about anything deployed, and collapsing 401 into 404 would leave a CI author with a wrong token
  unable to tell it from a wrong id.
- **Pre-authentication cost is bounded by a second limit.** The per-product window only engages once
  a caller has proved it holds the token — otherwise a stranger could lock a product's CI out by
  spending its budget — so a generous per-client-address window
  (`Watchtower:ReleaseWebhookClientRateLimitPerMinute`, default 60) is taken first, before the
  product lookup runs.

### The workflow step

Everything below comes from values Watchtower syncs (next section). `-sSf` makes a rejected release
fail the job — the feedback loop you want.

```yaml
- name: Report release to Watchtower
  if: github.ref == 'refs/heads/main'
  run: |
    curl -sSf -X POST \
      "${{ vars.WATCHTOWER_URL }}/api/webhooks/products/${{ vars.WATCHTOWER_PRODUCT_ID }}/release" \
      -H "Authorization: Bearer ${{ secrets.WATCHTOWER_RELEASE_TOKEN }}" \
      -H "Content-Type: application/json" \
      -d @- <<JSON
    {"commit":"${{ github.sha }}","branch":"${{ github.ref_name }}",
     "images":["${{ vars.REGISTRY }}/acme/api:${{ github.sha }}"],
     "runUrl":"${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }}"}
    JSON
```

A published composite action is future work, not v1: composite actions get no implicit secrets
context, so the token would still be hand-wired — the real win is the pre-filled snippet on the
product page (ids and URL already substituted).

### Secret sync

Generalize the orchestrator's `SyncRegistryAsync` pass into `SyncActionsConfigAsync(repo, ct)` with
two independent contributors and **independent hash guards** (a registry credential rotation must
not re-push the release token, and vice versa):

| Contributor | Name | Kind | Source | State lives on |
| --- | --- | --- | --- | --- |
| registry (exists) | `REGISTRY`, `REGISTRY_USERNAME`, `REGISTRY_PASSWORD` | variable + secrets | resolved registry view | `CiRepo` |
| release (new) | `WATCHTOWER_URL` | variable | `PublicBaseUrl` | `Product` |
| release (new) | `WATCHTOWER_PRODUCT_ID` | variable | `Product.Id` | `Product` |
| release (new) | `WATCHTOWER_RELEASE_TOKEN` | secret (sealed box) | `Product.ReleaseWebhookToken` | `Product` |

- Repo → products: parse `Product.RepositoryUrl` to `(owner, name)`, filter
  `SyncReleaseSecrets == true`. The filtered unique index makes the monorepo conflict (two products,
  one repo, fixed secret names) unrepresentable; the handler reports it in words. v2: name-suffixed
  secrets.
- Enabling `SyncReleaseSecrets` runs the existing PAT probe (`ValidateSecretsAccessAsync`) up
  front; token rotation and product saves clear the hash and `RequestReconcile()`, matching
  `ci.updateRepo`.
- `PublicBaseUrl` unset → durable error on the product, skip.
- Failure isolation: the release contributor gets its own try/catch inside the per-repo loop.
- Audit: `ci` / `release-token.sync`, actor-less, transitions-only on failure (the anti-eviction
  rule the registry sync already follows).
- **The manual fallback is equally prominent in the UI** (non-GitHub remotes, PATs without Secrets
  write): reveal + copy the token, with the exact GitHub settings path spelled out. A hobby user
  without an admin PAT must never hit a wall here.

## Deployment

### Two modes, one switch

`Product.ReleaseMode` selects the update mechanism — and the UI renders **only one of the two,
never both**:

- **`Git`** (default, and every migrated product): branch-HEAD clone, digest/commit polling,
  today's Updates panel and `AutoDeployMode` labels. Byte-for-byte today's behaviour.
- **`Releases`** (flipped on first accepted release; audited; operator-revertible): latest = the
  release with the highest `Id`; a deploy is that release's commit + digests, fully reproducible.

### Auto-deploy precedence

Reinterpret `AutoDeployMode`; add **no** second automation field. The rules, in order:

1. `DesiredState == Stopped` → nothing deploys (ADR-0025, unchanged). Fan-out *skips* stopped
   stacks in the query — no failed `DeployEvent` noise; the release view shows "skipped (stopped)".
2. `PinnedReleaseId != null` → **no automatic deploys of any kind.** A pin is an explicit "stay
   here"; release arrival, polling and the schedule window all skip it. Manual deploy redeploys the
   pin. Pinning is the opt-out from automation.
3. `ReleaseMode == Releases` → the release event is the trigger; `AutoDeployMode` keeps its three
   intents with the mechanism swapped from pull to push: `Off` = badge only; `OnChange` = deploy
   when a release arrives; `Scheduled` = newest release at the daily window. Git-head and
   registry-digest polling never trigger a deploy in this mode.
4. `ReleaseMode == Git` → exactly today's behaviour.

A second field would give operators two orthogonal automation switches whose combinations are
mostly nonsense. One consequence to accept: `AutoDeployMode` defaults to `Off`, so the UI (not the
model) defaults the selector to `OnChange` when creating a stack from a `Releases`-mode product.

### Convergent fan-out

A new `ReleaseRolloutService` fans out on release creation (and serves manual "deploy release to
all" actions):

```sql
SELECT id FROM stacks
WHERE product_id = @productId AND pinned_release_id IS NULL
  AND desired_state = 'Running' AND auto_deploy_mode = 'OnChange'
```

**Deploys are convergent, not imperative.** The enqueue carries no release id;
`ExecuteDeployAsync` resolves `PinnedReleaseId ?? newest` **at execution time**. That is what makes
the existing per-stack coalescing correct: two releases 30 s apart, with a deploy mid-flight,
collapse into one pending deploy that runs the newer release — never the superseded one.
`DeployEvent.ReleaseId` is stamped at execution, so a coalesced event reports what actually ran.
Trigger names: `"release"` (fan-out), `"release-manual"` (operator pin/deploy).

Why not fan out the *specific* release that triggered? Because a captured release id is the variant
with the race. With the gate draining a 200-stack fan-out over minutes, a v43 published mid-drain
interleaves with v42's still-queued payloads — a stack can run v43 and then its queued "deploy v42",
ending on a **downgrade** caused purely by queue timing. Guarding against that means "skip if newer
already deployed" logic, i.e. execution-time resolution rebuilt with more moving parts. The
convergent rule has no lost update instead: v43's fan-out enqueues only after its insert commits, so
every latest-tracking stack either resolves v43 directly or is re-enqueued by v43's fan-out (landing
in the pending slot) — provably ending on the true newest, never moving backwards. "Deploy exactly
this release regardless of what CI does next" is the *pin* intent, which is why the roll-out dialog
defaults to pinning.

One refinement: a release-triggered deploy short-circuits (completes as a no-op, noted in the event)
when the resolved release equals `LastDeployedReleaseId` — this absorbs the redundant re-deploy of a
stack that already converged early. Safe **only** for trigger `"release"`: manual, webhook and
scheduled deploys must never skip, because a deploy also converges config, env and compose changes.

**Global concurrency gate — a prerequisite, not a nicety.** `DeployQueueService` starts one worker
per stack with no global cap; `templates.deployAll` already makes that a latent problem and release
fan-out makes it routine (200 × clone + pull + up against one registry and one daemon). A
`SemaphoreSlim` around `ExecuteDeployAsync` (`Watchtower:MaxConcurrentDeploys`, default 4) bounds
cross-stack parallelism; per-stack queue semantics are unchanged.

Partial failure: per-stack `DeployEvent`s grouped by `ReleaseId` give the rollout view
(succeeded / failed / queued / skipped) plus "Retry failed" (re-enqueue failed ids only). No
automatic retry — a failing deploy usually fails identically, and auto-retry across 200 tenants is
a self-inflicted DoS.

### Clone at a commit

`git clone --depth 1 --branch` cannot check out an arbitrary SHA, and `--revision` needs git ≥
2.49. Portable form (new `GitCloneService.CloneAtCommitAsync`, virtual like its siblings):

```
git init <dir>
git -C <dir> fetch --depth 1 <authenticated-url> <sha>
git -C <dir> checkout FETCH_HEAD
```

Passing the URL to `fetch` keeps the token out of `.git/config` (argv-only, same exposure as
today's clone). Detached HEAD is fine; `rev-parse HEAD` still feeds `LastDeployedCommit`. Fetching
an arbitrary object needs `uploadpack.allowReachableSHA1InWant` (on at GitHub/GitLab/Gitea);
**fallback**: full `clone --branch <effectiveBranch>` + `checkout <sha>`, with a warning line in
the deploy output — slow but correct on plain self-hosted remotes. A release without a commit
clones the branch head but still pins digests.

### Image pinning

A new pure `ImageRef` type extracts the parsing currently inlined in
`DockerEngineClient.GetRemoteDigestAsync` (refactor that method onto it in the same change — a
second subtly-different parser is how pinning silently stops matching for someone's
`localhost:5000` registry):

1. Split off `@sha256:…`, then the tag as the last `:` after the last `/`.
2. First path segment is a registry host iff it contains `.` or `:` or equals `localhost`.
3. No host → `docker.io`; single-segment path → `library/<name>`; alias `index.docker.io` /
   `registry-1.docker.io` → `docker.io`; lowercase.

**Match rule:** a compose service is pinned iff its image's `CanonicalRepository` equals a
`ReleaseImage.Repository`. `postgres:16` → `docker.io/library/postgres` matches nothing and is
untouched — no allowlist needed. (`docker compose config` resolves `image:` after interpolation, so
`image: ghcr.io/acme/web:${TAG}` still matches on the repository part.)

**Label `watchtower.release-image`** (tri-state parsing discipline of `watchtower.inject-token`):
`"false"` → never rewritten even on a match (a service deliberately running a published tag);
`"true"` with no match → warning in the deploy output and on the release, deploy continues (failing
would take a fleet down because someone added a service); absent → match by repository; unparseable
→ warning, treated as absent. Build-only services (no `image`) are skipped; the existing
profile-gating caveat of `docker compose config` applies.

**Rendering** extends the ADR-0012 seam rather than adding a second mechanism:
`ComposeOverrideFile.ParseServices` returns `(Name, Image, InjectTokenLabel, ReleaseImageLabel)`;
a new pure `ImagePinPlan.Create(services, releaseImages)` (runtime-neutral, like
`EnvInjectionPlan`; a Kubernetes engine consumes it as container image fields) produces
`ServiceImagePin` rows + warnings; `ComposeOverrideFile.Render(envPlan, imagePlan)` merges both
into the one generated override — compose merges it after the repo's file, so `image:` wins per
service by the same mechanism `environment:` already relies on.

Deploy output names everything (digests are not secrets):

```
[Watchtower] Deploying release v2026.8.24-142 (commit a1b2c3d4)
[Watchtower] Pinning service 'api' to ghcr.io/acme/api@sha256:9f2c…
[Watchtower] Warning: service 'jobs' is labelled watchtower.release-image but this release has no image for ghcr.io/acme/jobs
```

A pinned digest that was garbage-collected from the registry fails loudly at `compose pull` for
that one stack — acceptable for the automatic path. **Pinning pre-validates instead**: pin/rollback
actions HEAD every image of the target release first and refuse with `409` naming the missing one —
a pre-flight refusal beats a mid-rollback surprise.

### Update checks and drift

In `Releases` mode, `StackUpdateService` replaces two of its three checks:

1. **Release availability** replaces registry-digest polling: `AvailableReleaseId` = newest release
   ≠ `LastDeployedReleaseId`. `HasUpdates` now means "a newer release exists". The per-tenant
   registry HEAD storm (200 tenants × k images per interval) disappears entirely.
2. **Drift** becomes local: do the running containers' `RepoDigests` match the deployed release?
   Pure `docker inspect` — it answers "is this stack really running v42?" instead of comparing a
   pinned digest against a moving tag (which would report "outdated" forever and fight the pin).
3. **Git head** stays informational: "unreleased commits on main", never a trigger.

Pinned stacks skip the git check entirely. `AutoDeployBackgroundService` gains a mode branch:
`OnChange` stacks are skipped in `Releases` mode (the webhook is their trigger); `Scheduled`
compares newest vs `LastDeployedReleaseId`.

**The first-release transition is visible, not special-cased.** The awkward case: a stack was
deploying branch HEAD (commit N); the first release is for N−1 because CI started before the last
push; the release deploy checks out N−1 — correct under the new model, surprising to the operator.
No exemption rule (nobody would remember it in six months); instead the mode flip is audited and
announced on the product page, the deploy output names the commit, and the product page warns when
latest's commit is not the branch head ("2 commits on main since v1"). It self-corrects on the next
release.

## Rollback and canary

`stacks.setRelease(stackId, releaseId | null)`: null → clear the pin, track latest, deploy latest;
an id → pre-validate images, pin, deploy (trigger `"release-manual"`).
`templates.setTenantsRelease(templateId, releaseId | null, deploy)` writes the pin onto every
tenant *and* stores it as the template default for future tenants; individual tenants can still be
pinned independently (per-tenant hotfix without leaving the fleet default).

Rollback is "pin to an older release" — and because a release stores its commit, the *checkout*
rolls back too: compose changes, entrypoint scripts, migration files travel with the code.

**Database caveat (prominent in user-facing docs).** Watchtower rolls back code and images; it
cannot roll back the application's database. Pinning back across a destructive schema migration
may fail at startup or corrupt data, and neither Watchtower nor Compose can detect it. The
operational answer is the backup feature: take a fleet backup before a risky rollout (the roll-out
dialog offers exactly that — see below), restore alongside the pin, and prefer expand/contract
migrations (v43 only adds, v44 removes) — the pattern that makes rollback actually safe.

**Canary** composes from the primitives, no dedicated feature: keep tenants on `Off`, deploy one
manually, then roll the release out to the rest. Documented as a supported workflow.

## Tenancy

`TenantProvisioningService.ProvisionAsync` sets `ProductId = template.ProductId` (**reference, not
copy** — the copy-at-provision bug disappears by construction), `BranchOverride = null` (inherit),
`PinnedReleaseId = template.DefaultPinnedReleaseId` (copied: per-tenant policy). The slug/domain/
project-name checks, single transaction, route creation and post-commit enqueue are untouched.

- `templates.update` loses the repo fields, gains `BranchOverride` and the release defaults.
  Changing a template's `ProductId` while it has tenants is **refused** (it would repoint every
  tenant at a different codebase; the realm-change refusal establishes the message shape).
- `templates.deployAll` keeps its mechanics and *becomes meaningful*: with convergent deploys it
  rolls the product's current latest out to every tenant instead of "whatever HEAD is now".
- Template deletion still detaches tenants (`TemplateId` SET NULL) and they still work — now
  because they hold their own `ProductId`.

Per-tenant subdomain routing is unchanged: one `Route` row per tenant from `DomainPattern`
(exact-host matching; wildcard certs via DNS-01 remain the known future answer to the Let's
Encrypt per-domain rate limit — see ADR-0022).

## Backups across tenants

Backups today are entirely stack-scoped and template-blind. The extension builds on existing seams:

- **Per-tenant storage folders.** A persisted `Stack.BackupDirectory` column, set at creation,
  replaces the on-the-fly `BackupNaming.StackDirectory(instance, stackName)` at all three
  path-composition sites (run, restore, remote listing) and in retention. Tenants get
  `{instance}/{productName}/{tenantSlug}/`; standalone stacks keep `{instance}/{stackName}/`. Side
  benefit: renaming a stack no longer orphans its archives (a pre-existing hazard).
  **The column is nullable and the migration backfills nothing** — null means "compute it as we always
  did", which keeps every existing archive discoverable without a migration guessing at a value SQL
  cannot see: the instance name is *configuration* (`Backup:InstanceName`, defaulting to the machine
  name), not a column. A legacy stack is stamped with that computed path after its next *successful*
  backup, which is the moment the value is known to be where the bytes really went. The reasoning, and
  the consequence that a stamped stack no longer follows an instance rename, are invariant 20 in
  [implementation-status.md](implementation-status.md#invariants--do-not-break-these).
- **Policy once, inherited live.** Template-level backup policy mirrors the stack fields with the
  "null = inherit" idiom `BackupCron` already uses: `StackTemplate.BackupEnabled?/BackupCron?/
  BackupStopContainers?/BackupQuiesceMode?` plus template-level service overrides
  `(TemplateId, Service)`. Stack fields become nullable tri-state. Resolution is the ADR-0020
  ladder extended: **compose label > stack override > template policy > instance default.** The
  stack Backups tab shows provenance ("Set by: template policy") reusing the plan preview's
  "Set by" pattern.
- **Fleet operations.** `templates.backupAll` mirrors `templates.deployAll` (the process-wide
  serial backup queue makes N tenants sequential — document duration expectations). **Release
  tie-in:** the roll-out dialog offers *"Back up each instance before deploying"* — the rollout
  service enqueues a backup with new trigger `"pre-deploy"` and chains the deploy on backup
  success; a failed pre-backup blocks that tenant's deploy. This is the operational answer to the
  database-rollback caveat.
- **Manifest** gains additive keys (`productId/productName/templateId/tenantSlug` + the deployed
  release version; `formatVersion` bump precedented). A backup then names the release it captured —
  restore instructions become "restore this archive **and** pin release vX".
- **Restore** stays same-stack. Tenant removal offers a final backup before teardown.
  Restore-into-a-*new* tenant (clone/migrate from backup) is future work: cross-directory restore
  plus manifest-driven volume mapping.
- **UI.** The backups module contributes a product-detail tab: template policy card, fleet rollup
  ("19 backed up in last 24 h · 1 failed · 2 never"), fleet history via the
  already-existing-but-unconsumed `backups.events(stackId: null)` filtered per product. The stack
  tab is unchanged except provenance labels.

## UX

The quality bar: self-explanatory, professional, *übersichtlich / geordnet / nicht überladen*. The
one sentence the whole IA hangs on is the model sentence itself — definition on the product,
runtime on the stack.

### Navigation

```
DEPLOYMENT
  Products   order 15   (mobile: false — status-checking stays a stack concern)
  Stacks     order 20
  Routes     order 25
```

**Templates leaves the sidebar**: a template was always "a product plus tenancy rules" and becomes
the product's tenancy setup on its Instances tab; `/templates*` routes redirect. Net sidebar count
is unchanged for a tenancy deployment and +1 otherwise. `/stacks` stays the flat "what is running
on this box at 3 a.m." list, gaining only a Product column. The Routes page groups tenant routes
under a collapsible "Managed by product *X* (20 routes)" so manual routes stay findable.

| Page | Owns | Does not own |
| --- | --- | --- |
| `/products` | Catalogue: product, repository, instance count, latest release (+ aggregate "1 failing" chip only when true) | Runtime status detail |
| `/products/$id` | Source, CI, releases, tenancy config, instance roster | Containers, logs, volumes |
| `/stacks`, `/stacks/$id` | The running copies: containers, domains, history, version policy | Repo URL, branch, compose path, credential (read-only link) |

### Hobby flow — "deploy my repo"

Entry stays `/stacks/new`; the button everywhere stays **New stack**. When ≥1 product exists the
Source card offers `New git repository` (default) / `Existing product`; with zero products it is
just the plain repo form. Stack name auto-fills from the repo path; the deploy webhook moves under
a collapsed **Advanced** disclosure (the one field a first-timer can't evaluate). The entire
product education for this persona is one quiet footer sentence:

> "Watchtower saves this repository as a **product** — add CI, releases or more instances later
> without repeating yourself."

After submit: `/stacks/$id` exactly as today; the only header change is the source becoming a link
to the product. CI is offered on the product page only (Overview tile + Releases empty state),
**never** in the create or first-deploy flow. Word budget for this persona: three passive touches
of "product", zero required interactions, zero modals — enforced in review.

### SaaS flow

1. **`/products/new`** — one card: name (auto-filled), repo URL, branch, compose path, credential.
   No mode question; those choices teach better after the object exists.
2. The product opens on a **Next steps** card (rendered only while it has no instances, releases or
   CI; the key teaching screen): *Deploy it once* / *Run it for many tenants* ("One isolated copy
   per customer, each on its own subdomain") / *Build it here*. Three rows, three sentences, three
   buttons.
3. **Enable CI** — today's `EnableCiCard` verbatim, new home.
4. **Set up tenancy** — today's template form minus the entire Source card. The `{tenant}` pattern
   is taught by a **live preview** under the input (`acme.example.com · globex.example.com`), not
   prose; the validation error reads the same way. Saved config collapses to a summary line above
   the tenant list (`{tenant}.example.com → web:3000 · 4 base env vars · [Edit]`).
5. **Add tenants** — today's slug row with its live resolved-domain hint, kept as-is.
6. **Version policy** — per-row `⋯ → Change version`, or the bulk **Roll out release…** dialog.

### Product detail page

Route `/products/$id?tab=`, structured like `StackDetailPage` via a new `productDetailTabs`
extension point (CI contributes its tab *there* instead of to `stackDetailTabs`; tenancy
contributes into Instances). Header: name, mono source line, instance-count badge, and **one**
state-dependent primary button (0 instances → Create deployment; 1 → Deploy; ≥2 with releases →
Roll out release…). Hard caps: **6 tabs**, ≤4 sections each.

> **The cap was 5 and is now 6 — an explicit amendment, not a slip.** This section was written before
> tenant-aware backups existed; stage 7 added a **Backups** tab, which took the count to six once stage
> 8b folded tenancy in as **Instances**. It is raised rather than enforced by merging two tabs because
> each of the six is a *distinct module's* contribution to `productDetailTabs` (products owns Overview,
> Releases and Settings; tenancy contributes Instances; ci contributes CI; backups contributes Backups),
> so any merge would put one module's screen inside another's — the thing the extension point exists to
> prevent. The count is also the *maximum*: a deployment with Tenancy, CI or Backups switched off sees
> five, four or three, because a tab is only contributed by a module that is enabled.

1. **Overview** — three StatCards (Instances / Latest release / Builds), the deployments card (a
   single instance renders as one card, never a one-row table — opening a product must never be a
   dead hop), last 3 releases, Next-steps card in the fully-empty case.
2. **Releases** — newest first; row = `v1.4.0` `latest` · `a1b2c3d` · age · image count · "in use
   by N instances", expandable to the digest table; filter chips All / In use / Unused; 20 rows +
   "Show older". Row menu labels are contextual — "Deploy this release" vs **"Roll back to this
   release"** — so the consequence is stated before the click. The roll-out dialog: segmented
   *Pin to v1.4.0* / *Set to track latest*, instance checklist with current versions, the
   pre-rollout-backup checkbox, and a live consequence sentence ("3 instances will be pinned to
   v1.4.0 and deployed").
3. **Instances** — tenancy summary card, add-tenant row, roster with **Version** column
   (`latest` / `v1.2.3` + `pinned` badge), a `Behind` column populated only when pinned-and-behind,
   status, last deployed. Above it a rollup that doubles as a filter: "18 on latest · 2 pinned ·
   1 behind" — the "which tenant runs which version" answer in one screen.
4. **CI** — today's `StackCiTab` moved verbatim (its copy already says "shared by every stack
   deploying this repo"; it was never about the stack). This also finally homes the five `ci.*`
   RPCs that have no UI today.
5. **Settings** — the fields that left stack Settings, plus "Used by 3 deployments" next to the
   repo group and an enumerating save-confirm ("Saving changes the source for 3 deployments. They
   keep running until redeployed.").

**Release setup teaching** lives in the Releases empty state: one definition sentence ("A release
is one build of this product: the git commit plus the image digests your CI produced"), then a
*Report a release from CI* card in the exact shape of the existing Webhook/RegistrySync cards —
token row with sync badge reusing the registry-sync vocabulary (`synced` / `sync pending` /
`sync failed` + "last synced 3 minutes ago"), the equally-prominent manual fallback when sync isn't
possible, the canonical curl snippet with ids pre-filled, and a collapsed "What this sends". After
the first release the whole card collapses to one link.

### Stack detail

- **Header invariant — the load-bearing rule:** *the header always states the version the Deploy
  button will apply.* `myapp · main @ a1b2c3d` (Git mode) / `myapp · v1.4.0 (latest)` /
  `myapp · v1.2.0 pinned`. If any surface ever shows Deploy without its version visible (including
  the mobile FAB), the feature becomes untrustworthy — a review checklist item.
- The version fragment is a button opening the **Version dialog** (pinning is operational, not
  Settings): `(•) Track latest — "Deploys the newest release as soon as it's built. Currently
  v1.4.0."` / `( ) Pin to a release` + select with ages. Two buttons: **Save** and
  **Save & deploy**. The radio labels *are* the explanation; nothing else is needed.
- **Drift:** tracking-latest + behind → info banner "v1.4.0 is available" with Deploy. Pinned +
  behind → a quiet header chip (`pinned` `3 behind`), **never a banner** — nagging someone for a
  deliberate choice is how a tool starts feeling hostile. Up to date → silence.
- Overview: the Updates panel is *replaced* by a **Version** panel in `Releases` mode (current
  release, policy, available-release line; no per-image digest list, no "Check now" — releases are
  pushed, not polled). Exactly one of the two panels ever renders. While `releaseCount == 1` a
  single self-clearing line announces the transition.
- Settings: repo fields replaced by a read-only "From product **myapp** — … [Edit product]" row in
  the same position (never delete a control someone has used; demote it and point at its new home)
  plus the `BranchOverride` field. "Automatic deployment" is re-labelled **"Automatic rollout"** in
  `Releases` mode (`Off` / *When a new release is published* / *Daily at a fixed time*) and is
  **disabled with the reason inline** while pinned — never hidden.
- Tab changes: CI leaves for the product (6 → 5 tabs, all runtime concerns); the known
  missing-domains gap is filled by a Domains *section* on Overview, not a tab.

### Explanation strategy

| Surface | Used for | Rule |
| --- | --- | --- |
| Field hint | Anything you type | Format, default or consequence, ≤ 12 words |
| Empty state | Teaching a concept | Title = the missing thing; one defining sentence; the action |
| Banner | Actionable/abnormal states only | Never education |
| Live preview | `{tenant}` pattern, resolved domains | Beats every explanatory sentence |
| Tooltip | Icon-only actions, why-disabled reasons | Reuse the focusable-span idiom |
| Docs link | The user's actual question | Max one per page, phrased as a question |

**No "i" info popovers** — they hide what a first-timer needs behind a click they don't know to
make and add noise for the expert. If a concept needs a popover, the concept is in the wrong place.
Each new noun is taught in exactly one primary place: product → the stack-create footer sentence;
release → the Releases empty state; latest/pin → the two radio labels; tenant → the Next-steps row;
domain pattern → the live preview.

### Übersichtlichkeit audit

| Risk | Fix |
| --- | --- |
| Stack Settings (5 heavy sections, repo fields duplicated 3×) | Repo fields → product; read-only Source row in place; "Authentication" renamed *Deploy webhook* |
| Product detail ballooning | Hard caps (**6 tabs** / ≤4 sections — raised from 5 when Backups joined Instances, CI and the products module's three; see §Product detail page for why it is not a merge); snippet card collapses after first release |
| Products page becoming a second Stacks page | 4 columns only, no status column (status belongs to instances) |
| Two competing update mechanisms | The `ReleaseMode` binary — only one panel ever renders |
| Routes page drowning in tenant routes | Collapsible per-product group |
| Releases list unbounded | 20 + "Show older", digests behind row expansion |
| Instances tab carrying three jobs | Config collapsed to a summary card; the tab most likely to split later |
| Mobile: 5-column instance table | Card fallback leading with slug + version + status |

### Migration morning-after

Stacks, stack detail, Routes and the dashboard are visually unchanged except the Product column and
the header link. **No migrated product has releases, so everything new is dormant until opted in —
an explicit acceptance criterion.** The one genuinely jarring change, Templates → Products in the
sidebar, gets three mitigations: redirects, a one-time info banner on Products ("Templates moved
here — a template is now a product's tenancy setup; yours are unchanged under each product's
Instances tab"), and a `tenants` badge on migrated tenancy products. The upgrade note rides the
existing update-banner surface; no new announcement mechanism.

## RPC surface

New `Products` module (`[AppModule("Products")]`, the `Ci`/`Tenancy` layout):

- `products.list` / `.get` / `.create` / `.update` / `.delete` (delete refused with blockers named)
- `products.listStacks` — stacks + templates using it, with tracking state
- `products.listReleases` / `.getRelease` / `.createRelease` (manual — useful for adopting the
  model before CI is wired) / `.deleteRelease` (blocked while pinned) / `.deployRelease`
- `products.rotateReleaseToken` / `.setReleaseWebhook`

Changed elsewhere: `stacks.setRelease(stackId, releaseId | null)`;
`templates.setTenantsRelease(templateId, releaseId | null, deploy)`; `templates.backupAll`;
`ci.enableForProduct` / `ci.getProductCi` (with `ci.getStackCi` as a thin forward for one release).
Non-RPC: the release webhook endpoint.

**Back-compat:** `StackDto` keeps `repositoryUrl`/`branch`/`composeFilePath`/`credentialId` as
read-only projections of the *effective* source and gains `productId`, `productName`,
`branchOverride`, `trackingMode`, `pinnedReleaseId`, `lastDeployedRelease` — zero churn for
existing readers (frontend, mgmt API, scripts). `stacks.create` keeps the inline repo fields and
**find-or-creates** the product by the migration's normalization rule (this *is* the hobby UX);
supplying both `productId` and repo fields is a validation error. `stacks.update` compares repo
fields against effective values (the frontend posts whole objects, so presence-based rejection
would break every save): a *changed* field is an error pointing at `products.update`, except
`branch`, which maps to `BranchOverride`. `rpc-schema.json` regeneration is part of every stage.

## Audit

| Category | Actions |
| --- | --- |
| `products` | `product.create/update/delete` (field diffs; repo-URL change called out), `product.credential.change`, `release.publish` (actor-less, one row per release — target `product/version`, detail: source, commit, image count, stacks enqueued), `release.delete`, `release.prune`, `release.token.rotate`, `release.webhook.toggle`, `release.mode.change` |
| `stacks` | `release.pin` / `release.unpin` (before → after), `release.pin.bulk` (template, tenant count) |
| `ci` | `release-token.sync` (transitions-only on failure, like `registry.sync`) |
| `backups` | `backup.all` (template fan-out, one row), the `pre-deploy` trigger on per-stack rows |

Fix in passing: registry CRUD is currently unaudited — an operator can rotate the credential behind
a synced registry with no attributed record.

## Migration

One EF migration (`migrationBuilder.Sql`, deterministic, transactional with the NOT NULL):

1. Create `products` / `releases` / `release_images`; add nullable `product_id` columns.
2. Insert one product per **normalized `(RepositoryUrl, ComposeFilePath)`** across
   `stacks ∪ stack_templates` (URL: trim, lowercase host, strip `.git` and trailing `/`; path: the
   deploy's existing `TrimStart('/', '\\')`). Branch differences become `BranchOverride` — keying
   on branch would fork the product list into duplicates that then diverge; one product per stack
   would create 40 products for a 40-tenant template. With this rule, a template's tenants (today
   carrying *identical* copied fields) collapse onto one product — the propagation fix landing for
   free. `default_branch`/`credential_id` from the `min(id)` representative; names derived from the
   repo path, disambiguated by compose directory, then numeric suffix.
3. Point `stacks` / `stack_templates` at their product; set `branch_override` where the branch
   differed.
4. Drop the four source columns from both tables; set `product_id` NOT NULL.

`ci_repo_id` stays null and is resolved lazily on first read (URL parsing in SQL isn't worth it).
A best-effort `Down` recreates the columns from the product join.

**The SQLite import path is dead and deliberately not taught about this.** Every known Watchtower
instance has already migrated to PostgreSQL (confirmed 2026-08-25), so `SqliteImporter` gets no
`product_id` conversion step — a legacy import would now fail at the NOT NULL, which is acceptable
for a path with no remaining users. Removing the importer (and its upgrade docs) entirely is a
separate cleanup task outside this roadmap.

## Staged roadmap

Each stage merges to main independently and leaves the product working; nothing behaves differently
for existing installs until a product enters `Releases` mode.

| Stage | Contents |
| --- | --- |
| **0 — pure refactors** | `ImageRef` extraction + `GetRemoteDigestAsync` refactor; `ComposeOverrideFile.ParseServices` returns image + labels; `GitCloneService.CloneAtCommitAsync` + fallback; the global deploy-concurrency gate. (The `Stack.BackupDirectory` column + backfill from stage 7 may land here too — independently valuable against the rename hazard.) |
| **1 — Product, no releases** | Entity, FKs, backfill migration, SQLite-importer step, `ProductSourceResolver`, deploy reads the product, `products.*` CRUD, products frontend module, `stacks.create` find-or-create, provisioning by reference. **Template propagation is fixed here and nothing else changes.** |
| **2 — CI link** | `Product.CiRepoId`, `ci.enableForProduct`/`getProductCi`, CI tab moves to the product page, `CiToolchainRecorder` keyed by product. |
| **3 — Releases, read-only** | `Release`/`ReleaseImage`, the webhook endpoint (resolution, validation, idempotency, rate limit), manual create, Releases tab. Point a real workflow at it and watch releases accumulate with zero deployment risk. |
| **4 — Release-aware deploys** | Pin fields, `ReleaseResolver`, `ImagePinPlan` + `Render` extension, `stacks.setRelease`, `ReleaseMode` flip, fan-out via `ReleaseRolloutService`, the `AutoDeployMode` reinterpretation, release-mode update checks, Version panel/dialog. **The behaviour-changing stage; the zero-releases guarantee is the acceptance test.** |
| **5 — Secret sync** | `SyncActionsConfigAsync`, product sync state + PAT probe, snippet UI. (Until then, operators paste the token by hand — a fine intermediate state.) |
| **6 — Tenant release policy** | Template defaults, `templates.setTenantsRelease`, Version/Behind columns + rollup, rollout view + retry-failed, release pruning. |
| **7 — Tenant-aware backups** | `Stack.BackupDirectory` (if not landed at 0), per-tenant folder layout, template backup policy + nullable stack fields, `templates.backupAll`, pre-rollout backup chaining, manifest keys, product Backups tab. |

## Risks and open questions

1. **Fetch-by-SHA portability** — the full-clone fallback covers servers without
   `allowReachableSHA1InWant`; test against a plain self-hosted remote and measure the fallback on
   a large repo before stage 4.
2. **First-release flip** — publishing release #1 moves every latest-tracking stack from
   branch-HEAD to release mode, possibly to an older commit than currently running. Visible and
   audited, not prevented; a `ReleasesEnabled` gate is the fallback if it proves too implicit.
3. **Monorepo secret collision** — schema-prevented (filtered unique index); the UI must explain
   the refusal, not surface a mystery error. v2: suffixed secret names.
4. **Ancestry-unaware ordering** — a rerun of an old commit's workflow creates a newer release of
   an older tree. Mitigated by the required-branch check and the "latest ≠ branch head" warning;
   ancestry-aware ordering is future work.
5. **Multi-arch digests** — pinning must use the manifest *index* digest; verify
   `GetRemoteDigestAsync` returns it before stage 4.
6. **Mirror hosts** — `ghcr.io/acme/web` behind a pull-through mirror won't repository-match; the
   label is the escape hatch, and the normalization rules must be documented, not implicit.
7. **`stacks.update` contract change** — API clients that repoint a stack's repo get a validation
   error naming `products.update`; needs a release-note line.
8. **Products vs Stacks reading as two lists of the same thing** — the highest-severity UX risk;
   mitigated by the definition/runtime split, disjoint columns, and the 1-instance collapse. Worth
   a "where do I change the branch?" usability test.
9. **Shared products surprise editors** — editing a migrated product's branch changes several
   stacks at next deploy; the usage count + enumerating save-confirm are the mitigation, and it is
   still the most likely post-upgrade support ticket.
10. **Pinned tenants rot** — deliberately not nagged; the "2 pinned" rollup is the visibility. A
    dashboard section is a possible later addition.
11. **Release retention** — `RetainReleases` must never prune pinned/recently-deployed releases;
    `deploy_events` has no retention today either (pre-existing gap, noted).
12. **Backup fan-out duration** — the serial backup queue makes pre-rollout fleet backups
    sequential; surface expected duration in the dialog.
