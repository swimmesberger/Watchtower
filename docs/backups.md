# Stack backups

Watchtower can back up each stack's **named volumes** — the only state on the host that is not
reproducible from git and a registry — to external storage, on a daily schedule, with retention and
optional encryption. Design and rationale: [ADR-0016](decisions/0016-stack-backups.md).

What a backup is: one `*.tar.gz` (or `*.tar.gz.enc` when encrypted) per stack per run, containing
every volume labelled with the stack's compose project plus a `backup-manifest.json` that records
the instance, stack, volumes and timestamp — so any archive identifies itself even after being
copied around.

What it is **not** (v1): application-aware dumps (`pg_dump` et al.) or incremental backups. A
file-level snapshot of the stopped stack is the v1 consistency model.

## Setting it up

Everything global lives in **Settings → Backups** (or the `WATCHTOWER__BACKUP__*` environment
variables — env vars pin their setting read-only in the UI, see
[ADR-0014](decisions/0014-env-wins-runtime-settings.md)):

| Setting | Env var | Meaning |
| --- | --- | --- |
| Schedule | `WATCHTOWER__BACKUP__ENABLED` | Master switch for the daily run (default off). |
| Time | `WATCHTOWER__BACKUP__TIME` | Server-local `HH:mm` the window opens (default `03:30`). |
| Instance name | `WATCHTOWER__BACKUP__INSTANCENAME` | Names this Watchtower in the storage layout and manifests. Set it explicitly in containers — the default (machine name) is the container id there. |
| Retention (days) | `WATCHTOWER__BACKUP__RETENTIONDAYS` | Delete backups older than N days after each successful run; `0` keeps forever (default 30). |
| Retention (count) | `WATCHTOWER__BACKUP__RETENTIONMAXCOUNT` | Keep at most N backups per stack; `0` unlimited. |
| Encryption passphrase | `WATCHTOWER__BACKUP__ENCRYPTIONPASSPHRASE` | When set, archives are encrypted (see below). |
| Helper image | `WATCHTOWER__BACKUP__HELPERIMAGE` | Image for the never-started helper container (default `busybox:stable`); any pullable image works. |
| Provider | `WATCHTOWER__BACKUP__PROVIDER` | `sftp` (default) or `local`. |

Then opt each stack in on its **Backups tab**: include it in the schedule, and choose whether its
containers are **stopped during the snapshot** (default on — the recommended setting for anything
with a database; the stop window covers only the local snapshot, not the upload). "Back up now"
works regardless of the schedule switch.

Backups run one at a time through a single-flight queue, and every run is recorded in the tab's
history (status, size, remote path, full log).

### SFTP (e.g. a Hetzner Storage Box)

Any SSH-reachable storage works — a [Hetzner Storage Box](https://www.hetzner.com/storage/storage-box/),
a NAS, another server:

- **Host / port / username** — for a Storage Box: `u123456.your-storagebox.de`, port **23**,
  user `u123456` (or a sub-account limited to its own directory, recommended).
- **Auth** — password and/or an SSH private key (paste the PEM block; register the matching public
  key with the storage — for Hetzner Storage Boxes SSH keys must be **RSA or ECDSA** (ed25519 is
  supported on newer boxes); generate e.g. `ssh-keygen -t ecdsa -b 521 -f storagebox_key`).
- **Base directory** — remote directory the layout is rooted in (default `watchtower-backups`),
  created automatically.

Save, then hit **Test storage** — it writes and deletes a probe file, so connectivity, auth and
write permission fail there with the server's own words rather than in a 03:30 run nobody watches.

The remote layout is `{base}/{instance}/{stack}/{project}_{yyyyMMddTHHmmssZ}.tar.gz[.enc]` — which
instance and stack a file belongs to is readable straight off its path, and the UTC timestamp in the
name is what retention works from.

### Local directory

The `local` provider writes the same layout under a directory *inside the Watchtower container* —
mount a second disk or network share there (e.g. `-v /mnt/backup-disk:/backups`). Backups on the
same disk as `/data` protect against very little.

## Encryption

With a passphrase set, every archive is piped through AES-256-CBC in the **OpenSSL `enc` container
format** (PBKDF2-SHA256, 600 000 iterations, random per-file salt). Nothing but stock OpenSSL is
needed to decrypt — deliberately, so a restore never depends on a running Watchtower:

```bash
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 \
  -in web-app_20260817T033000Z.tar.gz.enc -out web-app_20260817T033000Z.tar.gz
```

Keep the passphrase somewhere safe *outside* the host (password manager). Without it, encrypted
backups are unrecoverable; changing it affects future backups only. Note the format encrypts but
does not authenticate (no MAC) — treat the storage as untrusted for confidentiality, not integrity.

## Restoring a backup

### From the UI (same host, Watchtower running)

On the stack's **Backups tab → Restore…**: pick an archive (the list shows what is actually on the
storage right now, not the local history), then confirm by typing the stack's name. Watchtower then

1. downloads the archive (and decrypts it with the configured passphrase),
2. compares its contents against the stack's volumes — only volumes present in **both** are
   touched; mismatches are logged, never guessed at (if the stack has no volumes yet, deploy it
   once first so compose creates them),
3. stops the stack's containers, **erases the target volumes' current contents**, extracts the
   archive back into them (ownership and permissions preserved), and restarts the stack.

Data written since the backup was taken is gone afterwards — that is the point, and why the confirm
is typed. The run lands in the same history as backups (trigger `restore`), with the full log.
Restores are refused while a deploy or another backup/restore of the stack is in flight. The volume
wipe is the one step that executes code in the helper container, so the helper image must provide
`sh`/`rm` — the default busybox does.

### Manually (disaster recovery — any Docker host, no Watchtower needed)

1. **Fetch** the archive (any SFTP client; `scp -P 23 u123456@u123456.your-storagebox.de:watchtower-backups/nas/web-app/web-app_20260817T033000Z.tar.gz.enc .`).
2. **Decrypt** if needed (command above).
3. **Inspect** — the tar contains `backup/backup-manifest.json` plus one directory per volume:

   ```bash
   tar -tzf web-app_20260817T033000Z.tar.gz | head
   # backup/backup-manifest.json
   # backup/web-app_pgdata/...
   ```

4. **Stop the stack** (`docker compose -p web-app down` — or the stack's Stop in Watchtower), so
   nothing writes into the volumes while you restore. If the volumes are damaged or you are on a
   fresh host, recreate them empty first (deploy the stack once, or `volumes.recreate`).
5. **Unpack each volume** into its (empty) named volume — the volume names are in the manifest:

   ```bash
   docker run --rm -i -v web-app_pgdata:/restore busybox \
     sh -c 'rm -rf /restore/* /restore/..?* /restore/.[!.]*; tar -xzf - -C /restore --strip-components=2 backup/web-app_pgdata' \
     < web-app_20260817T033000Z.tar.gz
   ```

   (`--strip-components=2` drops the `backup/{volume}/` prefix; the `rm` clears a non-empty volume.)
   Repeat per volume.
6. **Start the stack again** (deploy from Watchtower). Verify the application actually sees its
   data before deleting anything remote.

A single volume downloaded from the UI (Volumes → ⋯ → *Download archive*) has the same shape
(`backup/{volume}/…`, no manifest) and restores with the same command.

Moving to a **new host**: register the stack in the new Watchtower, deploy it once (creates the
volumes), set the backup storage + instance name to the old values, then use the UI restore — the
picker lists the old instance's archives as long as the instance name matches its directory.

## How a run works (and its costs)

1. Volumes are resolved by the `com.docker.compose.project` label — an **undeployed stack has no
   volumes and the run fails** with a message saying so.
2. If "stop containers" is on, the stack's running containers are stopped.
3. A helper container is *created but never started* with each volume mounted read-only; the Docker
   daemon's archive endpoint streams one tar out of it (no code executes in the helper). The tar is
   gzipped (and encrypted) into a spool file in the container's temp directory — so the host needs
   free space for one compressed archive, and the stop window ends here.
4. Containers restart, then the spool uploads to the storage provider (to a `.partial` name,
   renamed on completion — a torn upload never looks like a finished backup).
5. Retention prunes the stack's remote folder: only files matching Watchtower's own naming pattern
   are considered, and the newest backup is never deleted.

Failures (including "process restarted mid-run") land in the history as `failed` with the log
attached; the next scheduled window simply tries again.
