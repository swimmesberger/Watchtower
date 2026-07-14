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
  imageName: string | null
  credentialId: number | null
  composeFilePath: string | null
  composeProjectName: string | null
  detectedImageName: string | null
  detectedComposeFilePath: string | null
  detectedComposeProjectName: string | null
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
  imageName?: string | null
  credentialId?: number | null
  composeFilePath?: string | null
  composeProjectName?: string | null
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

export interface DnsCheckResult {
  resolves: boolean
  addresses: string[]
}

export interface ProxyStatus {
  enabled: boolean
  caddyRunning: boolean
  routeCount: number
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
}

export type UpdateTemplateRequest = CreateTemplateRequest

export interface AddTenantRequest {
  templateId: number
  slug: string
  envOverrides?: TemplateEnvVarInput[] | null
}
