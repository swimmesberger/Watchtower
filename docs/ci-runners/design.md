# CI Runners — self-hosted GitHub Actions runners managed by Watchtower

Status: draft (2026-08-02)

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
  GitHub API on demand (UI polling), not mirrored into SQLite. The only persistent state
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
- Mounts: cache volumes (below); `/var/run/docker.sock` only when `AllowDockerSocket`.

### Caching (avoiding the ephemeral-runner slowdown)

The manually-managed VM had warm caches; a fresh container has none. Per-repo named
volumes mounted into every runner of that repo:

- `watchtower-ci-tool-{repo}` → `/home/runner/_work/_tool` (setup-* action toolcache)
- `watchtower-ci-pkg-{repo}` → NuGet/npm cache dirs (via runner env)

The workspace itself stays ephemeral (clean checkout per job). Volumes are removable via
the existing Volumes module; GC/pruning is future work.

### RPC surface

| Method | Notes |
| --- | --- |
| `ci.listRepos` | configured repos + orchestrator status (slots running, last error) |
| `ci.addRepo` / `ci.updateRepo` / `ci.removeRepo` | CRUD; add validates the PAT scopes up front |
| `ci.listAvailableRepos` | GitHub repo picker (`GET /user/repos` via a chosen credential) |
| `ci.listRuns` | proxied workflow runs for a repo (status, conclusion, commit, timing) |
| `ci.listJobs` | jobs + step status for a run (live-ish via polling) |
| `ci.getJobLogs` | full log text after job completion |
| `ci.getRunnerStatus` | per-repo runner slots, container ids, backoff/error state |

PAT CRUD reuses the existing `credentials.*` module — note in the UI that CI needs a
**fine-grained PAT** with Administration RW + Actions R + Metadata/Contents R (unlike the
ghcr.io classic-PAT caveat documented on `Credential`).

### Frontend

New module `src/watchtower-web/src/modules/ci/` (contribution model):

- Repos page: enabled repos, runner slot status, add-repo dialog with repo picker.
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

Secrets a workflow needs (registry logins, API tokens) often already live in Watchtower as
`Credential`s. Three delivery mechanisms, by secret type:

1. **Watchtower → GitHub Actions secrets sync (default).** Per CI repo, a mapping
   `secret name → Credential`. Watchtower encrypts the value with the repo's public key
   (libsodium sealed box, `GET .../actions/secrets/public-key`) and upserts it via
   `PUT /repos/{o}/{r}/actions/secrets/{name}` — on mapping change and **automatically on
   credential rotation**, so rotating once in Watchtower propagates to every repo that uses
   it. Workflows keep standard `${{ secrets.X }}` syntax and GitHub's log masking. Requires
   the fine-grained PAT to also carry the "Secrets" (write) repository permission.
2. **Registry auth without any secret (automatic for docker-socket repos).** Runners of
   repos with `AllowDockerSocket` get a pre-authenticated `DOCKER_CONFIG` mounted, built by
   the existing `RegistryAuthBuilder` from the repo's mapped registries — `docker push` in a
   job needs no login step and no secret. Also the natural push→deploy bridge: the job
   pushes, Watchtower's stack update-check sees the new image.
3. **Local env injection (opt-in, discouraged default).** Env vars injected into the runner
   container never touch GitHub, but the runner cannot mask values it doesn't know are
   secrets — an accidental `echo` lands in build logs in plaintext. Reserved for secrets
   that must not be stored at GitHub, behind an explicit warning in the UI.

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
3. **Comfort**: cache volumes wired by default, custom house runner image, run-completed
   → stack deploy hook, live log tail (mount runner `_diag` volume, SSE tail endpoint —
   possible precisely because Watchtower hosts the runner), cache volume GC.

## Open questions

- Instance name source: new `Watchtower:InstanceName` option (default: machine hostname).
- Whether `ci.listRuns` should cache briefly (rate limits: 5k req/h fine-grained PAT —
  polling one repo's runs every 5 s while the view is open is well within budget, so v1
  skips caching).
