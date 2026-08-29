# CI Runners — self-hosted GitHub Actions runners managed by Watchtower

Status: draft (2026-08-02); amended 2026-08-18 with the per-stack CI section
([Stack-linked CI](#stack-linked-ci-toolchain-detection--cache-pre-warming)) — implemented.

## Motivation

Today every repository that needs a self-hosted runner means manual ceremony: create the
runner on github.com, copy a registration token into a VM, run `config.sh`, keep the VM's
toolchains and runner version alive. Watchtower already owns the Docker host — it can absorb
the entire ceremony: **add a repo in Watchtower, builds run locally in ephemeral containers,
build status and logs show up in the Watchtower UI** (Blacksmith-like experience, fully
self-hosted).

Key enabler: GitHub's just-in-time runner API
(`POST /repos/{owner}/{repo}/actions/runners/generate-jitconfig`) returns a single-use
runner config. With one fine-grained PAT, Watchtower can mint ephemeral runners per repo
with no tokens copied and nothing persisted inside the runner.

## Goals

- Zero-ceremony enablement: pick a repo, builds run on this box.
- Ephemeral, clean runner per job (GitHub-hosted semantics; no snowflake VM).
- Watchtower as the UI: run list, job/step status, logs per repo.
- No secrets inside runner containers — the PAT never leaves Watchtower.
- Fits the single-host architecture: each Watchtower instance is a self-contained CI
  island for the repos enabled on it. No instance-to-instance communication.

## Non-goals (v1)

- Multi-host federation or remote Docker endpoints. Cross-box behavior comes free via
  GitHub's job queue if the same repo is ever enabled on two instances (see
  [Multi-instance behavior](#multi-instance-behavior)).
- Replacing GitHub Actions (workflows stay `.github/workflows/*.yml`; the GHA ecosystem
  keeps working).
- Organization-level runners (personal repos can't share runners; per-repo JIT registration
  is the mechanism).
- Live step-by-step log streaming (v1 shows live job/step *status* + full logs on
  completion; live tail is future work, see below).

## Architecture

```
┌─────────────────────────── Watchtower instance ───────────────────────────┐
│                                                                           │
│  Ci module (handlers, ci.*)      RunnerOrchestrator (BackgroundService)   │
│        │                                 │ reconcile loop                 │
│        ▼                                 ▼                                │
│  GitHubApiClient ──────────────► DockerEngineClient                       │
│   (JIT config, runs,              (create/start/wait/remove               │
│    jobs, logs, repos)              runner containers)                     │
└──────────┬────────────────────────────────┬──────────────────────────────┘
           │ HTTPS (PAT)                    │ /var/run/docker.sock
           ▼                                ▼
      api.github.com                 ephemeral runner containers
      (job queue = the               (ghcr.io/actions/actions-runner,
       cross-box scheduler)           one job each, long-poll GitHub)
```

- **No webhooks, no commit polling for triggering.** An idle ephemeral runner long-polls
  GitHub itself; when a job is queued, GitHub hands it to the runner. Watchtower only
  keeps the desired number of runner slots alive.
- **GitHub is the source of truth for builds.** Run/job/log data is fetched from the
  GitHub API on demand (UI polling), not mirrored into the database. The only persistent state
  is the repo enablement config.
- **Runner containers are tracked via Docker labels** (like stacks/tenants are), not a DB
  table — state is reconstructable from `ListContainersByLabelsAsync` + the GitHub API
  after a Watchtower restart.

## New module: `Ci`

`src/Watchtower.Application/Modules/Ci/` following the existing module shape
(`[AppModule("Ci")]`, handlers under `Handlers/`, `CiContracts.cs`, `CiJsonContext.cs`).

### Entity: `CiRepo`

| Column | Notes |
| --- | --- |
| `Id` | int PK |
| `Owner`, `Name` | GitHub `owner/repo` |
| `CredentialId` | FK → existing `Credential` (reused for the PAT; no new secret store) |
| `Enabled` | orchestrator only reconciles enabled repos |
| `MaxConcurrentRunners` | runner slots for this repo (default 1) — the one capacity knob |
| `RunnerImage` | nullable; overrides the instance default image |
| `ExtraLabels` | nullable CSV; extra runner labels beyond the defaults |
| `AllowDockerSocket` | opt-in: mount `/var/run/docker.sock` into runners (see Security) |
| `CreatedAt` | |

Unique index on (`Owner`, `Name`).

### Services

**`GitHubApiClient`** (singleton, `HttpClient` against `api.github.com`, auth per call from
the repo's `Credential`):

- `GenerateJitConfigAsync(owner, repo, runnerName, labels)` →
  `POST /repos/{o}/{r}/actions/runners/generate-jitconfig`; returns
  `{ runner.id, encoded_jit_config }`.
- `DeleteRunnerAsync(owner, repo, runnerId)` — best-effort cleanup when a slot is torn down
  while idle.
- `ListWorkflowRunsAsync`, `ListJobsAsync(runId)`, `GetJobLogsAsync(jobId)` — read side
  for the UI.
- `ListUserReposAsync` — for the add-repo picker.
- `ValidateCredentialAsync(owner, repo)` — permission probe used by `ci.addRepo` so a
  wrong-scoped PAT fails at configuration time, not at reconcile time.

**`RunnerOrchestrator`** (`BackgroundService`, registered in
`WatchtowerServiceCollectionExtensions` like `AutoDeployBackgroundService`):

Reconcile loop (default every 15 s, plus immediate wake on config changes):

1. Load enabled `CiRepo`s.
2. `ListContainersByLabelsAsync("watchtower.ci.repo=<owner>/<name>")`.
3. **Exited containers**: read exit code (job finished → normal for ephemeral runners;
   non-zero without having taken a job → count as a failure for backoff), remove
   container.
4. **Missing slots** (`running < MaxConcurrentRunners`): mint a JIT config, create +
   start a runner container.
5. **Disabled/removed repos**: stop + remove their runner containers, best-effort
   `DeleteRunnerAsync` for idle runners.
6. Per-repo exponential backoff on repeated failures (bad PAT, image pull failure);
   error state surfaced via `ci.getRunnerStatus` instead of log-only.

Runner container spec:

- Image: `RunnerImage` ?? instance default (`ghcr.io/actions/actions-runner:latest`
  pinned by digest check like stack images; a custom image with the house toolchain —
  .NET, Node — is the intended end state, see Caching).
- Cmd: `run.sh --jitconfig <encoded>` (single-use; the config is worthless after the
  first job, so its visibility in `docker inspect` is acceptable).
- Labels: `watchtower.ci.repo`, `watchtower.ci.slot`, `watchtower.managed=true`.
- Runner name: `watchtower-{instance}-{repo}-{shortid}` — instance-qualified so the
  origin box is visible in GitHub's UI and in a future overlapping-scopes setup.
- Runner labels: `self-hosted` + `watchtower` + `{instance}` + `ExtraLabels`. Existing
  workflows using `runs-on: self-hosted` keep working unchanged.
- Mounts: cache volumes (below); `/var/run/docker.sock` only when `AllowDockerSocket` — together
  with `GroupAdd` = Watchtower's own supplementary group ids (`/proc/self/status`, the same
  mechanism the self-update coordinator uses). The socket belongs to the host's `docker` group,
  while the runner image gives its non-root `runner` user a `docker` group with a hardcoded id of
  123; without the host ids the socket is mounted but unusable ("permission denied while trying to
  connect to the Docker daemon socket").
- Labels also carry `watchtower.ci.spec-hash`, the hash of the settings baked into the container
  (image, docker socket, extra labels). Idle runners are long-lived — they sit long-polling GitHub
  until a job arrives — so a settings change would otherwise only take effect after the current
  runner consumed one more job. On a mismatch the reconcile loop retires the runner: it deletes the
  registration at GitHub first, which doubles as the idleness check (GitHub refuses to delete a
  runner that is executing a job), and only then removes the container. A busy runner is left alone
  and replaced after it exits on its own.

### Caching (avoiding the ephemeral-runner slowdown)

The manually-managed VM had warm caches; a fresh container has none. Per-repo named
volumes mounted into every runner of that repo (implemented):

- `watchtower-ci-tool-{repo}` → `/opt/hostedtoolcache`, exposed as `RUNNER_TOOL_CACHE`
  (setup-* action toolcache, same path GitHub-hosted images use; also
  `DOTNET_INSTALL_DIR={toolcache}/dotnet`, because setup-dotnet installs to that env
  var's dir rather than `RUNNER_TOOL_CACHE`)
- `watchtower-ci-pkg-{repo}` → `/home/runner/_pkg`, exposed via runner env inherited by
  job steps: `NUGET_PACKAGES=…/nuget`, `npm_config_cache=…/npm`, `GOMODCACHE=…/gomod`

No mount may live under `/home/runner/_work`: dockerd creates missing mountpoint parents
as root, and a root-owned `_work` breaks the runner user's `_work/_temp` creation at job
start (`UnauthorizedAccessException` in `TempDirectoryManager`). Fresh named volumes are
root-owned too, so before first use the orchestrator runs a one-shot root container
(`watchtower.managed=ci-volume-init`) that chowns the two volume roots to the `runner`
user — once per repo per orchestrator lifetime, idempotent.

The workspace itself stays ephemeral (clean checkout per job). Volumes are removable via
the existing Volumes module; GC/pruning is future work. Pre-warming the toolcache from the
detected toolchain profile is described in
[Stack-linked CI](#stack-linked-ci-toolchain-detection--cache-pre-warming).

### Container image builds (BuildKit defaults; issue #65)

A job that builds an image with `docker/setup-buildx-action` gets the `docker-container`
driver: a BuildKit daemon in its own container, which reads **none** of the host daemon's
configuration. Two host facts then leak into every consuming repo's workflow YAML unless
Watchtower delivers them itself:

- **Plain-HTTP registries.** The daemon's `insecure-registries` setting doesn't reach the
  out-of-daemon BuildKit, so pushing to a local registry needs a `buildkitd-config-inline`
  stanza in every workflow.
- **The snapshotter.** BuildKit's OCI worker probes `auto` → overlayfs → fuse-overlayfs →
  `native`, each with a functional check. On Synology DSM's 4.4 kernel both checks fail
  (no overlayfs; the fuse-overlayfs test mount fails too) and it lands on `native`, which
  has no copy-on-write: every layer materialisation is a full recursive copy of the
  accumulated image tree, and builds run ~10× slower with nothing in the job log saying
  why (the tell is `org.mobyproject.buildkit.worker.snapshotter: native` in the
  `Set up Docker Buildx` output; the symptom is cache-*hit* steps spending minutes in
  `extracting`).

**Mechanism — a default buildkitd config shipped into every runner.** buildx reads
`$BUILDX_CONFIG/buildkitd.default.toml` whenever the workflow passes no config of its own,
so the orchestrator generates one per reconcile pass (`CiBuildkitConfig`):

- `[registry."…"] http/insecure = true` stanzas for exactly the registries the host
  daemon itself treats as insecure (`GET /info` → `RegistryConfig.IndexConfigs`) — the
  daemon is the authority on which registries this box reaches without TLS. This deletes
  `buildkitd-config-inline` from consuming workflows.
- `[worker.oci] snapshotter = …`, only when the instance-wide `Ci:BuildkitSnapshotter`
  option explicitly names one. There is deliberately **no default and no detection**
  (a `/proc/filesystems`-based auto-detection shipped briefly and was reverted): BuildKit's
  own `auto` already probes overlayfs and then fuse-overlayfs *with a real test mount*
  ([`main_oci_worker.go`](https://github.com/moby/buildkit/blob/master/cmd/buildkitd/main_oci_worker.go);
  fuse-overlayfs's `Supported()` mounts read-only multiple lowerdirs) before falling back
  to `native` — so the `native` outcome on a host means the fuse-overlayfs test mount
  *genuinely failed there*, and nothing Watchtower can see from the outside beats that
  evidence. Crucially, an explicitly named snapshotter makes buildkitd **skip** the
  functional check entirely: it starts cleanly without ever proving a mount works, and a
  wrong name turns quietly-slow builds into builds that fail at the first layer mount.
  Hence the knob is expert-only — for an operator who has verified a snapshotter with a
  real mount (below) on a host whose probe is demonstrably wrong, or who wants one the
  probe never tries (e.g. `stargz`) — and it only helps when the builder image actually
  contains that snapshotter's binary (the stock buildx builder image ships none beyond
  overlayfs/native — see the verification note below). `none`/`auto` both mean "emit
  nothing"; instance-wide because which snapshotter works is a property of the host
  kernel, not of any repo; an override is logged once on change.

Delivery is a third per-repo volume, `watchtower-ci-buildx-{repo}`, mounted at
`/home/runner/_buildx` and exported as `BUILDX_CONFIG` (runner env is inherited by job
steps). A volume rather than a file bind because of the standing trap: dockerd creates
missing bind parents as root, and a mount under `~/.docker` would leave that directory
root-owned and break the next `docker login` in a job. The existing volume-init container
writes the file (content passed as env, written with `printf '%s'`) and chowns all three
volume roots; it re-runs whenever the generated content changes — the last-written stamp
lives on the repo's in-memory status — so a registry added at runtime reaches jobs within
one pass. A workflow that passes its own `buildkitd-config(-inline)` still wins outright;
the default only fills the unconfigured case.

**The fast path on hosts without a working OCI snapshotter.** Where neither overlayfs nor
fuse-overlayfs can work, the `docker-container` driver is simply the wrong tool: the
daemon's own builder (`driver: docker`, i.e. *no* setup-buildx step) uses the host's
storage driver — real CoW even on btrfs — keeps its build cache on the host between runs,
and inherits `insecure-registries` natively. That choice is encoded once in the reusable
workflow [`build-push-image.yml`](../../.github/workflows/build-push-image.yml)
(`uses: swimmesberger/Watchtower/.github/workflows/build-push-image.yml@main`), together
with its consequences: `provenance: false` (the docker driver can't do attestations) and
no registry cache import/export (the daemon's cache persists anyway). Consuming repos
carry one `uses:` line, `secrets: inherit`, and no host knowledge — the
`REGISTRY`/`REGISTRY_USERNAME`/`REGISTRY_PASSWORD` values it reads are the ones the
registry sync (Secrets §1) already pushes.

**Operational notes.**

- With the docker driver, the daemon's build cache is no longer discarded with the builder
  container. `docker builder prune` is the relief valve when it grows; wiring it into
  Watchtower's maintenance/pruning story is future work.
- A `FROM` on the plain-HTTP registry fails under the docker driver with "server gave
  HTTP response to HTTPS client" even when `insecure-registries` is configured: the
  daemon-embedded BuildKit's `FROM`-metadata resolver ignores that setting (observed on
  Synology's moby 24.0.2; the classic pull/push paths honor it fine). BuildKit does
  resolve a locally-present image without touching the network, so the reusable
  workflow's `pre-pull` input — a `docker pull` through the daemon's insecure-aware,
  authenticated path before the build — is the fix for private base images.
- Runners on small hosts will not reach GitHub-hosted speeds even once the snapshotter is
  right — `exporting layers` is mostly gzip on however few cores the box has. That is not
  a bug to go hunting for.
- Verifying fuse-overlayfs viability on a host by hand, before ever setting
  `Ci:BuildkitSnapshotter`. Two traps, both hit while investigating issue #65 on the NAS
  (2026-08-28): a forced `--oci-worker-snapshotter=fuse-overlayfs` start is **not** a
  test — an explicit name skips `Supported()`, so buildkitd registers the worker without
  ever mounting anything, even when the binary is absent — and the standard
  `moby/buildkit` image **does not contain the fuse-overlayfs binary at all** (upstream
  installs it only in the `-rootless` variant). The latter is why `Supported()` fails at
  its `LookPath` step and `auto` can never select fuse-overlayfs under the stock
  `docker-container` builder — on any host. Testing what the *kernel* can do therefore
  needs an image that has the binary:

  ```bash
  docker run --rm --privileged alpine:3.22 sh -c '
    apk add -q fuse-overlayfs
    mkdir -p /tmp/l1 /tmp/l2 /tmp/u /tmp/w /tmp/m
    fuse-overlayfs -o lowerdir=/tmp/l2:/tmp/l1 /tmp/m && echo RO-MULTI-LOWER-OK && umount /tmp/m
    fuse-overlayfs -o lowerdir=/tmp/l1,upperdir=/tmp/u,workdir=/tmp/w /tmp/m && echo RW-OK'
  ```

  Even a capable kernel is not enough for the container driver: the builder image itself
  must carry the binary (`setup-buildx-action` with `driver-opts: image=…` pointing at a
  custom build) — per-repo host knowledge again, and fuse-overlayfs is slower than
  kernel overlayfs anyway (metadata copy-up instead of `native`'s full-tree copies). In
  practice, on a host without kernel overlayfs the docker-driver reusable workflow above
  is the fast path.

### RPC surface

| Method | Notes |
| --- | --- |
| `ci.listRepos` | configured repos + orchestrator status (slots running, last error) |
| `ci.addRepo` / `ci.updateRepo` / `ci.removeRepo` | CRUD; add validates the PAT scopes up front |
| `ci.listAvailableRepos` | GitHub repo picker (`GET /user/repos` via a chosen credential) |
| `ci.listRuns` | proxied workflow runs for a repo (status, conclusion, commit, timing) |
| `ci.listJobs` | jobs + step status for a run (live-ish via polling) |
| `ci.getJobLogs` | full log text after job completion |
| `ci.getRunnerStatus` | per-repo runner slots, the live runner containers, backoff/error state |
| `ci.recycleRunner` / `ci.recycleRunners` | operator-requested recycle of one runner container / the whole pool: deregister at GitHub, remove, wake the loop to respawn under the current settings. Deregistration doubles as the idleness check (as in the automatic stale recycle), so a runner mid-job is kept and reported busy; `force` removes it anyway, failing that job |

PAT CRUD reuses the existing `credentials.*` module — note in the UI that CI needs a
**fine-grained PAT** with Administration RW + Actions R + Metadata/Contents R (unlike the
ghcr.io classic-PAT caveat documented on `Credential`).

### Frontend

New module `src/watchtower-web/src/modules/ci/` (contribution model):

- Repos page: enabled repos, runner slot status, add-repo dialog with repo picker.
- Runner containers table (on the product's CI tab): one row per live runner — container name
  and short id, Docker state, uptime, image, and the runner's id at GitHub, linked to the
  repository's Actions runner settings. A runner still on superseded settings is badged
  "settings changed" from the spec-hash comparison, so a saved change that has not reached the
  running runner yet is visible rather than mysterious. Watchtower stores no runner table (the
  containers are the state), so the rows come off the host's containers and their labels; the
  list is the orchestrator's last reconcile pass, which is why a just-spawned runner can trail
  the slot count by one interval. Each row carries a recycle action (plus "Recycle all" on the
  card) backed by `ci.recycleRunner`/`ci.recycleRunners`; a busy runner answers with a confirm
  dialog that escalates to `force`.
- Builds view per repo: run list → job list with step progress (poll while running) →
  log viewer (fetched on completion).
- Generated RPC client from `rpc-schema.json` as everywhere else.

## Security

- **The PAT never enters a runner container.** Only the single-use JIT config does.
- **Docker socket is opt-in per repo and clearly labeled** in the UI as host-root
  equivalent. Reasonable for your own private repos; it is also the hook that lets a
  build push an image and trigger a Watchtower stack deploy (future work).
- **Anyone who can push to an enabled repo can execute code on this box.** Acceptable
  for personal repos; the docs should recommend keeping GitHub's default "require
approval for outside collaborators" workflow setting on, and never enabling public
  repos with fork PRs.
- Runner containers get no Watchtower API access and no extra mounts beyond caches.

## Secrets

Secrets and config a workflow needs (registry logins, API tokens, plain settings) often
already live in Watchtower as `Credential`s / config. Two requirements shape the design:

- **Workflow YAML stays fully standard** — `${{ secrets.X }}` and `${{ vars.X }}` work
  unmodified, so the GHA ecosystem (actions with `with:` inputs, reusable workflows with
  `secrets:` inputs) and GHA-compatible providers (Gitea/Forgejo Actions, `act`) keep
  working. This forces delivery through GitHub: expression contexts are composed by
  GitHub's service into the job message, and a self-hosted runner can only *evaluate* them
  — there is no runtime mechanism to extend the `secrets`/`vars` contexts locally.
- **Watchtower is the source of truth.** Nothing is managed in GitHub settings; GitHub is a
  *write-only delivery cache* that Watchtower fills.

Mechanisms:

1. **Watchtower → GitHub sync (default).** Per CI repo, mappings
   `secret name → Credential` and `variable name → value`. Watchtower ships secrets via the
   sealed-box secrets API (`GET .../actions/secrets/public-key` + `PUT
   .../actions/secrets/{name}`; libsodium) and non-secret config via the variables API.
   *Implemented for the registry case:* `CiRepo.SyncRegistryUrl` selects one registry from
   the merged view (host docker config + Watchtower registries; `RegistryAuthBuilder.
   ListResolvedRegistriesAsync`), and the orchestrator pushes the `REGISTRY` variable plus the
   `REGISTRY_USERNAME`/`REGISTRY_PASSWORD` secrets each reconcile pass the value hash
   differs (rotation re-pushes automatically; failures backoff 5 min and surface in the CI
   tab, and every `ci.updateRepo` save clears the backoff so fixing the PAT + saving retries
   immediately). Selecting a registry probes the PAT's Secrets/Variables access up front —
   the sync stays optional, so a PAT without those permissions is fine until a registry is
   selected. Syncs and CI config changes are audited (category `ci`). Arbitrary
   name→credential mappings remain future work.
   Sync runs on mapping changes and **automatically on credential rotation** — rotate once
   in Watchtower, every repo using that credential is re-pushed. Secret values cannot be
   read back from GitHub, so Watchtower stores a hash of the last-synced value per mapping
   to detect drift/failed syncs and re-push. GitHub's log masking and fork-PR secret
   semantics work as normal. Requires the fine-grained PAT to also carry "Secrets" (write)
   and "Variables" (write); `ci.addRepo` validation probes these too.
   *Portability:* the YAML stays provider-neutral; moving to Forgejo later means swapping
   this sync adapter (Forgejo/Gitea expose an equivalent API), not touching workflows.
2. **Registry auth without any secret (automatic for docker-socket repos).** Runners of
   repos with `AllowDockerSocket` get a pre-authenticated `DOCKER_CONFIG` mounted, built by
   the existing `RegistryAuthBuilder` from the repo's mapped registries — `docker push` in a
   job needs no login step and no secret anywhere. Also the natural push→deploy bridge:
   the job pushes, Watchtower's stack update-check sees the new image.
3. **Local env injection (opt-in).** For secrets that must never be stored at GitHub even
   encrypted: injected into the runner container env, readable as `$NAME` in steps.
   Caveats (why it is not the default): not part of the `secrets` context, so no automatic
   log masking (mitigate via an `ACTIONS_RUNNER_HOOK_JOB_STARTED` hook emitting
   `::add-mask::` — verify in spike) and unusable in `with:` inputs without a
   `$GITHUB_ENV` bridge step.

### Evaluated alternative: patched runner ("context shim")

It IS possible to make `${{ secrets.X }}`/`${{ vars.X }}` resolve locally without GitHub
ever seeing the values: `actions/runner` is MIT-licensed and Watchtower ships the runner
image anyway, so a small patch in `Runner.Worker`'s job-message processing can merge a
Watchtower-mounted secrets file into the `secrets`/`vars` contexts and register the values
as log masks. Step-level usage (`env:`, `with:`, `run:`, container `credentials:`) is
evaluated in the worker and would be fully covered.

Hard limits (no local mechanism can cross them): expressions GitHub evaluates at plan time
— job-level `if:`/`environment:`, and **reusable-workflow `secrets:` wiring**
(`secrets: inherit`), which is expanded server-side — still require real GitHub secrets.

Cost: a maintained fork. The patch is small, but GitHub enforces a minimum runner version,
so it needs an automated rebase-and-rebuild per upstream release; an upstream refactor of
context composition breaks the patch until rebased. (A MITM variant — rewriting the
encrypted job message, feasible since the JIT config contains the runner's RSA key — was
considered and rejected as undocumented-protocol surgery.)

Decision: **sync (mechanism 1) stays the v1 default** — zero maintenance, covers
everything including reusable workflows. The context shim is a candidate experiment after
milestone 3; if it proves stable across several runner releases it may become the default,
demoting sync to the reusable-workflow edge case.

### Evaluated alternative: sovereign mode (act engine, no GitHub attachment)

No third-party runner can attach to GitHub's Actions service (undocumented protocol,
enforced minimum runner version — Gitea/Forgejo runners speak their own forge's protocol).
But **nektos/act** — the engine underneath Gitea/Forgejo Actions — executes GHA workflow
YAML directly, which enables a different architecture: Watchtower as the CI provider.
On push (webhook/poll), check out the commit and run `.github/workflows/*.yml` via act in
containers; post commit statuses back to GitHub via the API.

- Solves secrets/vars completely: act composes the expression contexts from local input —
  `${{ secrets.X }}` works with values that never leave the box. No sync, no runner fork.
- Compatibility ~90–95%: marketplace actions, containers, matrix, services, reusable
  workflows (limits), artifact server + cache exist. Gaps: OIDC `id-token`, scoped
  `github.token` (a PAT stands in), environments/approvals, concurrency groups; GitHub's
  Actions tab stays empty (Watchtower must be the complete build UI); scheduled triggers
  become Watchtower's responsibility.

**Decision (2026-08-02): attached mode — the official GitHub Actions runner — is the
architecture**, settled in favor of native PR/commit visibility (checks, branch
protection, Actions tab, scoped `github.token`, OIDC) over full local sovereignty.
Sovereign mode is shelved, not planned; this section stays as the record of the analysis
should the trade-off ever be revisited (e.g. a forge migration, where act is the natural
engine anyway).

## Product-linked CI, toolchain detection & cache pre-warming

Status: implemented (2026-08-18); moved from the stack to the product 2026-08-25 (ADR-0026
decision 7). Products are where repositories live in Watchtower — the natural place to turn
CI on. This section covers the product↔CI link, the toolchain profile detected from deploy
clones, and the toolcache warmer driven by it.

### "Enable CI" per product

A product's `RepositoryUrl` is parsed into `owner/name` (`GitHubRepoUrl`, github.com HTTPS
and SSH forms only — other forges can't get Actions runners and say so).
`ci.enableForProduct` creates — or re-enables — the `CiRepo` for that pair and records it as
`Product.CiRepoId`; since CI repos are unique on `owner/name`, **every stack deploying the
product (and every other product over the same repository) shares one runner pool and one
cache**. `ci.getProductCi` is the read side the product page's CI tab polls: parse result,
linked repo, runner status, toolchain profile.

The FK replaced URL string matching, and is filled in lazily: a product whose `CiRepoId` is
null — everything the ADR-0026 product backfill created — is resolved from its repository URL
on the first CI read (`CiRepoResolver`), which then records the answer. `products.update` clears
the link when the repository URL moves, so the next read re-resolves it; a read that raced that
clear can re-record the old repo, so every lookup also re-checks that the linked repo's
`owner/name` still matches the parsed URL and ignores the link when it does not. `ci.getStackCi` and
`ci.enableForStack` survive as thin forwards through `stack.ProductId` for one release.

Credentials: the product's clone credential usually holds a Contents-read PAT, while runner
registration needs repository **Administration (read and write)**. The chosen credential —
explicit, or defaulting to the product's — is probed via `ValidateRepoAccessAsync` before
anything is written, and a wrong-scoped PAT fails with a message naming the missing
permission and the way out (choose/create a runner-admin credential). Re-enabling with the
already-validated credential skips the probe.

### Toolchain detection (heuristics)

Every deploy already clones the repository, so detection piggybacks on that clone at zero
extra cost: right after a successful clone, `CiToolchainRecorder` (best-effort by contract —
it can never fail a deploy) detects a **toolchain profile** for the CI repo the deployed
stack's product links to, if one is configured. Signals, strongest first:

1. `.github/workflows/*.yml` `setup-dotnet`/`setup-node`/`setup-go` steps and their
   `*-version:` inputs (inline, block-scalar and flow-list forms; matrix expressions are
   ignored). What a workflow names is what jobs will install, so when a workflow names
   versions for a kind, manifest signals for that kind are dropped.
2. Manifests: `global.json` (SDK channel), `*.csproj` TFMs (bounded tree walk that skips
   `node_modules`/`bin`/`obj`/…), `.nvmrc`/`package.json` `engines.node`, `go.mod`'s `go`
   directive. Dockerfile presence is recorded as a flag (a docker-based build needs no
   toolcache).

The profile is persisted as JSON on the `CiRepo` (`toolchain_profile_json` +
`toolchain_detected_at`) and shown in the UI ("detected: .NET 10.0, Node 22"). The parser
is line-based and deliberately heuristic; a malformed file contributes nothing, and an empty
profile is a valid result. Detection failure never blocks deploys or runners.

### Cache pre-warming

The whole point of the toolcache volume is that `setup-*` actions find a local hit and skip
their downloads — but something has to put the SDKs there first. The orchestrator's
reconcile loop converges on that: whenever the profile's hash (over kind+version pairs
only — source attribution and Dockerfile presence don't change what gets installed) differs
from the last successfully warmed hash, it spawns a **one-shot warmer container** (label
`watchtower.managed=ci-warmer`, same tracking scheme as runners, restart-safe) running a
generated bash script (`CiWarmerScript`) that installs into the shared toolcache volume:

- Node/Go: tarballs extracted to `{tool}/node/{version}/{arch}` + `.complete` marker —
  exactly the layout `tc.find` probes; partial versions ("22", "1.24") resolve to the
  latest release of the line at warm time.
- .NET: `dotnet-install.sh --channel {X.Y}` into `{tool}/dotnet`, which runners expose as
  `DOTNET_INSTALL_DIR`; dotnet-install then reports the SDK as already installed.

Outcomes are persisted on the repo (`warmed_profile_hash`/`last_warmed_at` on success,
`last_warm_error` with the log tail on failure) and surfaced in the UI as
warmed/warming/failed/pending. Failures retry after a fixed 15-minute in-memory backoff and
are **never fatal** — a cold cache just means jobs download their own tools. Re-warming
happens on profile change only; an unchanged profile costs the loop nothing but a hash
comparison.

Security: warmer containers get **no PAT, no JIT config, no Docker socket** — only the
cache volume. The script downloads exclusively from the public SDK hosts (nodejs.org,
dot.net, go.dev), and toolchain versions are re-validated against a strict numeric pattern
at the script boundary so a hostile repository manifest cannot smuggle shell syntax into
the warmer.

### Not implemented (future work)

- **Generated per-profile runner images** (bake the toolchains into an image built over the
  Docker socket instead of a shared volume): stronger isolation between repos and faster
  container start, at the cost of an image build pipeline + registry storage per profile.
  The volume approach was chosen first because it needs no image builds and converges
  per-repo; images remain the intended end state for the "house toolchain" case.
- Warm-aware cache GC (drop toolcache entries the profile no longer names).

## Multi-instance behavior

Watchtower stays single-host; each instance (NAS, each Hetzner box) manages runners
against its local Docker socket for its own repo scope. Instances never coordinate —
GitHub's job queue is the only shared component. Scopes are currently disjoint, but this
design does not depend on that: if the same repo is later enabled on two instances, both
register instance-named runners and GitHub distributes jobs across them (pin with
`runs-on: [self-hosted, {instance}]` when needed). The build view is scope-global either
way, because it reads the GitHub API, not local state.

## Migration from the runner VM

Per repo, no big-bang: enable the repo in Watchtower → its ephemeral runner registers
with the same `self-hosted` label → verify a few builds → unregister that repo's VM
runner. The VM stays as fallback until the last repo moves, then it is deleted.
Toolchains previously installed in the VM move into a custom runner image (or `setup-*`
actions + warm toolcache volume).

## Milestones

1. **Spike (this branch)**: `Ci` module skeleton, `CiRepo` + migration, `GitHubApiClient`
   (JIT + validation), `RunnerOrchestrator` reconcile loop, `ci.addRepo/listRepos/
   updateRepo/removeRepo/getRunnerStatus`. Proven end-to-end against one real repo.
2. **Builds UI**: `ci.listRuns/listJobs/getJobLogs` + the frontend module.
3. **Comfort**: cache volumes wired by default ✓, stack-linked enablement + toolchain
   detection + toolcache pre-warming ✓ (see
   [Stack-linked CI](#stack-linked-ci-toolchain-detection--cache-pre-warming)), custom house
   runner image, run-completed → stack deploy hook, live log tail (mount runner `_diag`
   volume, SSE tail endpoint — possible precisely because Watchtower hosts the runner),
   cache volume GC.

## Open questions

- Instance name source: new `Watchtower:InstanceName` option (default: machine hostname).
- Whether `ci.listRuns` should cache briefly (rate limits: 5k req/h fine-grained PAT —
  polling one repo's runs every 5 s while the view is open is well within budget, so v1
  skips caching).
