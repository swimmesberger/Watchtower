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
  lastDeployStatus: 'success' | 'failed' | 'running' | 'queued' | null
  lastDeployedAt: string | null
  createdAt: string
  /** True when at least one container image has a newer version available. Null when never checked. */
  hasUpdates: boolean | null
  /** Image names that have a newer version available. Null when never checked. */
  outdatedImages: string[] | null
  /** ISO timestamp of the last update check. Null when never checked. */
  updatesCheckedAt: string | null
}

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

export interface DockerConfigStatus {
  /** True when config.json exists at the resolved path inside the container. */
  exists: boolean
  /** Absolute path inside the container that was checked. */
  path: string
  /** "WATCHTOWER_DOCKER_CONFIG" | "DOCKER_CONFIG" | "default" */
  source: string
}
