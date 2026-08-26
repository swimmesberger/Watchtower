// TypeScript types matching the C# backend response records.

export interface Registry {
  id: number
  name: string
  url: string
  credentialId: number | null
  credentialName: string | null
  createdAt: string
}

/**
 * A read-only registry entry from the host docker config (managed by `docker login` on the host).
 * Usable for pulls and as a CI sync source; not editable in Watchtower. Username is null for
 * credential-helper entries.
 */
export interface HostRegistry {
  url: string
  username: string | null
}

export interface Credential {
  id: number
  name: string
  username: string
  createdAt: string
}

/**
 * Which update mechanism a product's deployments use (ADR-0026 §"Two modes, one switch"). `git` is the
 * default and every migrated product: branch-HEAD clones and registry polling. `releases` is flipped on
 * by the first accepted release: a deploy is one release's commit plus its image digests.
 *
 * **This is the binary the UI hangs on (invariant 4):** the Updates panel renders in `git` mode, the
 * Version panel in `releases` mode, never both.
 */
export type ReleaseMode = 'git' | 'releases'

/**
 * A git repository Watchtower can deploy — its compose file, default branch and clone credential
 * (ADR-0026). Every stack and every template references one; the source fields they expose are
 * read-only projections of this.
 */
export interface Product {
  id: number
  name: string
  description: string | null
  repositoryUrl: string
  composeFilePath: string
  defaultBranch: string
  credentialId: number | null
  credentialName: string | null
  createdAt: string
  /** How many stacks deploy this product. */
  stackCount: number
  /** How many templates instantiate it, each with its own tenants. */
  templateCount: number
  /** Whether this product's CI may report releases to the webhook. */
  releaseWebhookEnabled: boolean
  /** The newest release, or null while the product has none. */
  latestRelease: ProductReleaseSummary | null
  /** `git` until the first release flips it to `releases`; operator-revertible. */
  releaseMode: ReleaseMode
}

/** The newest release of a product, as much as a header line or a catalogue row needs. */
export interface ProductReleaseSummary {
  id: number
  version: string
  createdAt: string
}

/** How a release arrived. */
export type ReleaseSource = 'webhook' | 'manual'

/** One row of the Releases tab. The digests live behind the row expansion (`products.getRelease`). */
export interface Release {
  id: number
  version: string
  commitSha: string | null
  branch: string
  createdVia: ReleaseSource
  createdAt: string
  /** When the build itself was published, if the reporter said. Display only — the list is ordered by id. */
  publishedAt: string | null
  sourceRunUrl: string | null
  imageCount: number
}

/** One image a release pins. */
export interface ReleaseImage {
  repository: string
  tag: string | null
  digest: string
}

/** The expanded release: its images and its notes. */
export interface ReleaseDetail {
  id: number
  productId: number
  productName: string
  version: string
  commitSha: string | null
  branch: string
  createdVia: ReleaseSource
  createdAt: string
  publishedAt: string | null
  sourceRunUrl: string | null
  notes: string | null
  images: ReleaseImage[]
}

/** `products.listReleases`: one keyset page, newest first. */
export interface ReleasePage {
  releases: Release[]
  /** Whether an older page exists — what "Show older" keys on. */
  hasMore: boolean
}

/** `products.createRelease`: recording a build by hand. */
export interface CreateReleaseRequest {
  version: string
  commitSha?: string | null
  images: string[]
  notes?: string | null
}

/** `products.rotateReleaseToken`: the new token, and the webhook it just enabled. */
export interface ReleaseTokenRotation {
  enabled: boolean
  token: string
}

/**
 * `products.setReleaseWebhook`. No token: enabling may have generated one, and the value is read from
 * `products.get` — the one place it is served.
 */
export interface ReleaseWebhookState {
  enabled: boolean
}

/** One stack deploying a product, as `products.get` rosters it. */
export interface ProductStack {
  id: number
  name: string
  /** The effective branch: the stack's override when it has one, else the product default. */
  branch: string
  branchOverride: string | null
  templateId: number | null
  /** Set when the stack is a tenant of `templateId`; null for standalone stacks. */
  tenantSlug: string | null
  lastDeployStatus: 'success' | 'failed' | 'running' | 'queued' | null
  lastDeployedAt: string | null
}

/** One template instantiating a product, as `products.get` rosters it. */
export interface ProductTemplate {
  id: number
  name: string
  branch: string
  branchOverride: string | null
  tenantCount: number
}

/** `products.get`: the product plus everything that deploys it. */
export interface ProductDetail {
  product: Product
  stacks: ProductStack[]
  templates: ProductTemplate[]
  /**
   * The release webhook bearer, or null when none has been generated. Only on the detail response —
   * the catalogue lists every product and must not carry every product's secret.
   */
  releaseWebhookToken: string | null
}

export interface CreateProductRequest {
  name: string
  repositoryUrl: string
  composeFilePath: string
  defaultBranch: string
  description?: string | null
  credentialId?: number | null
}

export type UpdateProductRequest = CreateProductRequest

export interface Stack {
  id: number
  name: string
  /** The product this stack is a running copy of. */
  productId: number
  productName: string
  repositoryUrl: string
  composeFilePath: string
  /** The effective branch — `branchOverride` when set, else the product's default. */
  branch: string
  /** Set only when this stack deploys a branch other than the one it would inherit. */
  branchOverride: string | null
  composeProjectName: string
  credentialId: number | null
  webhookToken: string | null
  webhookEnabled: boolean
  /** Pull-based deployment: redeploy automatically when polling detects something new. */
  autoDeployMode: AutoDeployMode
  /** Local time of day ("HH:mm") for scheduled auto-deploy. Null unless mode is 'scheduled'. */
  autoDeployTime: string | null
  /** Operator intent: a 'stopped' stack is disabled — containers stopped, deploys rejected. */
  desiredState: StackDesiredState
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
  /** The product's update mechanism — the switch between the Updates and Version panels (invariant 4). */
  releaseMode: ReleaseMode
  /** Derived from the pin, not stored: `pinned` when `pinnedRelease` is set, else `latest`. */
  trackingMode: TrackingMode
  /** The release this stack is pinned to, or null when it tracks latest. */
  pinnedRelease: StackReleaseRef | null
  /** The release the last successful deploy applied, when there was one. */
  lastDeployedRelease: StackReleaseRef | null
  /**
   * From the cached update check: the newest release when it differs from `lastDeployedRelease`.
   * Computed for pinned stacks too — that is what the "behind" chip counts against.
   */
  availableReleaseId: number | null
  /** Its version label, denormalized so a list renders it without a second call. */
  availableReleaseVersion: string | null
  /** Running containers not on the deployed release's digests. Null when never checked. */
  driftedContainers: string[] | null
}

/** Whether a stack follows the newest release or stays on one it was pinned to. */
export type TrackingMode = 'latest' | 'pinned'

/** A release named on a stack: enough for a chip, not enough to need a second call. */
export interface StackReleaseRef {
  id: number
  version: string
}

/**
 * `stacks.setRelease`. `deployed` is false when the caller asked for no deploy **and** when the stack is
 * stopped — a stopped stack is pinned successfully and simply not deployed, which is a result to show,
 * not an error.
 */
export interface SetStackReleaseResult {
  stack: Stack
  deployed: boolean
  deployEventId: number | null
}

/** `products.deployRelease`: what the roll-out actually targeted. */
export interface DeployReleaseResult {
  releaseId: number
  version: string
  /** Latest-tracking, running stacks only — pinned and stopped ones are skipped. */
  stacksEnqueued: number
  deployEventIds: number[]
}

export type AutoDeployMode = 'off' | 'onChange' | 'scheduled'

export type StackDesiredState = 'running' | 'stopped'

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
  /**
   * An existing product to deploy. When set the repository fields must be left empty — the product
   * owns them — and `branch` becomes a per-stack override if it differs from the product default.
   */
  productId?: number | null
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

/**
 * The three metrics backends (ADR-0013). `database` was spelled `sqlite` before ADR-0024 replaced the
 * file with PostgreSQL; the server still accepts the old value on read, but never sends it.
 */
export type MetricsBackend = 'memory' | 'database' | 'influxdb'

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
  /** History window of the database backend, in days (1–365). */
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
  /**
   * Fallback login hostname for the operator realm (bare host, no scheme). Since ADR-0023 the login host
   * is normally a `watchtower` route; this is read only while no route is marked as one.
   */
  host: string | null
  sessionLifetimeHours: number
  absoluteSessionLifetimeDays: number
  /** Config paths pinned by `WATCHTOWER__*` env vars (env wins) — those fields are read-only. */
  pinnedPaths: string[]
  /** Where the operator realm actually redirects anonymous visitors now. Read-only; set on Routes. */
  effectiveLoginHost?: string | null
}

/** `system.updateAuthConfig` request. */
export interface UpdateAuthConfigRequest {
  enabled: boolean
  host: string | null
  sessionLifetimeHours: number
  absoluteSessionLifetimeDays: number
}

/** The reverse-proxy backends. See ADR-0015 and ADR-0022. */
export type ProxyProvider = 'caddy' | 'cloudflare' | 'yarp'

/**
 * In-process proxy + ACME values (the EAB HMAC key never leaves the server). Certificates and the ACME
 * account are rows in the database since ADR-0024, so there is no directory to report.
 */
export interface ProxyYarpConfig {
  /** Container port the plain-HTTP ingress listener binds; 0 turns it off. Applied without a restart. */
  httpPort: number
  /** Container port the TLS ingress listener binds; 0 turns it off. Applied without a restart. */
  httpsPort: number
  acmeDirectoryUrl: string
  /** Extra PEM roots trusted when talking to the ACME directory (an internal CA). */
  acmeCaBundlePath: string | null
  /** External Account Binding key id, for CAs that require one. */
  acmeEabKeyId: string | null
  /** True when an EAB HMAC key is stored — the UI sends a new one only to replace it. */
  hasAcmeEabHmacKey: boolean
  redirectHttpToHttps: boolean
  /** Runtime state: false means nothing is terminating TLS and routes are served in the clear. */
  httpsListenerBound: boolean
}

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
  /** ACME account email for certificate expiry notices (the certificate-issuing providers). */
  adminEmail: string | null
  caddyImage: string
  yarp: ProxyYarpConfig
  cloudflare: ProxyCloudflareConfig
  /** Config paths pinned by `WATCHTOWER__*` env vars (env wins) — those fields are read-only. */
  pinnedPaths: string[]
}

/** `proxy.updateConfig` request. Null provider fields keep the stored values (secrets included). */
export interface UpdateProxyConfigRequest {
  enabled: boolean
  provider: ProxyProvider
  adminEmail: string | null
  caddyImage: string
  yarpHttpPort?: number | null
  yarpHttpsPort?: number | null
  yarpAcmeDirectoryUrl?: string | null
  yarpAcmeCaBundlePath?: string | null
  yarpAcmeEabKeyId?: string | null
  yarpAcmeEabHmacKey?: string | null
  yarpRedirectHttpToHttps?: boolean | null
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

/**
 * What a route's hostname is served by (ADR-0023). `service` forwards it to a container inside a stack;
 * `watchtower` means this instance serves the hostname itself — its UI and API, and for the realm's login
 * route, its login page.
 */
export type RouteTarget = 'service' | 'watchtower'

export interface Route {
  id: number
  /** Null on a `watchtower` route, which forwards nowhere. */
  stackId: number | null
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
  target: RouteTarget
  /** The realm a `watchtower` route serves; null on a `service` route. */
  realmId: number | null
  realmSlug: string | null
  /** Whether that realm redirects its anonymous visitors to this hostname. */
  isLoginRoute: boolean
}

export interface CreateRouteRequest {
  stackId: number
  domain: string
  serviceName: string
  containerPort: number
  tlsEnabled: boolean
  isPrimary: boolean
  kind?: DomainKind | null
  /** Omitted means `service` — the only kind of route that existed before ADR-0023. */
  target?: RouteTarget | null
  /** `watchtower` routes only; defaults to the operator realm. */
  realmId?: number | null
  /** `watchtower` routes only; omitted means "yes, if the realm has no login host yet". */
  makeLoginRoute?: boolean | null
}

export interface UpdateRouteRequest {
  domain: string
  serviceName: string
  containerPort: number
  tlsEnabled: boolean
  isPrimary: boolean
  kind?: DomainKind | null
  /** `watchtower` routes only: designate (true) or release (false) this realm's login host. */
  makeLoginRoute?: boolean | null
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

/**
 * One host's certificate state under the in-process proxy. Every served host has a route row since
 * ADR-0023, Watchtower's own hostnames included; `source: 'orphan'` is a certificate still on disk for a
 * host nothing routes to any more.
 */
export interface CertificateInfo {
  host: string
  /** `route` — a routed domain; `orphan` — nothing routes here any more. */
  source: 'route' | 'orphan'
  routeId?: number | null
  /** `active` means a certificate is being served, whatever the last renewal attempt did. */
  state: 'none' | 'pending' | 'active' | 'awaitingDns' | 'error'
  notBefore?: string | null
  notAfter?: string | null
  issuer?: string | null
  lastAttemptAt?: string | null
  /** Why the last attempt failed. Present alongside `active` when a *renewal* failed. */
  lastError?: string | null
  /** When the scheduler will try again — a renewal when healthy, a backoff rung after a failure. */
  nextAttemptAt?: string | null
  consecutiveFailures: number
}

/**
 * A public hostname configured on the Cloudflare tunnel (dashboard-made) that Watchtower's route
 * table doesn't know. Preserved verbatim by the reconcile; importable as a route, with a heuristic
 * stack/service/port suggestion when the service URL follows Watchtower's own alias convention.
 */
export interface CloudflareForeignRoute {
  hostname: string
  service: string
  path?: string | null
  /** The tunnel the rule lives on — every account tunnel is scanned, not just Watchtower's own. */
  tunnelName: string
  suggestedStackId?: number | null
  suggestedStackName?: string | null
  suggestedServiceName?: string | null
  suggestedContainerPort?: number | null
}

/**
 * One entry of the audit trail (`audit.listEvents`) — the instance's single record of what happened:
 * what users did (`auth`, `access`, `users`, `groups`, `realms`) and what Watchtower did
 * (`proxy.cloudflare`, `backups`, `system`, `metrics`, …). Reference-free: subjects are named, so the
 * row outlives the account, app or tunnel it mentions.
 */
export interface AuditEvent {
  id: number
  at: string
  /** The plane the event belongs to, e.g. `proxy.cloudflare`, `auth`, `backups`. */
  category: string
  /** What happened, e.g. `tunnel.config.push`, `login.failed`, `user.created`, `config.update`. */
  action: string
  /** What it acted on — an account, a hostname, a stack, a settings surface. */
  target: string
  detail?: string | null
  /** The acting user's name; null for background work and startup hooks — rendered as "system". */
  actor?: string | null
  /** False for a failed write, a rejected login or a refused access. */
  success: boolean
  error?: string | null
}

/**
 * One page of the trail, newest first. `nextBeforeId` is a keyset cursor, not an offset: the trail is
 * append-only and is being written while it is read, so an offset page would shift under the reader.
 * Null means this was the last page.
 */
export interface AuditEventPage {
  events: AuditEvent[]
  nextBeforeId: number | null
}

/** Filters and cursor for one `audit.listEvents` call. Every field is optional and the filters are ANDed. */
export interface AuditQuery {
  /** Category prefix — `proxy` matches `proxy.cloudflare`; one of the values `audit.listFacets` reports. */
  category?: string | null
  /** Exact action match. */
  action?: string | null
  /** Exact actor match; `system` selects rows without one. */
  actor?: string | null
  /** Return only rows older than this id — the previous page's `nextBeforeId`. */
  beforeId?: number | null
  /** Page size; the server defaults to 100 and clamps to 500. */
  limit?: number | null
}

/** The distinct values the trail contains, for the filter dropdowns. */
export interface AuditFacets {
  categories: string[]
  actions: string[]
  actors: string[]
}

export interface ProxyStatus {
  enabled: boolean
  /** Whether the active provider's data plane is running (name kept for wire compatibility). */
  caddyRunning: boolean
  routeCount: number
  provider: ProxyProvider
  /** A provider-specific caveat worth showing next to the status, or null. */
  providerDetail: string | null
}

// ── Multi-tenancy (stack templates) ─────────────────────────────────────────

export interface StackTemplate {
  id: number
  name: string
  /** The product every tenant of this template deploys. */
  productId: number
  productName: string
  repositoryUrl: string
  composeFilePath: string
  /** The effective branch — `branchOverride` when set, else the product's default. */
  branch: string
  /** Set only when this template's tenants deploy a branch other than the product default. */
  branchOverride: string | null
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
  /** An existing product to instantiate. When set the repository fields must be left empty. */
  productId?: number | null
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
  /** The operator realm: renameable, never deletable, and the only one `Auth:Host` is a fallback for. */
  isSystem: boolean
  userCount: number
  groupCount: number
  templateCount: number
  createdAt: string
  /** The `watchtower` route this realm's login page is served on; null when it has none (ADR-0023). */
  loginRouteId: number | null
  /** That route's domain, or — on the operator realm alone — the configured `Auth:Host` fallback. */
  loginHost: string | null
}

export interface CreateRealmRequest {
  name: string
  slug: string
  /**
   * Creates a `watchtower` route for this hostname and makes it the realm's login host. There is no
   * "pick an existing route" here on purpose: a `watchtower` route belongs to a realm, so none can exist
   * for a realm that does not yet. Designating one is `realms.update`'s job.
   */
  loginDomain?: string | null
}

/**
 * A partial update: an omitted field is left alone, so renaming a realm never has to restate its login
 * route. A `loginRouteId` of `0` clears it — that is how "this realm has no login host" is said.
 */
export interface UpdateRealmRequest {
  name?: string | null
  loginRouteId?: number | null
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
  /** Five-field cron (`minute hour day-of-month month day-of-week`), server-local wall clock. */
  cron: string
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
  /** Five-field cron (`minute hour day-of-month month day-of-week`), server-local wall clock. */
  cron: string
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

/**
 * How a stack's stateful containers are quiesced for the snapshot (ADR-0019): `stop` (SIGTERM,
 * restart afterwards — application-consistent) or `pause` (cgroup freeze for the tar, unpause —
 * milliseconds of downtime, but only crash-consistent).
 */
export type BackupQuiesceMode = 'stop' | 'pause'

/** A stack's backup participation. */
export interface BackupStackConfig {
  stackId: number
  /** Included in the backup schedule. */
  enabled: boolean
  /** Quiesce the stack's stateful containers during the snapshot for consistency (ADR-0016 §2). */
  stopContainers: boolean
  /** This stack's schedule override; null follows the instance-wide schedule. */
  cron: string | null
  /** How unlabelled stateful containers are quiesced when `stopContainers` is on. */
  quiesceMode: BackupQuiesceMode
}

/**
 * Per-service backup settings configured in the UI (ADR-0022), in the compose labels' own value
 * syntax — `exclude` stands in for `watchtower.backup.exclude=true`, `stop` for `watchtower.backup.stop`,
 * `dump` for `watchtower.backup.dump`. A label on the deployed service always wins.
 */
export interface BackupServiceOverride {
  service: string
  exclude: boolean
  /** Omitted on the wire when not set. */
  stop?: 'true' | 'false' | 'pause' | null
  /** Omitted on the wire when not set. */
  dump?: 'false' | 'postgres' | null
}

/** What the next backup run would do with one container. */
export type BackupServiceAction = 'stop' | 'pause' | 'keep' | 'dump' | 'excluded' | 'notRunning'

/** Where a per-service decision came from: the mount rule / stack default, a compose label, or a UI override. */
export type BackupSettingSource = 'default' | 'label' | 'override'

/** One row of the backup plan preview. */
export interface BackupServicePreview {
  service: string
  /** Absent for an override whose service is not deployed right now. */
  container?: string | null
  state: 'running' | 'not running' | 'absent' | string
  volumes: string[]
  action: BackupServiceAction
  reason: string
  source: BackupSettingSource
  /** The raw compose labels; omitted on the wire when the service carries none. */
  excludeLabel?: string | null
  stopLabel?: string | null
  dumpLabel?: string | null
  /** Omitted on the wire when the service has no override. */
  override?: BackupServiceOverride | null
}

/** The dry run of a backup for a stack as deployed right now (ADR-0022). */
export interface BackupPlanPreview {
  deployed: boolean
  volumes: string[]
  excludedVolumes: { name: string; reason: 'label' | 'dump'; detail: string }[]
  services: BackupServicePreview[]
  warnings: string[]
  /** The UI overrides rendered as compose labels to paste; omitted when there are none. */
  labelSnippet?: string | null
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

/**
 * State of the registry -> GitHub Actions sync (REGISTRY variable + REGISTRY_USERNAME /
 * REGISTRY_PASSWORD secrets). Null on a `CiRepo` until a sync registry is selected.
 */
export interface CiRegistrySync {
  /** 'synced' | 'pending' (push not attempted yet or values changed) | 'failed'. */
  status: 'synced' | 'pending' | 'failed'
  syncedAt: string | null
  error: string | null
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
  syncRegistryUrl: string | null
  registrySync: CiRegistrySync | null
}

/**
 * The CI view of one product: whether its repository is on github.com (only those can get Actions
 * runners) and the linked CI repo when enabled. Products over the same repository share one CI
 * repo — one runner pool, one toolcache — as do all the stacks deploying them.
 */
export interface CiLink {
  isGitHub: boolean
  owner: string | null
  name: string | null
  repo: CiRepo | null
}
