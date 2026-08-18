# ADR-0016: Stack backups — volume archives to pluggable remote storage

## Status

Accepted

## Context

A Watchtower host carries state that exists nowhere else: the named volumes of every deployed stack
(databases, uploads, generated data). The stacks themselves are reproducible — a stack is a git
repository plus images in a registry — but the volumes are not, and until now Watchtower offered no
way to get them off the host. Operators need:

- **automatic** backups at a sensible (configurable) time of day;
- storage on an **external** target, reachable over a standard protocol so the storage vendor is
  swappable (an SFTP host such as a Hetzner Storage Box being the canonical first target);
- **retention** — old backups must age out without manual housekeeping;
- optional **encryption**, so the storage operator never sees plaintext data;
- **traceability** — it must always be obvious which backup belongs to which instance and stack;
- a **documented restore path**; and
- an on-demand way to pull a single volume out of the UI as an archive.

Constraints that shaped the decision: Watchtower runs as a single container whose only privileged
capability is the Docker socket (volumes are *not* mounted into it); the shipped image should not
grow new binaries (no rclone/restic/gpg); and everything must follow the established
runtime-settings pattern (ADR-0014: stored settings live-reload, env vars pin).

## Decision

### 1. The backup unit is a stack's compose volumes, archived through the Docker Engine API

A backup archives every named volume whose `com.docker.compose.project` label matches the stack's
compose project. Watchtower creates a **helper container that is never started**: an existing small
image (`busybox:stable` by default, configurable) with each volume bind-mounted read-only under
`/backup/{volume}`. The Docker Engine's archive endpoint (`GET /containers/{id}/archive?path=/backup`)
then streams a tar of all volumes straight out of the daemon — file-level, no code executes in the
helper, and the helper is removed afterwards. A `backup-manifest.json` (format version, instance
name, stack id/name/project, volume list, UTC timestamp, encryption flag) is injected into the same
tar beforehand via the corresponding `PUT …/archive` endpoint, so every archive self-describes.

The same mechanism powers the UI's per-volume download: an authenticated endpoint streams any single
named volume as `.tar.gz` without staging it on disk.

### 2. Consistency: stop the stack's containers during the snapshot (per-stack opt-out)

A file-level copy of a live database volume is not atomic — tar reads files over a time window, which
is worse than a crash snapshot. Each stack therefore has a `BackupStopContainers` option
(**default on**): the stack's running containers are stopped for the duration of the archive step and
restarted afterwards (only the ones that were running). Operators who accept fuzzy snapshots (or
whose stacks are read-mostly) can turn it off per stack. Application-aware dumps (`pg_dump` et al.)
are explicitly out of scope for V1 — the extension point would be compose labels, and it can arrive
in a later ADR without changing the storage or encryption format.

### 3. Storage backends: a small provider abstraction; SFTP and local directory built in

Uploads go through `IBackupStorage` (upload / list / delete on relative paths), with two built-in
providers mirroring the metrics/proxy provider pattern (ADR-0007/0015):

- **`sftp`** — SSH.NET-based; password and/or private-key auth. SFTP is ubiquitous (every NAS,
  every storage box, any Linux host with sshd), which is what makes the storage vendor swappable.
- **`local`** — a directory inside the container (i.e. an operator-mounted second disk/NFS share),
  which is also the provider integration tests exercise.

The remote layout encodes traceability: `{basePath}/{instanceName}/{stackName}/
{project}_{yyyyMMdd'T'HHmmss'Z'}.tar.gz[.enc]`. The instance name is a backup setting (default:
machine name) so two Watchtower hosts can share one storage target without ambiguity.

### 4. Encryption: optional passphrase, OpenSSL `enc`-compatible AES-256-CBC

When a passphrase is configured, the gzipped tar is piped through AES-256-CBC with a key derived by
PBKDF2-SHA256 (600 000 iterations) from a random per-file salt, in **exactly the container format
`openssl enc` writes** (`Salted__` + salt header). Choosing an existing, boring format over a custom
one (or age/gpg) means restore needs nothing but stock OpenSSL:

```
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -in backup.tar.gz.enc | tar tz
```

A backup must be restorable on a machine where Watchtower does not run — that property outweighs the
lack of authenticated encryption (integrity of the archive is not the threat model here;
confidentiality against the storage operator is).

### 5. Retention: age and count limits, applied per stack folder after each success

`RetentionDays` (default 30; 0 = keep forever) and `RetentionMaxCount` (0 = unlimited) are applied
to the stack's remote folder after every successful backup. Only files matching Watchtower's own
naming pattern are considered — foreign files in the same directory are never touched — and the
newest backup is never deleted, so a misconfigured retention can not delete the backup it just wrote.

### 6. Scheduling and history follow the established patterns

One global daily window (`Backup:Time`, default `03:30`, server-local — same semantics as
`AutoDeployTime`) plus a per-stack `BackupEnabled` opt-in. All global knobs are runtime-editable
settings under `Watchtower:Backup:*` (ADR-0014: env vars pin; secrets are write-only toward the UI).
Runs are recorded as `BackupEvent` rows (status, size, remote path, captured error output) — the
same shape deploys use — listed in the UI, and a manual "back up now" enqueues through a
single-flight queue (one backup at a time process-wide; per-stack coalescing), so a slow upload can
never stampede the host.

## Consequences

- The application gains its first SFTP dependency (SSH.NET) — a single, MIT-licensed, pure-managed
  library; the shipped image grows no new binaries.
- With `BackupStopContainers` on, a stack is down for the duration of its archive step — the price
  of consistent snapshots, bounded by scheduling backups into a quiet window. With it off, snapshots
  of write-active volumes are only crash-consistent at best.
- The helper-container mechanism requires the helper image to be present or pullable; it is pulled
  on first use and is configurable for air-gapped hosts.
- CBC without a MAC means an attacker with write access to the storage could tamper with ciphertext
  undetected; accepted for V1 (see §4) — a format change would be a new ADR.
- Restore is deliberately manual in V1 — documented step-by-step in [docs/backups.md](../backups.md)
  (download, decrypt with stock OpenSSL, untar into recreated volumes) — because a wrong-direction
  restore button is more dangerous than a wrong-direction backup.
- Database-aware dump hooks, restore-from-UI, and additional providers (S3, WebDAV) are explicit
  non-goals of this ADR and would extend, not replace, the abstraction.
