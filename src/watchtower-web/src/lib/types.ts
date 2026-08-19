// TypeScript types matching the C# backend response records.

export interface Registry {
  id: number
  name: string
  url: string
  credentialId: number | null
  credentialName: string | null
  createdAt: string
}

export interface Credential {
  id: number
  name: string
  username: string
  createdAt: string
}

export interface Stack {
  id: number
  name: string
  repositoryUrl: string
  composeFilePath: string
  branch: string
  composeProjectName: string
  credentialId: number | null
  webhookToken: string | null
  webhookEnabled: boolean
  /** Pull-based deployment: redeploy automatically when polling detects something new. */
  autoDeployMode: AutoDeployMode
  /** Local time of day ("HH:mm") for scheduled auto-deploy. Null unless mode is 'scheduled'. */
  autoDeployTime: string | null
  lastDeployStatus: 'success' | 'failed' | 'running' | 'queued' | null
  lastDeployedAt: string | null
  /** Commit SHA checked out by the last successful deploy. Null until a deploy succeeds. */
  lastDeployedCommit: string | null
  createdAt: string
  /** True when at least one container image has a newer version available. Null when never checked. */
  hasUpdates: boolean | null
  /** Image names that have a newer version available. Null when never checked. */
  outdatedImages: string[] | null
  /** Remote branch head SHA when a commit newer than the last deploy exists. Null otherwise. */
  newCommitSha: string | null
  /** ISO timestamp of the last update check. Null when never checked. */
  updatesCheckedAt: string | null
}

export type AutoDeployMode = 'off' | 'onChange' | 'scheduled'

export interface DeployEvent {
  id: number
  stackId: number
  triggeredBy: string
  status: 'queued' | 'running' | 'success' | 'failed'
  output: string | null
  startedAt: string
  finishedAt: string | null
}

export interface Container {
  id: string
  names: string[]
  image: string
  state: string
  status: string
  /** "healthy" | "unhealthy" | "starting" | null */
  health: string | null
  stackName: string | null
}

export interface DeployAccepted {
  deployEventId: number
  status: string
}

export interface ActiveDeployment {
  id: number
  stackId: number
  stackName: string
  /** "queued" | "running" */
  status: string
  triggeredBy: string
  startedAt: string
}

export type SelfUpdateApplyStage = 'idle' | 'pulling' | 'restarting' | 'error'

export interface SelfUpdateStatus {
  credentialId: number | null
  detectedImageName: string | null
  isRunningInContainer: boolean
  currentImageId: string | null
  latestImageId: string | null
  isOutdated: boolean
  lastCheckedAt: string | null
  canApplyUpdate: boolean
  applyStage: SelfUpdateApplyStage
  applyError: string | null
  startedAt: string | null
}

// ── Request types ────────────────────────────────────────────────────────────

export interface CreateRegistryRequest {
  name: string
  url: string
  credentialId?: number | null
}

export interface UpdateRegistryRequest {
  name: string
  url: string
  credentialId?: number | null
}

export interface CreateCredentialRequest {
  name: string
  username: string
  token: string
}

export interface UpdateCredentialRequest {
  name: string
  username: string
  /** Omit or pass null to keep the existing token. */
  token?: string | null
}

export interface CreateStackRequest {
  name: string
  repositoryUrl: string
  composeFilePath: string
  branch: string
  composeProjectName?: string | null
  credentialId?: number | null
  webhookToken?: string | null
  webhookEnabled?: boolean
  autoDeployMode?: AutoDeployMode
  /** Required ("HH:mm") when autoDeployMode is 'scheduled'. */
  autoDeployTime?: string | null
  envVars?: StackEnvVarInput[]
}

export interface UpdateStackRequest {
  name: string
  repositoryUrl: string
  composeFilePath: string
  branch: string
  composeProjectName?: string | null
  credentialId?: number | null
  webhookToken?: string | null
  webhookEnabled?: boolean
  autoDeployMode?: AutoDeployMode
  /** Required ("HH:mm") when autoDeployMode is 'scheduled'. */
  autoDeployTime?: string | null
  /** When provided, atomically replaces all env vars. Pass [] to clear. Omit to leave unchanged. */
  envVars?: StackEnvVarInput[]
}

export interface UpdateSelfConfigRequest {
  credentialId?: number | null
}

export interface StackEnvVar {
  id: number
  key: string
  value: string
}

export interface StackEnvVarInput {
  key: string
  value: string
}

/** One env var a container is actually running with (from Docker inspect). */
export interface ContainerEnvVar {
  key: string
  value: string
}

export interface DockerConfigStatus {
  /** True when config.json exists at the resolved path inside the container. */
  exists: boolean
  /** Absolute path inside the container that was checked. */
  path: string
  /** "WATCHTOWER_DOCKER_CONFIG" | "DOCKER_CONFIG" | "default" */
  source: string
}

// ── Beacon: Volumes / Networks / Metrics domain types ────────────────────────
// Hand-maintained mirrors of the generated RPC shapes (camelCase). The three-state
// `lifecycle` + `refCount` fields are per amendment F4.

/** A volume's lifecycle relative to the running fleet (F4). */
export type ResourceLifecycle = 'live' | 'declared' | 'orphaned'

export interface VolumeInfo {
  name: string
  driver: string
  /** com.docker.compose.project label, else null. */
  project: string | null
  /** com.docker.compose.volume label (the short name in the compose file), else null. */
  composeVolume: string | null
  mountpoint: string
  createdAt: string | null
  labels: Record<string, string>
  scope: string
  /** Container names currently referencing it (running OR stopped). */
  inUseBy: string[]
  /** Containers referencing it, running OR stopped. Delete is offered only when 0 (F4). */
  refCount: number
  /** live = referenced by ≥1 container · declared = has a project label, no containers · orphaned = neither. */
  lifecycle: ResourceLifecycle
}

/** A volume's on-disk size, fetched on demand via `volumes.sizes` (df is expensive). */
export interface VolumeSize {
  name: string
  sizeBytes: number
  refCount: number
}

export interface NetworkEndpoint {
  containerId: string
  containerName: string
  /** Resolved from the container's compose project label. */
  stackName: string | null
  ipv4: string | null
  ipv6: string | null
}

export interface NetworkInfo {
  id: string
  name: string
  /** bridge | host | overlay | none | macvlan */
  driver: string
  scope: string
  /** Internal flag — no outbound route. */
  internal: boolean
  project: string | null
  composeNetwork: string | null
  createdAt: string | null
  labels: Record<string, string>
  ipam: { subnet: string | null; gateway: string | null }
  attached: NetworkEndpoint[]
  refCount: number
  /** live · declared · orphaned (F4). Defaults never report orphaned. */
  lifecycle: ResourceLifecycle
  /** name in { bridge, host, none }. */
  isDefault: boolean
}

export interface PublishedPort {
  containerId: string
  containerName: string
  stackName: string | null
  /** Compose service (com.docker.compose.service), or null for non-compose containers. */
  serviceName: string | null
  /** Container port. */
  privatePort: number
  /** Host port (null = exposed but not published). */
  publicPort: number | null
  /** tcp | udp */
  protocol: string
  /** "0.0.0.0" | "127.0.0.1" | "::" | specific host IP. */
  hostIp: string
  /** Server-derived: "public" (0.0.0.0/::) | "localhost" (127.0.0.1/::1) | "none". */
  exposure: string
}

export interface PortConflict {
  publicPort: number
  protocol: string
  hostIp: string
  /** ≥2 containers claiming the same host ip:port:proto. */
  containerNames: string[]
}

export interface HostSample {
  t: string
  cpuPercent: number | null
  memPercent: number | null
}

export interface HostMetrics {
  /** false when host /proc isn't mounted; all metric fields are then null. */
  available: boolean
  /** "host-proc-not-mounted" when unavailable, else null. */
  reason: string | null
  cpuPercent: number | null
  cpuCores: number | null
  loadAvg1: number | null
  loadAvg5: number | null
  memUsedBytes: number | null
  memTotalBytes: number | null
  memPercent: number | null
  diskUsedBytes: number | null
  diskTotalBytes: number | null
  diskPercent: number | null
  /** "host-rootfs" | "docker-df" | "unavailable" */
  diskSource: string
  sampledAt: string
  /** Ring, oldest → newest, for sparklines. */
  history: HostSample[]
}

export interface ContainerSample {
  t: string
  cpuPercent: number
  memUsedBytes: number
}

export interface ContainerMetrics {
  containerId: string
  containerName: string
  stackName: string | null
  /** 0–100 (can exceed 100 on multi-core; clamp display at cores*100). */
  cpuPercent: number
  memUsedBytes: number
  /** null when unlimited. */
  memLimitBytes: number | null
  memPercent: number | null
  /** false if the container isn't running (stats unavailable). */
  online: boolean
  history: ContainerSample[]
}

export interface StackSample {
  t: string
  cpuPercent: number
  memUsedBytes: number
}

export interface StackMetrics {
  /** compose project. */
  stackName: string
  /** Sum of member containers. */
  cpuPercent: number
  memUsedBytes: number
  containerCount: number
  /** Summed ring. */
  history: StackSample[]
}

/** `metrics.stacks` envelope: the ranking (CPU-desc, server-side) + the sample time. */
export interface StackMetricsResult {
  stacks: StackMetrics[]
  sampledAt: string
}

/** A historical time range for the `metrics.*` queries. Omit for the backend's live window. */
export interface MetricsRange {
  /** ISO-8601 start. */
  from: string
  /** ISO-8601 end. */
  to: string
  /** Server-side downsample bucket (bounds the returned point count). */
  stepSeconds: number
}

/** The three metrics backends (ADR-0013). */
export type MetricsBackend = 'memory' | 'sqlite' | 'influxdb'

/** InfluxDB connection values in the config surface. The token never leaves the server. */
export interface MetricsInfluxConfig {
  url: string | null
  org: string | null
  bucket: string | null
  /** True when a token is stored — the UI sends a new one only to replace it. */
  hasToken: boolean
  composeProjectTag: string
  diskMountpoint: string
}

/** `metrics.getConfig` / `metrics.updateConfig` payload: the effective backend configuration. */
export interface MetricsConfig {
  backend: MetricsBackend
  /** History window of the sqlite backend, in days (1–365). */
  retentionDays: number
  historyAvailable: boolean
  influx: MetricsInfluxConfig
  /** Config paths pinned by `WATCHTOWER__*` env vars (env wins) — those fields are read-only. */
  pinnedPaths: string[]
}

/** `metrics.updateConfig` request. Null influx fields keep the stored values (token included). */
export interface UpdateMetricsConfigRequest {
  backend: MetricsBackend
  retentionDays: number
  influxUrl?: string | null
  influxOrg?: string | null
  influxBucket?: string | null
  influxToken?: string | null
  influxComposeProjectTag?: string | null
  influxDiskMountpoint?: string | null
}

/** `networks.ports` envelope: the exposure map plus cross-container conflicts. */
export interface NetworkPortsResult {
  published: PublishedPort[]
  conflicts: PortConflict[]
}

/** `volumes.pruneOrphans` envelope. */
export interface PruneOrphansResult {
  removed: string[]
  reclaimedBytes: number | null
}

// ── Beacon request types ─────────────────────────────────────────────────────

export interface RecreateVolumesRequest {
  stackId: number
  volumeNames: string[]
}

/** Runtime-editable background-check toggles (Elarion settings-backed, live via IOptionsMonitor). */
export interface AutomationConfig {
  autoCheckEnabled: boolean
  autoCheckIntervalMinutes: number
  stackCheckEnabled: boolean
  stackCheckIntervalMinutes: number
  /** Periodic `docker image prune -f` equivalent — dangling (untagged) images only. */
  imagePruneEnabled: boolean
  imagePruneIntervalMinutes: number
  /** Config paths pinned by `WATCHTOWER__*` env vars (env wins) — those fields are read-only. */
  pinnedPaths: string[]
}

/** The values `system.updateAutomation` accepts (the response echoes them plus `pinnedPaths`). */
export type UpdateAutomationRequest = Omit<AutomationConfig, 'pinnedPaths'>

/** `system.getAuthConfig` / `system.updateAuthConfig` payload. */
export interface AuthConfig {
  /** The configured value — what the next start runs with. */
  enabled: boolean
  /** Whether the auth pipeline is enforcing in this process right now. */
  active: boolean
  /** True when `enabled` ≠ `active`: `Auth:Enabled` shapes the pipeline pre-DI, so it needs a restart. */
  restartRequired: boolean
  /** Central login hostname (bare host, no scheme). */
  host: string | null
  sessionLifetimeHours: number
  absoluteSessionLifetimeDays: number
  /** Config paths pinned by `WATCHTOWER__*` env vars (env wins) — those fields are read-only. */
  pinnedPaths: string[]
}

/** `system.updateAuthConfig` request. */
export interface UpdateAuthConfigRequest {
  enabled: boolean
  host: string | null
  sessionLifetimeHours: number
  absoluteSessionLifetimeDays: number
}

/** The two reverse-proxy backends (ADR-0015). */
export type ProxyProvider = 'caddy' | 'cloudflare'

/** Cloudflare Tunnel connection values (the API token never leaves the server). */
export interface ProxyCloudflareConfig {
  accountId: string | null
  zoneId: string | null
  /** True when a token is stored — the UI sends a new one only to replace it. */
  hasApiToken: boolean
  tunnelName: string
  /** Zero Trust team (bare name or full host) — derives the Access JWKS URL injected into deploys. */
  teamDomain: string | null
  /** True: Watchtower runs cloudflared as a managed container. False: the operator runs it. */
  managed: boolean
  cloudflaredImage: string
  /** Unmanaged mode: operator-run cloudflared container to connect to the ingress networks. */
  cloudflaredContainerName: string | null
  /** Comma-separated emails admitted by every Authenticated route's Access application. */
  accessAllowedEmails: string
  /** Comma-separated email domains admitted alongside `accessAllowedEmails`. */
  accessAllowedEmailDomains: string
  /** Comma-separated Zero Trust Access group ids admitted by Authenticated routes. */
  accessGroupIds: string
  /** Comma-separated reusable Access policy ids attached to Authenticated routes' apps. */
  accessReusablePolicyIds: string
}

/** `proxy.getConfig` / `proxy.updateConfig` payload. Fully runtime-switchable (no restart). */
export interface ProxyConfig {
  enabled: boolean
  provider: ProxyProvider
  /** ACME account email for certificate expiry notices (Caddy only). */
  adminEmail: string | null
  caddyImage: string
  cloudflare: ProxyCloudflareConfig
  /** Config paths pinned by `WATCHTOWER__*` env vars (env wins) — those fields are read-only. */
  pinnedPaths: string[]
}

/** `proxy.updateConfig` request. Null cloudflare fields keep the stored values (token included). */
export interface UpdateProxyConfigRequest {
  enabled: boolean
  provider: ProxyProvider
  adminEmail: string | null
  caddyImage: string
  cloudflareAccountId?: string | null
  cloudflareZoneId?: string | null
  cloudflareApiToken?: string | null
  cloudflareTunnelName?: string | null
  cloudflareTeamDomain?: string | null
  cloudflareManaged?: boolean | null
  cloudflaredImage?: string | null
  cloudflaredContainerName?: string | null
  cloudflareAccessAllowedEmails?: string | null
  cloudflareAccessAllowedEmailDomains?: string | null
  cloudflareAccessGroupIds?: string | null
  cloudflareAccessReusablePolicyIds?: string | null
}

// ── Reverse proxy (routes) ──────────────────────────────────────────────────

export type RouteStatus = 'pending' | 'awaitingdns' | 'active' | 'error'
export type DomainKind = 'managed' | 'custom'

export interface Route {
  id: number
  stackId: number
  stackName: string | null
  domain: string
  serviceName: string
  containerPort: number
  tlsEnabled: boolean
  isPrimary: boolean
  kind: DomainKind
  status: RouteStatus
  statusDetail: string | null
  /** ISO timestamp of the certificate expiry, when known. */
  certNotAfter: string | null
  createdAt: string
}

export interface CreateRouteRequest {
  stackId: number
  domain: string
  serviceName: string
  containerPort: number
  tlsEnabled: boolean
  isPrimary: boolean
  kind?: DomainKind | null
}

export interface UpdateRouteRequest {
  domain: string
  serviceName: string
  containerPort: number
  tlsEnabled: boolean
  isPrimary: boolean
}

/**
 * A route's access policy (docs/central-auth/design.md §3/§8). `Public` proxies every request as before;
 * `Authenticated` lets any signed-in user through; `Restricted` allows only the granted subjects — users
 * and groups alike. Mirrors the backend `AccessMode` enum, serialized by name.
 */
export type AccessMode = 'Public' | 'Authenticated' | 'Restricted'

/**
 * Which plaintext identity headers reach a protected upstream (docs/central-auth/design.md §2.3). The
 * signed `X-Watchtower-Jwt` assertion is always forwarded and is the source of truth; these opt a route
 * into ecosystem-standard plaintext headers for apps that read a username header instead. `None` (the
 * default) forwards the JWT only; `Remote` uses Authelia/Traefik `Remote-*`; `AuthRequest` uses
 * oauth2-proxy `X-Auth-Request-*`. Mirrors the backend `IdentityHeaderMode` enum, serialized by name.
 */
export type IdentityHeaderMode = 'None' | 'Remote' | 'AuthRequest' | 'Cloudflare'

/** The shape `proxy.setAccess` both accepts and returns — the policy itself, with nothing derived. */
export interface RouteAccess {
  mode: AccessMode
  /** Which plaintext identity headers reach the upstream; `None` forwards the signed JWT only. */
  identityHeaderMode: IdentityHeaderMode
  /** Newline-separated request-path prefixes exempt from access control; null when none. */
  bypassPaths: string | null
  /** Ids of the users granted through the route directly; only meaningful for `Restricted`. */
  grantedUserIds: number[]
  /**
   * Ids of the groups granted through the route; only meaningful for `Restricted`. Every member of a
   * granted group is let through, evaluated per request — so membership changes take effect immediately.
   */
  grantedGroupIds: number[]
}

/**
 * What `proxy.getAccess` returns: the policy plus the realm the route belongs to. The realm is derived
 * server-side (the stack's template category, or the operator realm for a standalone stack) and is
 * read-only — it is here so a grant editor can offer only the users and groups `proxy.setAccess` would
 * accept, rather than letting a cross-realm grant be composed and refused.
 */
export interface RouteAccessView extends RouteAccess {
  realmId: number
}

export interface DnsCheckResult {
  resolves: boolean
  addresses: string[]
}

export interface ProxyStatus {
  enabled: boolean
  /** Whether the active provider's data plane is running (name kept for wire compatibility). */
  caddyRunning: boolean
  routeCount: number
  provider: ProxyProvider
}

// ── Multi-tenancy (stack templates) ─────────────────────────────────────────

export interface StackTemplate {
  id: number
  name: string
  repositoryUrl: string
  composeFilePath: string
  branch: string
  credentialId: number | null
  domainPattern: string
  targetServiceName: string
  targetPort: number
  /** The realm every tenant of this category signs in to. Defaults to the operator realm. */
  realmId: number
  createdAt: string
  instanceCount: number
}

export interface TemplateEnvVar {
  id: number
  key: string
  value: string
}

export interface TemplateEnvVarInput {
  key: string
  value: string
}

export interface Tenant {
  stackId: number
  tenantSlug: string
  stackName: string
  domain: string | null
  lastDeployStatus: string | null
  lastDeployedAt: string | null
}

/**
 * One stack allowed to drive this template through the public Management API (`/api/mgmt/*`)
 * with its App-API token. Mirrors the backend `TemplateGrantDto`.
 */
export interface TemplateGrant {
  /** The granted stack (the caller, typically a vendor's central-management UI). */
  stackId: number
  stackName: string
  /** Additionally permits deprovisioning tenants of this template. */
  allowDelete: boolean
  createdAt: string
}

export interface CreateTemplateRequest {
  name: string
  repositoryUrl: string
  composeFilePath: string
  branch: string
  credentialId?: number | null
  domainPattern: string
  targetServiceName: string
  targetPort: number
  baseEnvVars?: TemplateEnvVarInput[] | null
  /** Omit for the operator realm. On update the server refuses a move once the category has tenants. */
  realmId?: number | null
}

export type UpdateTemplateRequest = CreateTemplateRequest

export interface AddTenantRequest {
  templateId: number
  slug: string
  envOverrides?: TemplateEnvVarInput[] | null
}

/**
 * A Watchtower account, as the Users admin screen sees it. Mirrors the backend `UserDto`, which
 * deliberately carries no password hash or security stamps.
 */
export interface User {
  id: number
  userName: string
  email: string | null
  /** Holds the Admin role: user management and system configuration. */
  isAdmin: boolean
  /** Suspended: the account exists but may neither sign in nor pass access verification. */
  disabled: boolean
  /** Temporarily locked by the brute-force counter. Derived server-side from the lockout deadline. */
  lockedOut: boolean
  /**
   * Whether the account demands an authenticator code after its password. An administrator can only ever
   * take a second factor away (`users.resetMfa`), never add one — enrolling needs a code that only the
   * account's owner can produce.
   */
  twoFactorEnabled: boolean
  /** The population the account belongs to; its user name is only unique within it. Immutable. */
  realmId: number
  createdAt: string
}

export interface CreateUserRequest {
  userName: string
  password: string
  email?: string | null
  isAdmin: boolean
  /** Omit for the operator realm. Only an operator-realm account may hold the Admin role. */
  realmId?: number | null
}

export interface UpdateUserRequest {
  userName: string
  email?: string | null
  isAdmin: boolean
}

/**
 * A named set of accounts that a route can be granted to, as the Groups admin screen sees it. Mirrors the
 * backend `GroupDto`.
 *
 * The name is not just a label: it is forwarded to protected apps in the `Remote-Groups` /
 * `X-Auth-Request-Groups` header and in the JWT's `groups` claim, so a group-aware app maps it onto its
 * own roles. That is why the backend constrains it to printable ASCII without commas — the client shows
 * the refusal rather than pre-validating a rule that has to hold server-side anyway.
 */
export interface Group {
  id: number
  name: string
  /** The population the group belongs to; it may only ever hold members of that same realm. */
  realmId: number
  /** Derived per read rather than stored — a counter could disagree with the membership rows. */
  memberCount: number
}

// ── Realms ───────────────────────────────────────────────────────────────────

/**
 * A user population with its own credential space and its own login host — Watchtower's answer to a
 * Keycloak realm (docs/central-auth/design.md §13). Every user, group and template belongs to exactly
 * one; the built-in **operator** realm owns this management UI and cannot be deleted.
 *
 * The three counts are what the delete guard is made of: a realm that anything still belongs to is
 * refused by the server with `Conflict`, so the client can say so before the click rather than after.
 */
export interface Realm {
  id: number
  name: string
  /** URL-safe identifier, chosen at creation and immutable afterwards. */
  slug: string
  /** The host this realm's login page answers on; null until DNS for it is ready. */
  authHost: string | null
  /** The operator realm: renameable, never deletable, and its auth host stays the configured `Auth:Host`. */
  isSystem: boolean
  userCount: number
  groupCount: number
  templateCount: number
  createdAt: string
}

export interface CreateRealmRequest {
  name: string
  slug: string
  authHost?: string | null
}

/**
 * A partial update: an omitted field is left alone, so renaming a realm never has to restate its auth
 * host. An empty-string `authHost` clears it — that is how "this realm has no login host yet" is said.
 */
export interface UpdateRealmRequest {
  name?: string | null
  authHost?: string | null
}

// ── Backups (ADR-0016) ───────────────────────────────────────────────────────

export type BackupProvider = 'sftp' | 'local'

/** SFTP connection values for the backup storage (secrets never leave the server). */
export interface BackupSftpConfig {
  host: string | null
  port: number
  username: string | null
  /** True when a password is stored — the UI sends a new one only to replace it. */
  hasPassword: boolean
  /** True when a private key is stored — the UI sends a new one only to replace it. */
  hasPrivateKey: boolean
  basePath: string
}

/** `backups.getConfig` / `backups.updateConfig` payload. Fully runtime-switchable (no restart). */
export interface BackupConfig {
  enabled: boolean
  /** Server-local daily window, "HH:mm". */
  time: string
  instanceName: string | null
  /** What the instance name resolves to when unset (the machine name). */
  resolvedInstanceName: string
  /** Backups older than this many days are pruned; 0 keeps forever. */
  retentionDays: number
  /** Keep at most this many backups per stack; 0 is unlimited. */
  retentionMaxCount: number
  /** True when an encryption passphrase is stored — archives are OpenSSL-enc encrypted. */
  hasEncryptionPassphrase: boolean
  helperImage: string
  provider: BackupProvider
  sftp: BackupSftpConfig
  localBasePath: string
  /** Config paths pinned by `WATCHTOWER__*` env vars (env wins) — those fields are read-only. */
  pinnedPaths: string[]
}

/** `backups.updateConfig` request. Null secret fields keep the stored values; empty string clears. */
export interface UpdateBackupConfigRequest {
  enabled: boolean
  time: string
  instanceName?: string | null
  retentionDays: number
  retentionMaxCount: number
  helperImage: string
  provider: BackupProvider
  encryptionPassphrase?: string | null
  sftpHost?: string | null
  sftpPort?: number | null
  sftpUsername?: string | null
  sftpPassword?: string | null
  sftpPrivateKey?: string | null
  sftpPrivateKeyPassphrase?: string | null
  sftpBasePath?: string | null
  localBasePath?: string | null
}

/** One backup run, for the history views. */
export interface BackupEvent {
  id: number
  stackId: number
  stackName: string
  triggeredBy: string
  status: 'queued' | 'running' | 'success' | 'failed'
  /** Provider-relative path of the uploaded archive (null until upload, and on failure). */
  remotePath: string | null
  /** Uploaded size in bytes (after compression/encryption). */
  sizeBytes: number | null
  output: string | null
  startedAt: string
  finishedAt: string | null
}

/** A stack's backup participation. */
export interface BackupStackConfig {
  stackId: number
  /** Included in the daily schedule. */
  enabled: boolean
  /** Stop the stack's containers during the snapshot for consistency (ADR-0016 §2). */
  stopContainers: boolean
}

export interface BackupRunAccepted {
  backupEventId: number
  status: string
}

/** One archive present on the backup storage — the restore picker's row. */
export interface BackupRemoteFile {
  name: string
  sizeBytes: number
  /** ISO timestamp parsed from the archive name (UTC). */
  takenAt: string
  encrypted: boolean
}

// ── CI runners ───────────────────────────────────────────────────────────────

/** One detected toolchain of a CI repo, e.g. kind "dotnet", version "10.0", source "workflow". */
export interface CiToolchain {
  kind: string
  version: string
  source: string
}

/**
 * The toolchain profile detected from the repository's working tree during stack deploys, plus the
 * toolcache warm state derived from it. Null on a `CiRepo` until a linked stack has deployed once.
 */
export interface CiToolchainProfile {
  toolchains: CiToolchain[]
  hasDockerfile: boolean
  detectedAt: string | null
  /** 'warmed' | 'warming' | 'failed' | 'pending' — whether the toolcache matches the profile. */
  warmStatus: 'warmed' | 'warming' | 'failed' | 'pending'
  lastWarmedAt: string | null
  lastWarmError: string | null
}

/** Live orchestrator state for one repo's runner slots. */
export interface CiRunnerStatus {
  desiredRunners: number
  runningRunners: number
  totalSpawned: number
  lastError: string | null
  lastErrorAt: string | null
  backoffUntil: string | null
}

/** A GitHub repository with CI runners managed by this Watchtower instance. */
export interface CiRepo {
  id: number
  owner: string
  name: string
  fullName: string
  credentialId: number
  enabled: boolean
  maxConcurrentRunners: number
  runnerImage: string | null
  extraLabels: string | null
  allowDockerSocket: boolean
  createdAt: string
  runnerStatus: CiRunnerStatus | null
  toolchain: CiToolchainProfile | null
}

/**
 * The CI view of one stack: whether its repository is on github.com (only those can get Actions
 * runners) and the linked CI repo when enabled. Stacks deploying the same repository share one
 * CI repo — one runner pool, one toolcache.
 */
export interface StackCi {
  isGitHub: boolean
  owner: string | null
  name: string | null
  repo: CiRepo | null
}
